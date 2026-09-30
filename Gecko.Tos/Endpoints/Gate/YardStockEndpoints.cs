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
/// The stock on hand, counted: the same rows as /yard/containers
/// (yard.vw_container_in_yard), tallied by yard, area, equipment type, line,
/// customer and condition, and as pools of type × line × grade × condition.
///
/// KORAKIT locates boxes at yard level (V-6: the position is Vector's area code,
/// not a slot), so there is no bay/row/tier here — a yard, its areas, and counts.
/// Read-only and additive; scoped like the stock list (tos.gate.view per depot).
/// </summary>
internal static class YardStockEndpoints
{
    public static RouteGroupBuilder MapYardStockEndpoints(this RouteGroupBuilder tos)
    {
        tos.MapGet("/yard/stock", StockAsync)
            .WithTags("TOS — gate")
            .RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("The stock on hand, counted by yard, area, type, line, customer, condition and pool");

        return tos;
    }

    private sealed record Box(
        Guid? YardId, string? PositionText, string? EquipmentTypeCode, string LineCode, string? CustomerCode,
        string FullEmpty, string? ConditionCode, string? GradeCode, int Days, bool IsHeld);

    private static async Task<Results<Ok<YardStockResponse>, ValidationProblem, ProblemHttpResult>> StockAsync(
        TosDbContext db, IMasterDataReferences master, ICallerPermissions scope, TimeProvider time, CancellationToken ct,
        Guid? branchId = null, Guid? yardId = null, string? fullEmpty = null)
    {
        if (branchId is { } asked && !scope.HasAt(TosPermissions.GateView, asked))
            return TosScope.OutsideYourBranches("That depot is not one you cover.");

        var rows = db.VwContainerInYards.AsNoTracking();
        if (branchId is not null) rows = rows.Where(v => v.BranchId == branchId);
        if (yardId is not null) rows = rows.Where(v => v.YardId == yardId);
        var load = fullEmpty.Clean();
        if (load is not null)
        {
            if (load is not (GateRules.Full or GateRules.Empty)) return TosSupport.Invalid("fullEmpty", "Use FULL or EMPTY.");
            rows = rows.Where(v => v.FullEmpty == load);
        }
        if (scope.BranchFilter(TosPermissions.GateView) is { } mine)
        {
            var allowed = mine.ToList();
            rows = rows.Where(v => allowed.Contains(v.BranchId));
        }

        // The customer is the booking's: a box in stock is there for one.
        var boxes = await (
            from v in rows
            join bc in db.BookingContainers on v.CurrentBookingContainerId equals bc.BookingContainerId into bcs
            from bc in bcs.DefaultIfEmpty()
            join b in db.Bookings on bc.BookingId equals b.BookingId into bs
            from b in bs.DefaultIfEmpty()
            select new Box(
                v.YardId, v.PositionText, v.EquipmentTypeCode, v.LinePartyCode, b == null ? null : b.CustomerPartyCode,
                v.FullEmpty, v.ConditionCode, v.GradeCode, v.DaysInYard ?? 0, v.IsHeld == true)).ToListAsync(ct);

        var typeCodes = boxes.Select(x => x.EquipmentTypeCode).OfType<string>().Distinct().ToList();
        var types = typeCodes.Count == 0
            ? (IReadOnlyDictionary<string, EquipmentTypeRef>)new Dictionary<string, EquipmentTypeRef>()
            : await master.EquipmentTypesAsync(typeCodes, ct);
        var partyCodes = boxes.Select(x => x.LineCode).Concat(boxes.Select(x => x.CustomerCode).OfType<string>()).Distinct().ToList();
        var parties = partyCodes.Count == 0
            ? (IReadOnlyDictionary<string, PartyRef>)new Dictionary<string, PartyRef>()
            : await master.PartiesAsync(partyCodes, ct);

        // A box with no equipment type (or one MDM no longer knows) adds no TEU — never a guessed size.
        YardStockTally Tally(IEnumerable<Box> set)
        {
            var list = set as IReadOnlyCollection<Box> ?? set.ToList();
            return new YardStockTally(
                Boxes: list.Count,
                Teu: list.Sum(x => x.EquipmentTypeCode is { } c && types.TryGetValue(c, out var t) ? t.Teu : 0m),
                Full: list.Count(x => x.FullEmpty == GateRules.Full),
                Empty: list.Count(x => x.FullEmpty == GateRules.Empty),
                Reefer: list.Count(x => x.EquipmentTypeCode is { } c && types.TryGetValue(c, out var t) && t.IsReefer),
                Held: list.Count(x => x.IsHeld),
                Days0To7: list.Count(x => x.Days <= 7),
                Days8To14: list.Count(x => x.Days is >= 8 and <= 14),
                Days15To30: list.Count(x => x.Days is >= 15 and <= 30),
                DaysOver30: list.Count(x => x.Days > 30),
                MaxDays: list.Count == 0 ? 0 : list.Max(x => x.Days));
        }

        List<YardStockGroupResponse> By(Func<Box, string?> key, bool named = false) =>
            boxes.GroupBy(key)
                .Select(g => new YardStockGroupResponse(g.Key, named && g.Key is { } k ? parties.GetValueOrDefault(k)?.Name : null, Tally(g)))
                .OrderByDescending(g => g.Tally.Boxes).ThenBy(g => g.Code, StringComparer.Ordinal)
                .ToList();

        var yards = boxes.GroupBy(x => x.YardId)
            .Select(g => new YardStockYardResponse(g.Key, Tally(g),
                g.GroupBy(x => x.PositionText)
                    .Select(a => new YardStockGroupResponse(a.Key, null, Tally(a)))
                    .OrderBy(a => a.Code ?? "￿", StringComparer.Ordinal).ToList()))
            .OrderByDescending(y => y.Tally.Boxes).ToList();

        var pools = boxes.GroupBy(x => (x.EquipmentTypeCode, x.LineCode, x.GradeCode, x.ConditionCode))
            .Select(g => new YardStockPoolResponse(
                g.Key.EquipmentTypeCode,
                g.Key.EquipmentTypeCode is { } c ? types.GetValueOrDefault(c)?.SizeCode : null,
                g.Key.LineCode, parties.GetValueOrDefault(g.Key.LineCode)?.Name,
                g.Key.GradeCode, g.Key.ConditionCode, Tally(g)))
            .OrderBy(p => p.LineCode, StringComparer.Ordinal).ThenBy(p => p.EquipmentTypeCode, StringComparer.Ordinal)
            .ThenBy(p => p.GradeCode, StringComparer.Ordinal).ThenBy(p => p.ConditionCode, StringComparer.Ordinal)
            .ToList();

        return TypedResults.Ok(new YardStockResponse(
            time.GetUtcNow(), branchId, yardId, load, Tally(boxes), yards,
            By(x => x.EquipmentTypeCode), By(x => x.LineCode, named: true), By(x => x.CustomerCode, named: true),
            By(x => x.ConditionCode), By(x => x.GradeCode), pools));
    }
}

/// <summary>Counts over a set of boxes in stock. Dwell buckets are whole days in the yard, as the stock list counts them.</summary>
public sealed record YardStockTally(
    int Boxes, decimal Teu, int Full, int Empty, int Reefer, int Held,
    int Days0To7, int Days8To14, int Days15To30, int DaysOver30, int MaxDays);

/// <summary><c>Code</c> is null for boxes with no value on that axis (no area, no grade…). <c>Name</c> is MDM's, for parties.</summary>
public sealed record YardStockGroupResponse(string? Code, string? Name, YardStockTally Tally);

/// <summary>One yard (null = boxes not placed in a yard) with its areas — the position text the gate wrote.</summary>
public sealed record YardStockYardResponse(Guid? YardId, YardStockTally Tally, IReadOnlyList<YardStockGroupResponse> Areas);

public sealed record YardStockPoolResponse(
    string? EquipmentTypeCode, string? SizeCode, string LineCode, string? LineName,
    string? GradeCode, string? ConditionCode, YardStockTally Tally);

public sealed record YardStockResponse(
    DateTimeOffset AsAt, Guid? BranchId, Guid? YardId, string? FullEmpty, YardStockTally Total,
    IReadOnlyList<YardStockYardResponse> Yards,
    IReadOnlyList<YardStockGroupResponse> Types, IReadOnlyList<YardStockGroupResponse> Lines,
    IReadOnlyList<YardStockGroupResponse> Customers, IReadOnlyList<YardStockGroupResponse> Conditions,
    IReadOnlyList<YardStockGroupResponse> Grades, IReadOnlyList<YardStockPoolResponse> Pools);
