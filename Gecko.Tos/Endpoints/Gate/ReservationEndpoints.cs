using Gecko.Data;
using Gecko.Identity.Contracts;
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
            .WithDescription("201 a new hold; 200 the same draft again (the hold is extended); 409 another draft holds it. A hold lapses after 15 minutes unless renewed. "
                + "An EMPTY booking place with the box keyed (containerNo) is checked at once (yard, other booking, type) and, when another truck took that place, "
                + "moved to the next free place like it (switchedFromBookingContainerId + message); 409 NO_FREE_PLACE / BOX_REFUSED otherwise.");
        holds.MapGet("/", ListAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("A draft's live holds (the screen reloading)");
        holds.MapDelete("/{id:guid}", ReleaseAsync).RequireBranchPermission(TosPermissions.GateCreate)
            .WithSummary("Release a hold (the row was removed); 204 also when it is already released");
        return tos;
    }

    private static async Task<Results<Created<BoxReservationResponse>, Ok<BoxReservationResponse>, ValidationProblem, ProblemHttpResult>> ReserveAsync(
        ReserveBoxRequest request, TosDbContext db, IUserDirectory users, ITenantContext caller, ICallerPermissions scope,
        IMasterDataReferences master, BranchClock clock, TimeProvider time, CancellationToken ct)
    {
        // Two clerks on the same empty place in the same instant: the loser of the race is moved on (owner 2026-10-06, "a").
        for (var attempt = 0; ; attempt++)
        {
            var result = await ReserveOnceAsync(request, db, users, caller, scope, master, clock, time, ct);
            if (result is not { Lost: true } || attempt == 2) return result.Answer!;
            db.ChangeTracker.Clear();
        }
    }

    private sealed record Attempt(Results<Created<BoxReservationResponse>, Ok<BoxReservationResponse>, ValidationProblem, ProblemHttpResult>? Answer, bool Lost = false);

    private static async Task<Attempt> ReserveOnceAsync(
        ReserveBoxRequest request, TosDbContext db, IUserDirectory users, ITenantContext caller, ICallerPermissions scope,
        IMasterDataReferences master, BranchClock clock, TimeProvider time, CancellationToken ct)
    {
        var branchId = request.BranchId!.Value;
        var draftId = request.DraftId!.Value;
        if (!scope.HasAt(TosPermissions.GateCreate, branchId))
            return new(TosScope.OutsideYourBranches("That gate is at a depot you do not cover."));
        if (request.BookingContainerId is null && request.ContainerNo.Clean() is null)
            return new(TosSupport.Invalid("bookingContainerId", "Name the booked box (bookingContainerId) or, for a box on no order, its containerNo."));
        var keyed = ContainerNumber.Normalise(request.ContainerNo ?? "") is { Length: > 0 } k ? k : null;
        if (keyed is not null && !ContainerNumber.IsWellFormed(keyed))
            return new(TosSupport.Invalid("containerNo", $"'{request.ContainerNo}' is not a container number (4 letters ending U/J/Z, 7 digits)."));

        var now = time.GetUtcNow();
        var bookingContainerId = request.BookingContainerId;
        string? containerNo;
        Guid? switchedFrom = null;
        string? note = null;
        var warnings = new List<GateFinding>();
        var emptyPlace = false;   // an empty place: any free place like it will do, so a lost race moves on
        if (bookingContainerId is { } lineId)
        {
            var place = await GateNominations.PlaceAsync(db, lineId, ct);
            if (place is null) return new(TosSupport.Invalid("bookingContainerId", "Unknown booked box."));
            if (place.BranchId != branchId) return new(TosSupport.Invalid("bookingContainerId", $"That box is booked at another depot ({place.OrderNo})."));
            if (place.BookingStatus != BookingRules.Open || place.EndedAt is not null)
                return new(TosSupport.Conflict($"That box is no longer open on {place.OrderNo}."));

            if (place.ContainerNo is not null && (keyed is null || keyed == place.ContainerNo) && place.Source != GateNominations.Source)
                containerNo = place.ContainerNo;   // a box the booking named: the line and its number are one
            else
            {
                // An empty place (or one a gate filled a moment ago): this box goes to it, or to the next free place like it.
                var choice = await GateNominations.ChooseAsync(db, place, keyed, draftId, now, ct);
                if (choice.Place is not { } chosen)
                    return new(Problem(choice.Code!, choice.Problem!, []));
                if (keyed is not null)
                {
                    var findings = await GateNominations.CheckAsync(db, master, clock, chosen, keyed, draftId, now, ct);
                    if (findings.Any(f => f.Severity == GateSeverity.Block))
                        return new(Problem("BOX_REFUSED", $"{keyed} cannot go on {chosen.OrderNo}.", findings));
                    warnings.AddRange(findings);
                }
                emptyPlace = true;
                bookingContainerId = chosen.BookingContainerId;
                containerNo = keyed ?? chosen.ContainerNo;
                switchedFrom = choice.SwitchedFrom;
                note = choice.Note;
            }
        }
        else
        {
            containerNo = keyed!;
            bookingContainerId = await db.BookingContainers.AsNoTracking()
                .Where(x => x.ContainerNo == containerNo && x.EndedAt == null)
                .Select(x => (Guid?)x.BookingContainerId).FirstOrDefaultAsync(ct);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var live = await BoxReservations.LiveAsync(db, bookingContainerId, containerNo, now, ct);
        if (live.FirstOrDefault(r => r.DraftId != draftId) is { } other)
            return emptyPlace && other.BookingContainerId == bookingContainerId && (keyed is null || other.ContainerNo != keyed)
                ? new(null, Lost: true)   // another truck took this empty place since it was chosen: choose again
                : new(await HeldAsync(other, users, ct));

        var mine = live.FirstOrDefault();
        var created = mine is null;
        foreach (var extra in live.Skip(1)) BoxReservations.Release(extra, BoxReservations.Removed, caller.UserId(), now);
        if (mine is null)
        {
            mine = new BoxReservation
            {
                TenantId = caller.TenantId(), BranchId = branchId, DraftId = draftId,
                ReservedBy = caller.UserId(), ReservedAt = now, CreatedBy = caller.UserId(),
            };
            db.BoxReservations.Add(mine);
        }
        mine.BookingContainerId = bookingContainerId;   // the place (possibly the next free one) and the box keyed for it
        mine.ContainerNo = containerNo;
        mine.ExpiresAt = now + BoxReservations.Hold;
        mine.UpdatedAt = now;
        mine.UpdatedBy = caller.UserId();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException?.Message.Contains("uq_box_reservation__live") == true)
        {
            // Two clerks pressed Record at the same instant and the other one won.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            if (emptyPlace) return new(null, Lost: true);   // an empty place: choose again, it moves to the next free one
            var winner = (await BoxReservations.LiveAsync(db, bookingContainerId, containerNo, now, ct)).FirstOrDefault(r => r.DraftId != draftId);
            return new(winner is null ? TosSupport.Conflict("That box was just held by another truck. Try again.") : await HeldAsync(winner, users, ct));
        }
        await tx.CommitAsync(ct);

        var response = await ProjectAsync(mine, users, ct) with
        {
            SwitchedFromBookingContainerId = switchedFrom,
            Message = note,
            Findings = warnings.Select(f => new GateFindingResponse(f.Code, f.Message, f.Severity.ToString().ToUpperInvariant())).ToList(),
        };
        return new(created ? TypedResults.Created($"/api/tos/gate/reservations/{mine.BoxReservationId}", response) : TypedResults.Ok(response));
    }

    /// <summary>A Record refused with a code the screen can act on, and the findings behind it.</summary>
    private static ProblemHttpResult Problem(string code, string title, IReadOnlyList<GateFinding> findings) =>
        TypedResults.Problem(title: title, statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["findings"] = findings.Select(f => new GateFindingResponse(f.Code, f.Message, f.Severity.ToString().ToUpperInvariant())).ToList(),
            });

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
