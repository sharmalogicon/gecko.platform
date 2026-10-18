using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.SharedKernel;
using Gecko.Tos.Domain;
using Gecko.Tos.Infrastructure.Persistence;
using Gecko.Tos.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Endpoints.Gate;

/// <param name="ConditionCode">MDM container condition; null = unchanged.</param>
/// <param name="GradeCode">MDM container grade; null = unchanged.</param>
public sealed record ChangeConditionRequest(
    [property: Required, MinLength(3), MaxLength(300)] string? Reason,
    [property: MaxLength(20)] string? ConditionCode = null,
    [property: MaxLength(20)] string? GradeCode = null);

/// <param name="Remarks">Free text; null or "" clears it.</param>
public sealed record EmrRemarksRequest([property: MaxLength(500)] string? Remarks);

public sealed record RenumberContainerRequest(
    [property: Required, MaxLength(11)] string? NewContainerNo,
    [property: Required, MinLength(3), MaxLength(300)] string? Reason,
    // The registry row's rowVersion as the clerk read it (GET /api/master/containers/{no}).
    string? RowVersion = null);

/// <summary>A stay in the yard after a change on the Container Master. <see cref="HoldsApplied"/>: holds this change put on.</summary>
public sealed record YardStayResponse(
    Guid ContainerVisitId, string ContainerNo, Guid BranchId, string? ConditionCode, string? GradeCode, string? EmrRemarks,
    DateTimeOffset LastEventAt, IReadOnlyList<string> HoldsApplied);

/// <param name="Moved">Rows moved to the new number in TOS, by table. Revenue's follow by the outbox a moment later.</param>
/// <param name="RowVersion">The registry row's new rowVersion.</param>
public sealed record RenumberContainerResponse(string OldContainerNo, string NewContainerNo, string? RowVersion, IReadOnlyDictionary<string, int> Moved);

/// <summary>
/// The Container Master's three writes (web.tos docs/CONTAINER_MASTER_FOR_API.md, owner 2026-10-12): a standing box's
/// condition / grade (Vector "Update Status"), its EMR remarks, and correcting its number. The first two are TOS's — the
/// stay carries the current condition — under <c>tos.yard.manage</c>. The renumber is the registry's act
/// (<c>mdm.equipment.manage</c>) and lives here because the box's TOS rows must move with it: TOS moves them, calls the
/// registry last, and tells Revenue by the outbox (owner: everything moves, issued documents too).
/// </summary>
internal static class ContainerMasterEndpoints
{
    public const string Renumbered = "ContainerRenumbered";
    private const string DamagedEvent = "SURVEY_DAMAGED";

    public static RouteGroupBuilder MapContainerMasterEndpoints(this RouteGroupBuilder tos)
    {
        tos.MapPost("/visits/{id:guid}/condition", ChangeConditionAsync).WithTags("TOS — yard")
            .RequireBranchPermission(TosPermissions.YardManage)
            .Validate<ChangeConditionRequest>()
            .WithSummary("Change the condition / grade of a box standing in the yard")
            .WithDescription("Reason required. 409 when the box has gated out. A damaged condition (unserviceable, damage flag) puts on the depot's damage hold, as a survey does.");

        tos.MapPut("/visits/{id:guid}/emr-remarks", EmrRemarksAsync).WithTags("TOS — yard")
            .RequireBranchPermission(TosPermissions.YardManage)
            .Validate<EmrRemarksRequest>()
            .WithSummary("Set what the EMR desk is waiting on for this stay (null or \"\" clears it)")
            .WithDescription("Shown on the Container Master and the inquiry, and at gate-out as information. 409 when the box has gated out.");

        tos.MapPost("/containers/{containerNo}/renumber", RenumberAsync).WithTags("TOS — yard")
            .RequirePermission(TosPermissions.EquipmentManage)
            .Validate<RenumberContainerRequest>()
            .WithSummary("Correct a container number: the registry row, and every stay, booking, hold, EIR, receipt and invoice line of the box")
            .WithDescription("409 when the new number is registered or in use (in the yard, on a booking, held). Revenue's rows follow by the outbox.");

        return tos;
    }

    private static async Task<Results<Ok<YardStayResponse>, NotFound, ValidationProblem, ProblemHttpResult>> ChangeConditionAsync(
        Guid id, ChangeConditionRequest request, TosDbContext db, IMasterDataReferences master, ITenantContext caller,
        ICallerPermissions scope, TimeProvider time, CancellationToken ct)
    {
        var visit = await db.ContainerVisits.SingleOrDefaultAsync(v => v.ContainerVisitId == id, ct);
        if (visit is null) return TypedResults.NotFound();
        if (!scope.HasAt(TosPermissions.YardManage, visit.BranchId)) return TosScope.OutsideYourBranches($"{visit.ContainerNo} is at a depot you do not cover.");
        if (visit.GateOutTransactionId is not null)
            return TosSupport.Conflict($"{visit.ContainerNo} has left the yard on that stay.", "A departed box's condition is history.");

        var condition = request.ConditionCode.Clean()?.ToUpperInvariant();
        var grade = request.GradeCode.Clean()?.ToUpperInvariant();
        if (condition is null && grade is null) return TosSupport.Invalid("conditionCode", "Say the new condition, the new grade, or both.");
        var conditionRef = condition is null ? null : (await master.ContainerConditionsAsync([condition], ct)).GetValueOrDefault(condition);
        if (condition is not null && conditionRef is not { IsActive: true })
            return TosSupport.Invalid("conditionCode", $"'{condition}' is not a container condition of this depot.");
        if (grade is not null && (await master.ContainerGradesAsync([grade], ct)).GetValueOrDefault(grade) is not { IsActive: true })
            return TosSupport.Invalid("gradeCode", $"'{grade}' is not a container grade of this depot.");

        var now = time.GetUtcNow();
        var reason = request.Reason!.Trim();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (condition is not null && condition != visit.ConditionCode)
        {
            AddEvent(db, visit, "CONDITION_CHANGE", visit.ConditionCode, condition, reason, now, caller);
            visit.ConditionCode = condition;
        }
        if (grade is not null && grade != visit.GradeCode)
        {
            AddEvent(db, visit, "GRADE_CHANGE", visit.GradeCode, grade, reason, now, caller);
            visit.GradeCode = grade;
        }

        // Owner 2026-10-12: a box found damaged where it stands picks up the hold its depot set for damage, as a survey's does.
        var applied = new List<string>();
        if (conditionRef is { IsDamaged: true })
            foreach (var hold in (await master.ActiveHoldsAsync(ct)).Where(h => string.Equals(h.AutoApplyOnEvent, DamagedEvent, StringComparison.OrdinalIgnoreCase)))
            {
                if (await db.ContainerHolds.AnyAsync(h => h.ContainerNo == visit.ContainerNo && h.HoldCode == hold.HoldCode && h.ReleasedAt == null, ct))
                    continue;
                db.ContainerHolds.Add(new ContainerHold
                {
                    TenantId = visit.TenantId,
                    ContainerNo = visit.ContainerNo,
                    HoldId = hold.HoldId,
                    HoldCode = hold.HoldCode,
                    AppliedAt = now,
                    AppliedBy = caller.UserId(),
                    ApplyReason = $"Condition changed to {condition} in the yard: {reason}",
                    Source = "AUTO",
                });
                AddEvent(db, visit, "HOLD_APPLIED", null, hold.HoldCode, $"Condition {condition}", now, caller);
                applied.Add(hold.HoldCode);
            }

        visit.LastEventAt = now;
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await tx.CommitAsync(ct);
        return TypedResults.Ok(Response(visit, applied));
    }

    private static async Task<Results<Ok<YardStayResponse>, NotFound, ValidationProblem, ProblemHttpResult>> EmrRemarksAsync(
        Guid id, EmrRemarksRequest request, TosDbContext db, ITenantContext caller, ICallerPermissions scope, TimeProvider time,
        CancellationToken ct)
    {
        var visit = await db.ContainerVisits.SingleOrDefaultAsync(v => v.ContainerVisitId == id, ct);
        if (visit is null) return TypedResults.NotFound();
        if (!scope.HasAt(TosPermissions.YardManage, visit.BranchId)) return TosScope.OutsideYourBranches($"{visit.ContainerNo} is at a depot you do not cover.");
        if (visit.GateOutTransactionId is not null)
            return TosSupport.Conflict($"{visit.ContainerNo} has left the yard on that stay.", "EMR remarks belong to a box standing in the yard.");

        var remarks = string.IsNullOrWhiteSpace(request.Remarks) ? null : request.Remarks.Trim();
        if (remarks == visit.EmrRemarks) return TypedResults.Ok(Response(visit, []));
        var now = time.GetUtcNow();
        AddEvent(db, visit, "EMR_REMARKS", Short(visit.EmrRemarks), Short(remarks), remarks ?? "(cleared)", now, caller);
        visit.EmrRemarks = remarks;
        visit.LastEventAt = now;
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(Response(visit, []));
    }

    /// <summary>
    /// The box's number, corrected everywhere TOS holds it — stays, booking places, holds, EIRs, surveys, coupons, pick-ups,
    /// reservations, reefer sessions — then the registry (last, so its refusal rolls TOS back), then Revenue by the outbox.
    /// </summary>
    private static async Task<Results<Ok<RenumberContainerResponse>, NotFound, ValidationProblem, ProblemHttpResult>> RenumberAsync(
        string containerNo, RenumberContainerRequest request, TosDbContext db, IMasterDataReferences master, IContainerRenumbering registry,
        ITenantContext caller, TimeProvider time, CancellationToken ct)
    {
        var from = ContainerNumber.Normalise(containerNo);
        var to = ContainerNumber.Normalise(request.NewContainerNo ?? "");
        if (!ContainerNumber.IsWellFormed(to))
            return TosSupport.Invalid("newContainerNo", $"'{request.NewContainerNo}' is not a container number (4 to 11 letters or digits).");
        if (!ContainerNumber.IsValid(to) && await master.GetBoolSettingAsync(TosSettingKeys.EnforceCheckDigit, null, false, ct))
            return TosSupport.Invalid("newContainerNo", $"{to} fails the ISO 6346 check digit (expected {ContainerNumber.CheckDigitOf(to)}). Check the plate.");
        if (to == from) return TosSupport.Invalid("newContainerNo", "That is the number it already has.");

        // The new number must be free in TOS as well as in the registry: a box in the yard, on a booking or held under it is another box.
        if (await db.ContainerVisits.AnyAsync(v => v.ContainerNo == to && v.GateOutTransactionId == null, ct)
            || await db.BookingContainers.AnyAsync(x => x.ContainerNo == to && x.EndedAt == null, ct)
            || await db.ContainerHolds.AnyAsync(h => h.ContainerNo == to && h.ReleasedAt == null, ct))
            return TosSupport.Conflict($"{to} is in use: in the yard, on a booking, or held.", "Renumbering would merge two boxes. Check both plates.");

        var reason = request.Reason!.Trim();
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var moved = new Dictionary<string, int>
        {
            ["containerVisits"] = await db.ContainerVisits.Where(x => x.ContainerNo == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContainerNo, to), ct),
            ["bookingContainers"] = await db.BookingContainers.Where(x => x.ContainerNo == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContainerNo, to), ct),
            ["holds"] = await db.ContainerHolds.Where(x => x.ContainerNo == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContainerNo, to), ct),
            ["gateTransactions"] = await db.GateTransactions.Where(x => x.ContainerNo == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContainerNo, to), ct),
            ["surveys"] = await db.Surveys.Where(x => x.ContainerNo == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContainerNo, to), ct),
            ["coupons"] = await db.GateAuthorizations.Where(x => x.ContainerNo == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContainerNo, to), ct),
            ["pickups"] = await db.VisitPickups.Where(x => x.ContainerNo == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContainerNo, to), ct),
            ["reservations"] = await db.BoxReservations.Where(x => x.ContainerNo == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContainerNo, to), ct),
            ["reeferSessions"] = await db.ReeferPowerSessions.Where(x => x.ContainerNo == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContainerNo, to), ct),
        };

        // The stay it is on (else its latest) says so in the activity log.
        var visit = await db.ContainerVisits.Where(v => v.ContainerNo == to)
            .OrderByDescending(v => v.GateOutTransactionId == null).ThenByDescending(v => v.LastEventAt).FirstOrDefaultAsync(ct);
        if (visit is not null) AddEvent(db, visit, "RENUMBERED", from, to, reason, now, caller);
        await db.SaveChangesAsync(ct);

        ContainerRenumberResult done;
        if (ContainerNumber.IsPlaceholder(from))
        {
            // A NOID number the gate issued (owner 2026-10-18) was never registered: nothing to rename there. The real number is
            // registered as the gate would have (it may be known already, from an earlier stay), and Revenue moves its rows.
            await GateEndpoints.RegisterAsync(master, to, visit?.EquipmentTypeCode, visit?.LinePartyCode, ct);
            var real = (await master.ContainersAsync([to], ct)).GetValueOrDefault(to);
            done = new ContainerRenumberResult(ContainerRenumberOutcome.Renumbered, real?.ContainerId ?? visit?.ContainerVisitId ?? Guid.NewGuid());
        }
        else
            // Base64: case matters, so trimmed only (Clean() upper-cases).
            done = await registry.RenumberAsync(from, to, reason, string.IsNullOrWhiteSpace(request.RowVersion) ? null : request.RowVersion.Trim(), ct);
        switch (done.Outcome)
        {
            case ContainerRenumberOutcome.NotFound: return TypedResults.NotFound();
            case ContainerRenumberOutcome.NewNumberTaken: return TosSupport.Conflict($"{to} is already in the container registry.", "Renumbering would merge two boxes. Check both plates.");
            case ContainerRenumberOutcome.Stale: return TosSupport.Conflict($"{from} was changed by someone else.", "Read it again and repeat the renumber.");
        }

        // Revenue moves its stays, charges, receipt and invoice lines (ADR-007: it never reads gecko_tos).
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO outbox.message (tenant_id, aggregate_type, aggregate_id, message_type, payload_json) VALUES ({0}, 'CONTAINER', {1}, {2}, {3})",
            [caller.TenantId(), done.ContainerId!.Value, Renumbered,
             JsonSerializer.Serialize(new { oldContainerNo = from, newContainerNo = to, reason })], ct);
        await tx.CommitAsync(ct);
        return TypedResults.Ok(new RenumberContainerResponse(from, to, done.RowVersion, moved));
    }

    private static string? Short(string? text) => text is { Length: > 100 } ? text[..97] + "..." : text;

    private static void AddEvent(TosDbContext db, ContainerVisit visit, string type, string? from, string? to, string? remarks, DateTimeOffset at, ITenantContext caller) =>
        db.VisitEvents.Add(new VisitEvent
        {
            TenantId = visit.TenantId,
            ContainerVisitId = visit.ContainerVisitId,
            EventType = type,
            FromValue = from,
            ToValue = to,
            EventAt = at,
            EventBy = caller.UserId(),
            Remarks = remarks is { Length: > 500 } ? remarks[..500] : remarks,
        });

    private static YardStayResponse Response(ContainerVisit v, IReadOnlyList<string> holds) =>
        new(v.ContainerVisitId, v.ContainerNo, v.BranchId, v.ConditionCode, v.GradeCode, v.EmrRemarks, v.LastEventAt, holds);
}
