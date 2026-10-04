using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.SharedKernel;
using Gecko.Tos.Application;
using Gecko.Tos.Domain;
using Gecko.Tos.Infrastructure.Persistence;
using Gecko.Tos.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Endpoints.Holds;

/// <summary>
/// Holds (PLAN §4.3, decision D-6, batch C). A hold is a ROW with an apply and a
/// release, each carrying a person and a reason — never a bit. That is the whole
/// design, and it is what Vector did not have:
///   IsHold contradicts the status on 647 boxes      → nothing to contradict here
///   1,483 held boxes already gated out              → the barrier reads active rows
///   no author, no reason, no history                → all three are NOT NULL
///
/// MDM owns the hold VOCABULARY — what a hold blocks (<c>blocking_scope</c>) and
/// who may lift it (<c>release_authority</c>). This module owns the holds
/// themselves, and maps that authority onto a permission (<see cref="HoldRules"/>).
/// </summary>
internal static class HoldEndpoints
{
    public static RouteGroupBuilder MapHoldEndpoints(this RouteGroupBuilder tos)
    {
        var holds = tos.MapGroup("/holds").WithTags("TOS — holds");

        holds.MapGet("/", ListAsync).RequireBranchPermission(TosPermissions.HoldView).WithSummary("List holds, active or released");
        holds.MapGet("/{id:guid}", GetAsync).RequireBranchPermission(TosPermissions.HoldView).WithName("GetContainerHold").WithSummary("One hold with its MDM definition");
        holds.MapPost("/", ApplyAsync).RequireBranchPermission(TosPermissions.HoldApply).Validate<ApplyHoldRequest>().WithSummary("Place a hold on a box or a booking");
        holds.MapPost("/{id:guid}/release", ReleaseAsync).RequireAuthorization().Validate<ReleaseHoldRequest>()
            .WithSummary("Release a hold — needs the permission its release authority maps to")
            .WithDescription("DEPOT_FINANCE needs tos.hold.release.finance, LINE needs tos.hold.release.line, everything else tos.hold.release.operations. CUSTOMS and MNR holds also need the reference of the document you are acting on.");

        holds.MapHoldBoardEndpoints();

        // The barrier's question, in the barrier's terms: give it a number, it says
        // what stops the box. Phase 5 answers this from an in-process path; the
        // endpoint exists now so the yard screen and the demo can ask it.
        tos.MapGet("/containers/{containerNo}/holds", ForContainerAsync)
            .RequireBranchPermission(TosPermissions.HoldView)
            .WithSummary("Every hold that applies to a box right now, its own and its booking's");

        return tos;
    }

    // ── reads ───────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<PagedResult<HoldResponse>>, ValidationProblem>> ListAsync(
        [AsParameters] ListQuery query, TosDbContext db, IMasterDataReferences master, BranchClock clock,
        ICallerPermissions scope, CancellationToken ct,
        string? containerNo = null, Guid? bookingId = null, string? holdCode = null, string? status = null)
    {
        var rows = db.ContainerHolds.AsNoTracking();

        if (ContainerNumber.Normalise(containerNo ?? "") is { Length: > 0 } box) rows = rows.Where(h => h.ContainerNo == box);
        if (bookingId is not null) rows = rows.Where(h => h.BookingId == bookingId);
        if (holdCode.Clean() is { } code) rows = rows.Where(h => h.HoldCode == code);

        switch (status.Clean() ?? "ACTIVE")
        {
            case "ACTIVE": rows = rows.Where(h => h.ReleasedAt == null); break;
            case "RELEASED": rows = rows.Where(h => h.ReleasedAt != null); break;
            case "ALL": break;
            default: return TosSupport.Invalid("status", "Use ACTIVE, RELEASED or ALL.");
        }

        if (query.Search.Clean() is { } q)
            rows = rows.Where(h => h.ContainerNo!.Contains(q) || h.HoldCode.Contains(q) || h.ExternalRef!.Contains(q));

        // A hold on a BOOKING belongs to that booking's depot, so it is scoped like
        // one. A hold on a BOX is not: the box travels, and a line or customs hold
        // follows it into whichever depot it turns up at.
        var projected =
            from h in rows
            join b in db.Bookings on h.BookingId equals b.BookingId into bookings
            from b in bookings.DefaultIfEmpty()
            select new { h, OrderNo = b == null ? null : b.OrderNo, BranchId = b == null ? (Guid?)null : b.BranchId };

        if (scope.BranchFilter(TosPermissions.HoldView) is { } mine)
        {
            var allowed = mine.ToList();
            projected = projected.Where(x => x.BranchId == null || allowed.Contains(x.BranchId.Value));
        }

        var page = await projected.OrderByDescending(x => x.h.AppliedAt).ToPagedAsync(query.Page, query.PageSize, ct);

        var definitions = await master.HoldsAsync(page.Items.Select(x => x.h.HoldCode), ct);
        var branches = await clock.BranchesAsync(page.Items.Where(x => x.BranchId is not null).Select(x => x.BranchId!.Value), ct);

        return TypedResults.Ok(new PagedResult<HoldResponse>(
            page.Items.Select(x => Project(x.h, definitions.GetValueOrDefault(x.h.HoldCode), x.OrderNo, x.BranchId,
                x.BranchId is { } id ? branches.GetValueOrDefault(id)?.BranchCode : null)).ToList(),
            page.Page, page.PageSize, page.TotalCount));
    }

    private static async Task<Results<Ok<HoldResponse>, NotFound>> GetAsync(
        Guid id, TosDbContext db, IMasterDataReferences master, BranchClock clock, ICallerPermissions scope, CancellationToken ct)
    {
        var found = await LoadAsync(db, id, ct);
        if (found is null || !await AllowedAsync(db, scope, TosPermissions.HoldView, found, ct)) return TypedResults.NotFound();

        return TypedResults.Ok(await DetailAsync(db, master, clock, found, ct));
    }

    private static async Task<Results<Ok<ContainerHoldsResponse>, ValidationProblem>> ForContainerAsync(
        string containerNo, TosDbContext db, IMasterDataReferences master, CancellationToken ct)
    {
        var box = ContainerNumber.Normalise(containerNo);
        if (!ContainerNumber.IsWellFormed(box))
            return TosSupport.Invalid("containerNo", $"'{containerNo}' is not a container number (4 letters ending U/J/Z, 7 digits).");

        var active = await (
            from h in db.VwActiveHolds.AsNoTracking().Where(h => h.ContainerNo == box)
            join b in db.Bookings on h.BookingId equals b.BookingId into bookings
            from b in bookings.DefaultIfEmpty()
            orderby h.AppliedAt
            select new { h, OrderNo = b == null ? null : b.OrderNo }).ToListAsync(ct);

        var definitions = await master.HoldsAsync(active.Select(x => x.h.HoldCode), ct);

        var holds = active
            .Select(x => new { x, d = definitions.GetValueOrDefault(x.h.HoldCode) })
            .OrderBy(x => x.d?.Priority ?? byte.MaxValue)
            .ThenBy(x => x.x.h.AppliedAt)
            .Select(x => new ActiveHoldResponse(
                x.x.h.ContainerHoldId, x.x.h.HoldCode, x.d?.DescriptionEn, x.d?.HoldType, x.d?.BlockingScope,
                x.d?.ReleaseAuthority, x.d?.Priority, x.d?.DisplayColorHex,
                x.x.h.AppliedAt, x.x.h.ApplyReason, x.x.h.Source, x.x.h.HeldVia, x.x.h.BookingId, x.x.OrderNo))
            .ToList();

        return TypedResults.Ok(new ContainerHoldsResponse(box, holds.Count > 0, holds));
    }

    // ── apply ───────────────────────────────────────────────────────────────

    private static async Task<Results<CreatedAtRoute<HoldResponse>, ValidationProblem, ProblemHttpResult>> ApplyAsync(
        ApplyHoldRequest request, TosDbContext db, IMasterDataReferences master, BranchClock clock,
        ITenantContext caller, ICallerPermissions scope, CancellationToken ct)
    {
        var errors = new Dictionary<string, List<string>>();

        var onBox = !string.IsNullOrWhiteSpace(request.ContainerNo);
        var onBooking = request.BookingId is not null;
        if (onBox == onBooking)
            return TosSupport.Invalid("containerNo", "Name a container OR a booking. A hold on a box says that box may not move; a hold on a booking stops everything on the order.");

        var holdCode = request.HoldCode.Clean()!;
        var definition = (await master.HoldsAsync([holdCode], ct)).GetValueOrDefault(holdCode);
        if (definition is null) errors.Add("holdCode", $"'{holdCode}' is not a hold type. Set it up in master data first.");
        else if (!definition.IsActive) errors.Add("holdCode", $"Hold type {definition.HoldCode} is no longer in use.");

        string? box = null;
        Booking? booking = null;

        if (onBox)
        {
            box = ContainerNumber.Normalise(request.ContainerNo!);
            if (!ContainerNumber.IsWellFormed(box))
                errors.Add("containerNo", $"'{request.ContainerNo}' is not a container number (4 letters ending U/J/Z, 7 digits).");
            else if (!ContainerNumber.IsValid(box)
                     && await master.GetBoolSettingAsync(TosSettingKeys.EnforceCheckDigit, null, false, ct))
                errors.Add("containerNo",
                    $"{box} fails the ISO 6346 check digit (expected {ContainerNumber.CheckDigitOf(box)}). A hold on a mistyped number stops the wrong box — or nothing at all.");
        }
        else
        {
            booking = await db.Bookings.AsNoTracking().SingleOrDefaultAsync(b => b.BookingId == request.BookingId, ct);
            if (booking is null) errors.Add("bookingId", "Unknown booking.");
            else if (!scope.HasAt(TosPermissions.HoldApply, booking.BranchId))
                return TosScope.OutsideYourBranches($"Booking {booking.OrderNo} belongs to a depot you do not cover.");
            else if (booking.Status != BookingRules.Open)
                errors.Add("bookingId", $"Booking {booking.OrderNo} is {booking.Status} — holding it stops nothing.");
        }

        if (errors.Count > 0) return TosSupport.Invalid(errors);

        var duplicate = await db.ContainerHolds.AsNoTracking().FirstOrDefaultAsync(h =>
            h.HoldCode == holdCode && h.ReleasedAt == null
            && (onBox ? h.ContainerNo == box : h.ContainerNo == null && h.BookingId == request.BookingId), ct);
        if (duplicate is not null)
            return TosSupport.Conflict(
                $"{holdCode} is already on {(onBox ? box : booking!.OrderNo)}.",
                $"Applied {duplicate.AppliedAt:u}: {duplicate.ApplyReason}. Release it before applying it again — a second row would hide the first.");

        var hold = new ContainerHold
        {
            TenantId = caller.TenantId(),
            ContainerNo = box,
            BookingId = onBooking ? request.BookingId : null,
            HoldId = definition!.HoldId,
            HoldCode = definition.HoldCode,
            AppliedAt = DateTimeOffset.UtcNow,
            AppliedBy = caller.UserId(),
            ApplyReason = request.Reason.Trim(),
            ExternalRef = request.ExternalRef?.Trim() is { Length: > 0 } reference ? reference : null,
            Source = "MANUAL",
        };
        db.ContainerHolds.Add(hold);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        return TypedResults.CreatedAtRoute(
            await DetailAsync(db, master, clock, hold, ct), "GetContainerHold", new { id = hold.ContainerHoldId });
    }

    // ── release ─────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<HoldResponse>, NotFound, ValidationProblem, ProblemHttpResult>> ReleaseAsync(
        Guid id, ReleaseHoldRequest request, TosDbContext db, IMasterDataReferences master, BranchClock clock,
        ITenantContext caller, ICallerPermissions scope, CancellationToken ct)
    {
        var hold = await LoadAsync(db, id, ct);
        // The door here is only "authenticated": WHICH permission a release needs
        // depends on the hold type, which is a row read away. Reading it at all still
        // needs tos.hold.view, or an id-guessing caller learns holds exist.
        if (hold is null || !await AllowedAsync(db, scope, TosPermissions.HoldView, hold, ct)) return TypedResults.NotFound();
        if (hold.ReleasedAt is not null)
            return TosSupport.Conflict($"{hold.HoldCode} was already released on {hold.ReleasedAt:u}.", hold.ReleaseReason);

        // WHO may lift it is the hold type's business, not the holder's: MDM's
        // release_authority maps to one permission (PLAN §10.7).
        var definition = (await master.HoldsAsync([hold.HoldCode], ct)).GetValueOrDefault(hold.HoldCode);
        if (definition is null)
            return TosSupport.Conflict($"Hold type {hold.HoldCode} no longer exists in master data.",
                "Without its release authority there is no way to say who may lift this hold. Restore the hold type first.");

        var (permission, referenceRequired) = HoldRules.ReleaseRule(definition.ReleaseAuthority);
        var branchId = await BranchOfAsync(db, hold, ct);
        var allowed = branchId is { } b ? scope.HasAt(permission, b) : scope.HasAnywhere(permission);
        if (!allowed)
            return TypedResults.Problem(
                title: $"{hold.HoldCode} is released by {definition.ReleaseAuthority}.",
                detail: $"You need {permission} to lift it. Ask someone who has it — the release goes on record under their name, not yours.",
                statusCode: StatusCodes.Status403Forbidden);

        if (referenceRequired && string.IsNullOrWhiteSpace(request.ReleaseRef))
            return TosSupport.Invalid("releaseRef", HoldRules.ReferenceReason(definition.ReleaseAuthority)!);

        if (!db.TrySetExpectedVersion(hold, request.RowVersion))
            return TosSupport.Invalid("rowVersion", "Send the rowVersion you received when reading the hold.");

        hold.ReleasedAt = DateTimeOffset.UtcNow;
        hold.ReleasedBy = caller.UserId();
        hold.ReleaseReason = request.Reason.Trim();
        hold.ReleaseRef = request.ReleaseRef?.Trim() is { Length: > 0 } reference ? reference : null;
        hold.ReleaseSource = "MANUAL";

        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        return TypedResults.Ok(await DetailAsync(db, master, clock, hold, ct));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static Task<ContainerHold?> LoadAsync(TosDbContext db, Guid id, CancellationToken ct) =>
        db.ContainerHolds.SingleOrDefaultAsync(h => h.ContainerHoldId == id, ct);

    /// <summary>
    /// A hold on a BOOKING is scoped to that booking's depot. A hold on a BOX is not
    /// scoped to any: the box travels, and a line or customs hold has to be visible
    /// at whichever depot it turns up at.
    /// </summary>
    private static async Task<bool> AllowedAsync(TosDbContext db, ICallerPermissions scope, string permission, ContainerHold hold, CancellationToken ct)
    {
        if (!scope.HasAnywhere(permission)) return false;
        if (hold.BookingId is null) return true;

        var branchId = await BranchOfAsync(db, hold, ct);
        return branchId is { } b && scope.HasAt(permission, b);
    }

    private static async Task<Guid?> BranchOfAsync(TosDbContext db, ContainerHold hold, CancellationToken ct) =>
        hold.BookingId is null
            ? null
            : await db.Bookings.AsNoTracking().Where(b => b.BookingId == hold.BookingId).Select(b => (Guid?)b.BranchId).SingleOrDefaultAsync(ct);

    private static async Task<HoldResponse> DetailAsync(
        TosDbContext db, IMasterDataReferences master, BranchClock clock, ContainerHold hold, CancellationToken ct)
    {
        var definition = (await master.HoldsAsync([hold.HoldCode], ct)).GetValueOrDefault(hold.HoldCode);

        string? orderNo = null;
        Guid? branchId = null;
        if (hold.BookingId is not null)
        {
            var booking = await db.Bookings.AsNoTracking()
                .Where(b => b.BookingId == hold.BookingId)
                .Select(b => new { b.OrderNo, b.BranchId }).SingleOrDefaultAsync(ct);
            orderNo = booking?.OrderNo;
            branchId = booking?.BranchId;
        }

        var branchCode = branchId is { } id ? (await clock.BranchesAsync([id], ct)).GetValueOrDefault(id)?.BranchCode : null;
        return Project(hold, definition, orderNo, branchId, branchCode);
    }

    internal static HoldResponse Project(ContainerHold h, HoldRef? d, string? orderNo, Guid? branchId, string? branchCode) => new(
        h.ContainerHoldId, h.ContainerNo, h.BookingId, orderNo, branchId, branchCode,
        h.HoldCode, d?.DescriptionEn, d?.HoldType, d?.BlockingScope, d?.ReleaseAuthority, d?.Priority, d?.DisplayColorHex,
        h.AppliedAt, h.AppliedBy, h.ApplyReason, h.ExternalRef, h.Source,
        h.ReleasedAt, h.ReleasedBy, h.ReleaseReason, h.ReleaseRef, h.ReleaseSource,
        h.ReleasedAt is null, Convert.ToBase64String(h.RowVersion));
}
