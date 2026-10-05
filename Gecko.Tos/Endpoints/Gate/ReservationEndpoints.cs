using Gecko.Data;
using Gecko.Identity.Contracts;
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

namespace Gecko.Tos.Endpoints.Gate;

/// <summary>
/// Record on the gate screen (owner 2026-10-04, GATE_IN_BIG_SAVE.md §1): the box is held for the
/// truck being keyed, so no other clerk picks it, raises a blind order for it or gates it. Holds
/// lapse by themselves (<see cref="BoxReservations.Hold"/>), so an abandoned screen frees its boxes.
/// </summary>
internal static class ReservationEndpoints
{
    public static RouteGroupBuilder MapReservationEndpoints(this RouteGroupBuilder tos)
    {
        var holds = tos.MapGroup("/gate/reservations").WithTags("TOS — gate");

        holds.MapPost("/", ReserveAsync).RequireBranchPermission(TosPermissions.GateCreate)
            .Validate<ReserveBoxRequest>()
            .WithSummary("Hold a box for the truck being keyed (Record)")
            .WithDescription("201 a new hold; 200 the same draft again (the hold is extended); 409 another draft holds it. A hold lapses after 15 minutes unless renewed.");
        holds.MapGet("/", ListAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("A draft's live holds (the screen reloading)");
        holds.MapDelete("/{id:guid}", ReleaseAsync).RequireBranchPermission(TosPermissions.GateCreate)
            .WithSummary("Release a hold (the row was removed); 204 also when it is already released");
        return tos;
    }

    private static async Task<Results<Created<BoxReservationResponse>, Ok<BoxReservationResponse>, ValidationProblem, ProblemHttpResult>> ReserveAsync(
        ReserveBoxRequest request, TosDbContext db, IUserDirectory users, ITenantContext caller, ICallerPermissions scope,
        TimeProvider time, CancellationToken ct)
    {
        var branchId = request.BranchId!.Value;
        var draftId = request.DraftId!.Value;
        if (!scope.HasAt(TosPermissions.GateCreate, branchId))
            return TosScope.OutsideYourBranches("That gate is at a depot you do not cover.");
        if (request.BookingContainerId is null && request.ContainerNo.Clean() is null)
            return TosSupport.Invalid("bookingContainerId", "Name the booked box (bookingContainerId) or, for a box on no order, its containerNo.");

        // Which box: a booked line (its number comes with it), or a number — which may itself be on a booking here.
        var bookingContainerId = request.BookingContainerId;
        string? containerNo = null;
        if (bookingContainerId is { } lineId)
        {
            var line = await (
                from x in db.BookingContainers.AsNoTracking()
                join b in db.Bookings on x.BookingId equals b.BookingId
                where x.BookingContainerId == lineId
                select new { x.ContainerNo, x.EndedAt, b.BranchId, b.Status, b.OrderNo }).SingleOrDefaultAsync(ct);
            if (line is null) return TosSupport.Invalid("bookingContainerId", "Unknown booked box.");
            if (line.BranchId != branchId) return TosSupport.Invalid("bookingContainerId", $"That box is booked at another depot ({line.OrderNo}).");
            if (line.Status != BookingRules.Open || line.EndedAt is not null)
                return TosSupport.Conflict($"That box is no longer open on {line.OrderNo}.");
            containerNo = line.ContainerNo;
        }
        else
        {
            containerNo = ContainerNumber.Normalise(request.ContainerNo!);
            if (!ContainerNumber.IsWellFormed(containerNo))
                return TosSupport.Invalid("containerNo", $"'{request.ContainerNo}' is not a container number (4 letters ending U/J/Z, 7 digits).");
            bookingContainerId = await db.BookingContainers.AsNoTracking()
                .Where(x => x.ContainerNo == containerNo && x.EndedAt == null)
                .Select(x => (Guid?)x.BookingContainerId).FirstOrDefaultAsync(ct);
        }

        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var live = await BoxReservations.LiveAsync(db, bookingContainerId, containerNo, now, ct);
        if (live.FirstOrDefault(r => r.DraftId != draftId) is { } other)
            return await HeldAsync(other, users, ct);

        var mine = live.FirstOrDefault();
        var created = mine is null;
        if (mine is null)
        {
            mine = new BoxReservation
            {
                TenantId = caller.TenantId(), BranchId = branchId, DraftId = draftId,
                BookingContainerId = bookingContainerId, ContainerNo = containerNo,
                ReservedBy = caller.UserId(), ReservedAt = now, CreatedBy = caller.UserId(),
            };
            db.BoxReservations.Add(mine);
        }
        mine.ExpiresAt = now + BoxReservations.Hold;
        mine.UpdatedAt = now;
        mine.UpdatedBy = caller.UserId();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException?.Message.Contains("uq_box_reservation__live") == true)
        {
            // Two clerks pressed Record at the same instant: the other one holds it now.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var winner = (await BoxReservations.LiveAsync(db, bookingContainerId, containerNo, now, ct)).FirstOrDefault(r => r.DraftId != draftId);
            return winner is null ? TosSupport.Conflict("That box was just held by another truck. Try again.") : await HeldAsync(winner, users, ct);
        }
        await tx.CommitAsync(ct);

        var response = await ProjectAsync(mine, users, ct);
        return created ? TypedResults.Created($"/api/tos/gate/reservations/{mine.BoxReservationId}", response) : TypedResults.Ok(response);
    }

    private static async Task<Results<Ok<IReadOnlyList<BoxReservationResponse>>, ValidationProblem, ProblemHttpResult>> ListAsync(
        TosDbContext db, IUserDirectory users, ICallerPermissions scope, TimeProvider time, CancellationToken ct,
        Guid? branchId = null, Guid? draftId = null)
    {
        if (branchId is null) return TosSupport.Invalid("branchId", "Which gate?");
        if (draftId is null) return TosSupport.Invalid("draftId", "Whose holds? Send the screen's draftId.");
        if (!scope.HasAt(TosPermissions.GateView, branchId.Value))
            return TosScope.OutsideYourBranches("That gate is at a depot you do not cover.");

        var now = time.GetUtcNow();
        var rows = await db.BoxReservations.AsNoTracking()
            .Where(r => r.BranchId == branchId && r.DraftId == draftId && r.ReleasedAt == null && r.ExpiresAt > now)
            .OrderBy(r => r.ReservedAt).ToListAsync(ct);
        var names = await users.DisplayNamesAsync(rows.Select(r => r.ReservedBy).Distinct(), ct);
        IReadOnlyList<BoxReservationResponse> list = rows.Select(r => Project(r, names.GetValueOrDefault(r.ReservedBy))).ToList();
        return TypedResults.Ok(list);
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> ReleaseAsync(
        Guid id, TosDbContext db, ITenantContext caller, ICallerPermissions scope, TimeProvider time, CancellationToken ct,
        Guid? draftId = null)
    {
        var hold = await db.BoxReservations.SingleOrDefaultAsync(r => r.BoxReservationId == id, ct);
        if (hold is null || !scope.HasAt(TosPermissions.GateView, hold.BranchId)) return TypedResults.NotFound();
        if (hold.ReleasedAt is not null) return TypedResults.NoContent();
        // A hold is its draft's to release; a supervisor may free a box someone left held.
        if (hold.DraftId != draftId && !scope.HasAt(TosPermissions.GateOverride, hold.BranchId))
            return TosScope.OutsideYourBranches("That box is held by another truck being keyed. Only its clerk (or a gate supervisor) can release it.");

        var now = time.GetUtcNow();
        BoxReservations.Release(hold, BoxReservations.Removed, caller.UserId(), now);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static async Task<ProblemHttpResult> HeldAsync(BoxReservation other, IUserDirectory users, CancellationToken ct)
    {
        var name = (await users.DisplayNamesAsync([other.ReservedBy], ct)).GetValueOrDefault(other.ReservedBy);
        var finding = BoxReservations.Finding(other, name);
        return TypedResults.Problem(
            title: finding.Message, statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = finding.Code, ["reservedBy"] = other.ReservedBy, ["reservedByName"] = name, ["expiresAt"] = other.ExpiresAt,
            });
    }

    private static async Task<BoxReservationResponse> ProjectAsync(BoxReservation r, IUserDirectory users, CancellationToken ct) =>
        Project(r, (await users.DisplayNamesAsync([r.ReservedBy], ct)).GetValueOrDefault(r.ReservedBy));

    private static BoxReservationResponse Project(BoxReservation r, string? name) =>
        new(r.BoxReservationId, r.BranchId, r.DraftId, r.BookingContainerId, r.ContainerNo, r.ReservedBy, name, r.ReservedAt, r.ExpiresAt);
}
