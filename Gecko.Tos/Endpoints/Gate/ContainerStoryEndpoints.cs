using Gecko.Data;
using Gecko.SharedKernel;
using Gecko.Tos.Application;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Endpoints.Gate;

/// <summary>
/// A box's story in one read: every stay at the depot (yard.container_visit) with
/// its journal (yard.visit_event), and every booking it has been on. The gate
/// register, the holds, the plug log and the registry already have their own
/// reads; this is the part no other endpoint answers — "where has this box been".
///
/// Read-only and additive. Stays are scoped like the gate (tos.gate.view per
/// depot), bookings like bookings (tos.booking.view per depot).
/// </summary>
internal static class ContainerStoryEndpoints
{
    /// <summary>A box that has lived at a depot for years stays readable: the newest stays first, capped.</summary>
    public const int MaxVisits = 100;

    public static RouteGroupBuilder MapContainerStoryEndpoints(this RouteGroupBuilder tos)
    {
        tos.MapGet("/containers/{containerNo}/story", StoryAsync)
            .WithTags("TOS — gate")
            .RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("One box's story: its stays at the depot with their events, and the bookings it has been on");

        return tos;
    }

    private static async Task<Results<Ok<ContainerStoryResponse>, ValidationProblem, ProblemHttpResult>> StoryAsync(
        string containerNo, TosDbContext db, BranchClock clock, ICallerPermissions scope, TimeProvider time,
        CancellationToken ct, Guid? branchId = null)
    {
        var box = ContainerNumber.Normalise(containerNo);
        if (!ContainerNumber.IsWellFormed(box))
            return TosSupport.Invalid("containerNo", $"'{containerNo}' is not a container number (4 to 11 letters or digits).");
        if (branchId is { } asked && !scope.HasAt(TosPermissions.GateView, asked))
            return TosScope.OutsideYourBranches("That depot is not one you cover.");

        return TypedResults.Ok(await ReadAsync(box, branchId, db, clock, scope, time, ct));
    }

    /// <summary>The story of a well-formed, normalised number, inside the caller's depots. Also the container inquiry's.</summary>
    internal static async Task<ContainerStoryResponse> ReadAsync(
        string box, Guid? branchId, TosDbContext db, BranchClock clock, ICallerPermissions scope, TimeProvider time, CancellationToken ct)
    {
        // ── stays ────────────────────────────────────────────────────────────
        var visits = db.ContainerVisits.AsNoTracking().Where(v => v.ContainerNo == box);
        if (branchId is not null) visits = visits.Where(v => v.BranchId == branchId);
        if (scope.BranchFilter(TosPermissions.GateView) is { } mine)
        {
            var allowed = mine.ToList();
            visits = visits.Where(v => allowed.Contains(v.BranchId));
        }

        var stays = await (
            from v in visits
            join gi in db.GateTransactions on v.GateInTransactionId equals gi.GateTransactionId into ins
            from gi in ins.DefaultIfEmpty()
            join go in db.GateTransactions on v.GateOutTransactionId equals go.GateTransactionId into outs
            from go in outs.DefaultIfEmpty()
            orderby gi.TransactionAt descending
            select new
            {
                v,
                InEir = gi == null ? null : gi.EirNo,
                InMovement = gi == null ? null : gi.MovementCode,
                InAt = gi == null ? (DateTimeOffset?)null : gi.TransactionAt,
                OutEir = go == null ? null : go.EirNo,
                OutAt = go == null ? (DateTimeOffset?)null : go.TransactionAt,
            }).Take(MaxVisits).ToListAsync(ct);

        var visitIds = stays.Select(s => s.v.ContainerVisitId).ToList();
        var events = (await db.VisitEvents.AsNoTracking()
                .Where(e => visitIds.Contains(e.ContainerVisitId))
                .OrderBy(e => e.EventAt).ThenBy(e => e.VisitEventId)
                .ToListAsync(ct))
            .ToLookup(e => e.ContainerVisitId);

        // The open stay's dwell and hold flag come from the stock view, so this page and
        // the stock list can never disagree (D-5).
        var inYard = await db.VwContainerInYards.AsNoTracking()
            .Where(y => visitIds.Contains(y.ContainerVisitId))
            .Select(y => new { y.ContainerVisitId, y.DaysInYard, y.IsHeld })
            .ToDictionaryAsync(y => y.ContainerVisitId, ct);

        // ── bookings ─────────────────────────────────────────────────────────
        var assignments =
            from bc in db.BookingContainers.AsNoTracking().Where(x => x.ContainerNo == box)
            join b in db.Bookings on bc.BookingId equals b.BookingId
            select new { bc, b };
        if (branchId is not null) assignments = assignments.Where(r => r.b.BranchId == branchId);
        if (scope.BranchFilter(TosPermissions.BookingView) is { } bookable)
        {
            var allowed = bookable.ToList();
            assignments = assignments.Where(r => allowed.Contains(r.b.BranchId));
        }
        var bookings = await assignments.OrderByDescending(r => r.bc.AssignedAt).Take(MaxVisits).ToListAsync(ct);

        var branches = await clock.BranchesAsync(
            stays.Select(s => s.v.BranchId).Concat(bookings.Select(r => r.b.BranchId)).Distinct(), ct);
        var today = time.GetUtcNow().UtcDateTime.Date;

        var stayRows = stays.Select(s =>
        {
            var open = s.v.GateOutTransactionId is null;
            var view = inYard.GetValueOrDefault(s.v.ContainerVisitId);
            // Same arithmetic as yard.vw_container_in_yard: calendar days between UTC dates.
            var days = view?.DaysInYard
                       ?? (s.InAt is { } inAt ? ((s.OutAt?.UtcDateTime.Date ?? today) - inAt.UtcDateTime.Date).Days : 0);
            return new ContainerStoryVisitResponse(
                s.v.ContainerVisitId, s.v.BranchId, branches.GetValueOrDefault(s.v.BranchId)?.BranchCode,
                s.v.EquipmentTypeCode, s.v.LinePartyCode, s.v.FullEmpty, s.v.ConditionCode, s.v.GradeCode,
                s.v.YardId, s.v.PositionText,
                s.v.GateInTransactionId, s.InEir, s.InMovement, s.InAt,
                s.v.GateOutTransactionId, s.OutEir, s.OutAt,
                Math.Max(days, 0), open, view?.IsHeld == true, s.v.CurrentBookingContainerId, s.v.LastEventAt,
                events[s.v.ContainerVisitId].Select(e => new ContainerStoryEventResponse(
                    e.VisitEventId, e.EventType, e.FromValue, e.ToValue, e.EventAt, e.EventBy, e.ReferenceId, e.Remarks)).ToList());
        }).ToList();

        var bookingRows = bookings.Select(r => new ContainerStoryBookingResponse(
            r.bc.BookingContainerId, r.b.BookingId, r.b.OrderNo, r.b.BranchId, branches.GetValueOrDefault(r.b.BranchId)?.BranchCode,
            r.b.OrderTypeCode, r.b.Status, r.b.LinePartyCode, r.b.CustomerPartyCode,
            r.bc.AssignedAt, r.bc.EndedAt, r.bc.EndReason, r.bc.EndedAt is null)).ToList();

        var current = stayRows.FirstOrDefault(v => v.IsInYard);
        return new ContainerStoryResponse(box, current is not null, current, stayRows, bookingRows);
    }
}

/// <summary><c>Current</c> is the open stay (the box is in the yard now), or null.</summary>
public sealed record ContainerStoryResponse(
    string ContainerNo, bool IsInYard, ContainerStoryVisitResponse? Current,
    IReadOnlyList<ContainerStoryVisitResponse> Visits, IReadOnlyList<ContainerStoryBookingResponse> Bookings);

public sealed record ContainerStoryVisitResponse(
    Guid ContainerVisitId, Guid BranchId, string? BranchCode,
    string? EquipmentTypeCode, string LineCode, string FullEmpty, string? ConditionCode, string? GradeCode,
    Guid? YardId, string? PositionText,
    Guid GateInTransactionId, string? GateInEirNo, string? GateInMovementCode, DateTimeOffset? GateInAt,
    Guid? GateOutTransactionId, string? GateOutEirNo, DateTimeOffset? GateOutAt,
    int DaysInYard, bool IsInYard, bool IsHeld, Guid? CurrentBookingContainerId, DateTimeOffset LastEventAt,
    IReadOnlyList<ContainerStoryEventResponse> Events);

public sealed record ContainerStoryEventResponse(
    long VisitEventId, string EventType, string? FromValue, string? ToValue,
    DateTimeOffset EventAt, Guid? EventBy, Guid? ReferenceId, string? Remarks);

public sealed record ContainerStoryBookingResponse(
    Guid BookingContainerId, Guid BookingId, string OrderNo, Guid BranchId, string? BranchCode,
    string OrderTypeCode, string BookingStatus, string LineCode, string? CustomerCode,
    DateTimeOffset AssignedAt, DateTimeOffset? EndedAt, string? EndReason, bool IsOpen);
