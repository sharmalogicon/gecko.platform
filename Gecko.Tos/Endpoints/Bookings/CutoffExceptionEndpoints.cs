using System.ComponentModel.DataAnnotations;
using Gecko.Data;
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

namespace Gecko.Tos.Endpoints.Bookings;

// ── contracts ───────────────────────────────────────────────────────────────

public sealed record CutoffExceptionResponse(
    Guid CutoffExceptionId, Guid BookingId, string OrderNo, Guid? EquipmentRequirementId, int? LineNo,
    string CutoffKind, DateTimeOffset AllowedUntil, DateTimeOffset? CutoffAt,
    DateTimeOffset ApprovedAt, Guid ApprovedBy, string Reason, string? CarrierApprovalRef,
    DateTimeOffset? RevokedAt, Guid? RevokedBy, string? RevokeReason,
    bool IsLive, string RowVersion);

public sealed record ApproveCutoffExceptionRequest(
    [property: Required, MaxLength(20)] string CutoffKind,
    [property: Required] DateTimeOffset? AllowedUntil,
    [property: Required, MinLength(3), MaxLength(500)] string Reason,
    Guid? EquipmentRequirementId = null,
    [property: MaxLength(50)] string? CarrierApprovalRef = null);

public sealed record RevokeCutoffExceptionRequest(
    [property: Required, MinLength(3), MaxLength(300)] string Reason,
    string? RowVersion = null);

/// <summary>
/// Late gates, approved BEFORE the truck arrives (PLAN §4.2, D-2, batch C).
///
/// Vector had a bit: `AllowLateGate`. In 2025 it let 686 boxes in after the yard
/// cut-off with nobody's name on it and no note of who at the line had agreed.
/// Here an exception is a row: which cut-off, until when, approved by whom, why,
/// and the line's reference — revocable, and never a blanket "late is fine".
///
/// The evaluation at the barrier (§5.2) reads these rows; approving one is the
/// only way past a cut-off other than a supervisor override at the barrier
/// itself, which is recorded on the transaction instead.
/// </summary>
internal static class CutoffExceptionEndpoints
{
    public static RouteGroupBuilder MapCutoffExceptionEndpoints(this RouteGroupBuilder bookings)
    {
        bookings.MapGet("/{id:guid}/cutoff-exceptions", ListAsync)
            .RequireBranchPermission(TosPermissions.BookingView)
            .WithSummary("Late-gate approvals on this booking, live and spent");

        bookings.MapPost("/{id:guid}/cutoff-exceptions", ApproveAsync)
            .RequireBranchPermission(TosPermissions.CutoffOverride)
            .Validate<ApproveCutoffExceptionRequest>()
            .WithSummary("Approve a late gate up to a stated time, with a reason");

        bookings.MapPost("/{id:guid}/cutoff-exceptions/{exceptionId:guid}/revoke", RevokeAsync)
            .RequireBranchPermission(TosPermissions.CutoffOverride)
            .Validate<RevokeCutoffExceptionRequest>()
            .WithSummary("Withdraw an approval that has not been used");

        return bookings;
    }

    private static async Task<Results<Ok<IReadOnlyList<CutoffExceptionResponse>>, NotFound>> ListAsync(
        Guid id, TosDbContext db, ICallerPermissions scope, TimeProvider time, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking().SingleOrDefaultAsync(b => b.BookingId == id, ct);
        if (booking is null || !scope.HasAt(TosPermissions.BookingView, booking.BranchId)) return TypedResults.NotFound();

        var rows = await db.CutoffExceptions.AsNoTracking().Where(e => e.BookingId == id)
            .OrderByDescending(e => e.ApprovedAt).ToListAsync(ct);
        var lineNos = await LineNumbersAsync(db, rows, ct);

        return TypedResults.Ok<IReadOnlyList<CutoffExceptionResponse>>(
            rows.Select(e => Project(e, booking.OrderNo, lineNos, cutoffAt: null, time.GetUtcNow())).ToList());
    }

    private static async Task<Results<Ok<CutoffExceptionResponse>, NotFound, ValidationProblem, ProblemHttpResult>> ApproveAsync(
        Guid id, ApproveCutoffExceptionRequest request, TosDbContext db, ITenantContext caller,
        ICallerPermissions scope, TimeProvider time, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking().SingleOrDefaultAsync(b => b.BookingId == id, ct);
        if (booking is null || !scope.HasAt(TosPermissions.CutoffOverride, booking.BranchId)) return TypedResults.NotFound();
        if (booking.Status != BookingRules.Open)
            return TosSupport.Conflict($"Booking {booking.OrderNo} is {booking.Status}.", "Nothing will gate against it, so a late-gate approval changes nothing.");

        var errors = new Dictionary<string, List<string>>();
        var kind = request.CutoffKind.Clean()!;
        if (!CutoffRules.Kinds.Contains(kind))
            errors.Add("cutoffKind", $"'{kind}' is not a cut-off kind. Use one of: {string.Join(", ", CutoffRules.Kinds.Order())}.");

        var allowedUntil = request.AllowedUntil!.Value;
        var now = time.GetUtcNow();
        if (allowedUntil <= now)
            errors.Add("allowedUntil", "That time has already passed — an approval that expires before it is written lets nothing through.");

        if (request.EquipmentRequirementId is { } requirementId
            && !await db.EquipmentRequirements.AnyAsync(r => r.EquipmentRequirementId == requirementId && r.BookingId == id, ct))
            errors.Add("equipmentRequirementId", "That requirement line is not on this booking.");

        // A cut-off belongs to a vessel call. Without one there is nothing to be late
        // for, and an exception would be a blanket permission with no end in sight.
        if (booking.VesselCallId is null)
            errors.Add("cutoffKind", $"Booking {booking.OrderNo} is not on a vessel call, so it has no cut-off to be excepted from.");

        if (errors.Count > 0) return TosSupport.Invalid(errors);

        var call = await db.VesselCalls.AsNoTracking().SingleAsync(c => c.VesselCallId == booking.VesselCallId, ct);
        if (call.IsCancelled)
            return TosSupport.Conflict($"Call {call.CallRef} is cancelled.", "Move the booking to the replacement call before approving a late gate.");

        var effective = await CutoffLookup.EffectiveAsync(db, call.VesselCallId, booking.LinePartyId, booking.BranchId, ct);
        var cutoff = effective.SingleOrDefault(c => c.Kind == kind);
        if (cutoff is null)
            return TosSupport.Invalid("cutoffKind", $"Call {call.CallRef} has no {kind} cut-off for {booking.LinePartyCode}. There is nothing to grant an exception to.");
        if (allowedUntil <= cutoff.At)
            return TosSupport.Invalid("allowedUntil",
                $"The {kind} cut-off is {cutoff.At:u} ({cutoff.AppliesTo}). An approval until {allowedUntil:u} grants nothing — set a later time.");

        // The ship is the hard stop. Vector has 495 cut-offs set after the ETD; an
        // exception that outlives the vessel is the same mistake with a signature.
        if (call.Etd is { } etd && allowedUntil > etd)
            return TosSupport.Invalid("allowedUntil",
                $"{call.CallRef} sails at {etd:u}. A box gated in after that misses the ship whoever approved it.");

        var live = await db.CutoffExceptions.AsNoTracking().FirstOrDefaultAsync(e =>
            e.BookingId == id && e.CutoffKind == kind && e.EquipmentRequirementId == request.EquipmentRequirementId
            && e.RevokedAt == null && e.AllowedUntil > now, ct);
        if (live is not null)
            return TosSupport.Conflict(
                $"{kind} is already approved until {live.AllowedUntil:u}.",
                "Revoke that approval first if the window has changed — two live approvals would leave it unclear which one the gate judged by.");

        var exception = new CutoffException
        {
            TenantId = caller.TenantId(),
            BookingId = id,
            EquipmentRequirementId = request.EquipmentRequirementId,
            CutoffKind = kind,
            AllowedUntil = allowedUntil,
            ApprovedAt = now,
            ApprovedBy = caller.UserId(),
            Reason = request.Reason.Trim(),
            CarrierApprovalRef = request.CarrierApprovalRef?.Trim() is { Length: > 0 } reference ? reference : null,
        };
        db.CutoffExceptions.Add(exception);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        var lineNos = await LineNumbersAsync(db, [exception], ct);
        return TypedResults.Ok(Project(exception, booking.OrderNo, lineNos, cutoff.At, now));
    }

    private static async Task<Results<Ok<CutoffExceptionResponse>, NotFound, ValidationProblem, ProblemHttpResult>> RevokeAsync(
        Guid id, Guid exceptionId, RevokeCutoffExceptionRequest request, TosDbContext db, ITenantContext caller,
        ICallerPermissions scope, TimeProvider time, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking().SingleOrDefaultAsync(b => b.BookingId == id, ct);
        if (booking is null || !scope.HasAt(TosPermissions.CutoffOverride, booking.BranchId)) return TypedResults.NotFound();

        var exception = await db.CutoffExceptions.SingleOrDefaultAsync(e => e.CutoffExceptionId == exceptionId && e.BookingId == id, ct);
        if (exception is null) return TypedResults.NotFound();
        if (exception.RevokedAt is not null)
            return TosSupport.Conflict($"That approval was already withdrawn on {exception.RevokedAt:u}.", exception.RevokeReason);

        if (!db.TrySetExpectedVersion(exception, request.RowVersion))
            return TosSupport.Invalid("rowVersion", "Send the rowVersion you received when reading the approval.");

        exception.RevokedAt = time.GetUtcNow();
        exception.RevokedBy = caller.UserId();
        exception.RevokeReason = request.Reason.Trim();

        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        var lineNos = await LineNumbersAsync(db, [exception], ct);
        return TypedResults.Ok(Project(exception, booking.OrderNo, lineNos, cutoffAt: null, time.GetUtcNow()));
    }

    private static async Task<IReadOnlyDictionary<Guid, int>> LineNumbersAsync(
        TosDbContext db, IReadOnlyCollection<CutoffException> rows, CancellationToken ct)
    {
        var ids = rows.Where(e => e.EquipmentRequirementId is not null).Select(e => e.EquipmentRequirementId!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, int>();

        return await db.EquipmentRequirements.AsNoTracking()
            .Where(r => ids.Contains(r.EquipmentRequirementId))
            .ToDictionaryAsync(r => r.EquipmentRequirementId, r => (int)r.LineNo, ct);
    }

    private static CutoffExceptionResponse Project(
        CutoffException e, string orderNo, IReadOnlyDictionary<Guid, int> lineNos, DateTimeOffset? cutoffAt, DateTimeOffset now) => new(
        e.CutoffExceptionId, e.BookingId, orderNo, e.EquipmentRequirementId,
        e.EquipmentRequirementId is { } r && lineNos.TryGetValue(r, out var lineNo) ? lineNo : null,
        e.CutoffKind, e.AllowedUntil, cutoffAt,
        e.ApprovedAt, e.ApprovedBy, e.Reason, e.CarrierApprovalRef,
        e.RevokedAt, e.RevokedBy, e.RevokeReason,
        e.RevokedAt is null && e.AllowedUntil > now, Convert.ToBase64String(e.RowVersion));
}
