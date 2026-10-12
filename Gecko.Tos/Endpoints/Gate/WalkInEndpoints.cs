using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.SharedKernel;
using Gecko.Tos.Application;
using Gecko.Tos.Domain;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Endpoints.Gate;

/// <summary>
/// Walk-in trucks (WALK_IN_ORDERS_PLAN §3, owner 2026-10-12): a customer's truck that drops off or picks up a box with no
/// paperwork. The clerk answers Drop off / Pick up and Empty / Full; the walk-in order type those answers name comes from
/// master data (never a code written here — every tenant names its own), and the trip save raises ONE booking of it for
/// the truck. Both reads are gate-scoped: a gate clerk has no commercial master-data permission.
/// </summary>
internal static class WalkInEndpoints
{
    /// <summary>The picker searches at most this many yard boxes: the clerk types a container number to narrow it.</summary>
    private const int MaxBoxes = 500;

    public static RouteGroupBuilder MapWalkInEndpoints(this RouteGroupBuilder tos)
    {
        var gate = tos.MapGroup("/gate").WithTags("TOS — gate");

        gate.MapGet("/walk-in-order-types", OrderTypesAsync)
            .RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("The walk-in order types: one per Drop off / Pick up × Empty / Full")
            .WithDescription("Master data's active walk-in order types (gecko_master 34). The clerk's two answers pick one: direction IN = drop off, OUT = pick up.");

        gate.MapGet("/walk-in-boxes", BoxesAsync)
            .RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("The yard boxes a walk-in truck may pick up")
            .WithDescription("fullEmpty = EMPTY or FULL (the clerk's answer). Boxes in the yard at the depot on no open order, not held by another truck's clerk, " +
                             "of a grade the walk-in order type releases. Search matches the container number or the position.");

        return tos;
    }

    private static async Task<Ok<List<WalkInOrderTypeResponse>>> OrderTypesAsync(IMasterDataReferences master, CancellationToken ct) =>
        TypedResults.Ok((await master.WalkInOrderTypesAsync(ct))
            .Select(p => (Plan: p, Step: p.WalkInStep!))
            .OrderBy(x => x.Step.Direction, StringComparer.Ordinal).ThenBy(x => x.Step.FullEmpty, StringComparer.Ordinal)
            .Select(x => new WalkInOrderTypeResponse(x.Plan.OrderTypeCode, x.Plan.DescriptionEn, x.Plan.DescriptionLocal, x.Step.Direction,
                x.Step.FullEmpty, x.Step.MovementCode, x.Plan.RulesOrNone.MaxBoxesPerTruck, x.Plan.RulesOrNone.PaymentRule,
                x.Plan.RulesOrNone.ReleaseGradeCodes))
            .ToList());

    /// <summary>The walk-in order type the clerk's two answers name; null when the tenant has none for them.</summary>
    internal static async Task<OrderTypePlanRef?> OrderTypeForAsync(IMasterDataReferences master, string direction, string fullEmpty, CancellationToken ct) =>
        (await master.WalkInOrderTypesAsync(ct)).FirstOrDefault(p => p.WalkInStep!.Direction == direction && p.WalkInStep.FullEmpty == fullEmpty);

    private static async Task<Results<Ok<PagedResult<WalkInBoxResponse>>, ValidationProblem, ProblemHttpResult>> BoxesAsync(
        [AsParameters] ListQuery query, TosDbContext db, IMasterDataReferences master, ICallerPermissions scope, TimeProvider time, CancellationToken ct,
        Guid? branchId = null, string? fullEmpty = null, Guid? draftId = null, string[]? excludeContainerNos = null)
    {
        if (branchId is null) return TosSupport.Invalid("branchId", "Which gate? A barrier belongs to a depot.");
        if (!scope.HasAt(TosPermissions.GateView, branchId.Value)) return TosScope.OutsideYourBranches("You cannot read that depot's gate.");
        var load = fullEmpty.Clean()?.ToUpperInvariant();
        if (load is not (GateRules.Full or GateRules.Empty)) return TosSupport.Invalid("fullEmpty", "Use EMPTY or FULL: what the truck picks up.");
        var orderType = await OrderTypeForAsync(master, GateRules.Out, load, ct);
        if (orderType is null)
            return TosSupport.Invalid("fullEmpty", $"This tenant has no walk-in order type for a {load} pick-up. Set one up in master data (order type rules).");

        var excluded = (excludeContainerNos ?? []).Select(ContainerNumber.Normalise).ToList();
        var rows = db.VwContainerInYards.AsNoTracking()
            .Where(v => v.BranchId == branchId && v.FullEmpty == load && !excluded.Contains(v.ContainerNo)
                        // On an open order: it leaves on that order, not as a walk-in.
                        && !db.BookingContainers.Any(x => x.ContainerNo == v.ContainerNo && x.EndedAt == null && x.DeletedAt == null));
        if (query.Search.Clean() is { } q)
        {
            var box = ContainerNumber.Normalise(q);
            rows = rows.Where(v => v.ContainerNo.Contains(box) || v.PositionText!.Contains(q));
        }

        var candidates = await rows.OrderBy(v => v.GateInAt).Take(MaxBoxes).ToListAsync(ct);
        var rules = orderType.RulesOrNone;
        candidates = candidates.Where(v => rules.ReleasesGrade(v.GradeCode)).ToList();

        // A box another truck's clerk holds (Record, GATE_IN_BIG_SAVE §1) is not offered.
        var now = time.GetUtcNow();
        var numbers = candidates.Select(v => v.ContainerNo).ToList();
        var held = (await db.BoxReservations.AsNoTracking()
                .Where(h => h.ReleasedAt == null && h.ExpiresAt > now && h.DraftId != draftId && h.ContainerNo != null && numbers.Contains(h.ContainerNo))
                .Select(h => h.ContainerNo!).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var picked = candidates.Where(v => !held.Contains(v.ContainerNo)).ToList();

        var page = Math.Max(query.Page ?? 1, 1);
        var size = Math.Clamp(query.PageSize ?? 50, 1, 200);
        var items = picked.Skip((page - 1) * size).Take(size)
            .Select(v => new WalkInBoxResponse(v.ContainerNo, v.EquipmentTypeCode, v.LinePartyCode, v.FullEmpty, v.GradeCode, v.ConditionCode,
                v.PositionText, v.GateInAt, v.DaysInYard, v.IsHeld == true))
            .ToList();
        return TypedResults.Ok(new PagedResult<WalkInBoxResponse>(items, page, size, picked.Count));
    }
}
