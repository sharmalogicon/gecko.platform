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

namespace Gecko.Tos.Endpoints.Reports;

/// <summary>
/// TOS reports — read-only aggregates for /reports/operational.
///
///   GET /reports/gate-moves   the moves at one depot over a range of depot days,
///                             totalled by day, movement, customer, line and type.
///
/// A move is what the dashboards count (DashboardQueries): a COMPLETED EIR on a
/// truck visit the gate recorded, days in the depot's time zone. The EIRs
/// themselves are the gate register's (GET /gate/transactions?from=&amp;to=).
/// </summary>
internal static class ReportEndpoints
{
    /// <summary>A year of depot days, so one report cannot scan the whole history.</summary>
    public const int MaxDays = 366;

    public static RouteGroupBuilder MapReportEndpoints(this RouteGroupBuilder tos)
    {
        var reports = tos.MapGroup("/reports").WithTags("TOS — reports");

        reports.MapGet("/gate-moves", GateMovesAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("Gate movements at a depot over a range of depot days: by day, movement, customer, line and type");

        return tos;
    }

    private static async Task<Results<Ok<GateMovesReportResponse>, NotFound, ValidationProblem, ProblemHttpResult>> GateMovesAsync(
        Guid? branchId, DateOnly? from, DateOnly? to, TosDbContext db, BranchClock clock, IMasterDataReferences master,
        ICallerPermissions scope, CancellationToken ct)
    {
        if (branchId is null) return TosSupport.Invalid("branchId", "Which depot? Days are the depot's own.");
        if (from is null) return TosSupport.Invalid("from", "The first day of the report.");
        if (to is null) return TosSupport.Invalid("to", "The last day of the report.");
        if (to < from) return TosSupport.Invalid("to", "The last day is before the first.");
        if (to.Value.DayNumber - from.Value.DayNumber + 1 > MaxDays)
            return TosSupport.Invalid("to", $"At most {MaxDays} days in one report.");

        var branch = (await clock.BranchesAsync([branchId.Value], ct)).GetValueOrDefault(branchId.Value);
        if (branch is null) return TypedResults.NotFound();
        if (!scope.HasAt(TosPermissions.GateView, branch.BranchId))
            return TosScope.OutsideYourBranches("That depot is not one you cover.");

        var window = new DashboardQueries.Window(branch, from.Value, to.Value);
        var moves = await DashboardQueries.RunAsync<DashboardQueries.GateMoves>(db, DashboardQueries.GateMovesSql, window, ct);
        var voided = await db.Database.SqlQueryRaw<int>(DashboardQueries.VoidedMovesSql, window.Parameters()).SingleAsync(ct);

        var typeCodes = moves.Select(m => m.EquipmentTypeCode).OfType<string>().Distinct().ToList();
        var types = typeCodes.Count == 0
            ? (IReadOnlyDictionary<string, EquipmentTypeRef>)new Dictionary<string, EquipmentTypeRef>()
            : await master.EquipmentTypesAsync(typeCodes, ct);
        var partyCodes = moves.Select(m => m.CustomerCode).OfType<string>().Concat(moves.Select(m => m.LineCode)).Distinct().ToList();
        var parties = partyCodes.Count == 0
            ? (IReadOnlyDictionary<string, PartyRef>)new Dictionary<string, PartyRef>()
            : await master.PartiesAsync(partyCodes, ct);

        // A move whose type MDM does not know adds no TEU — never a guessed size.
        decimal Teu(IEnumerable<DashboardQueries.GateMoves> set) =>
            set.Sum(m => m.EquipmentTypeCode is { } c && types.TryGetValue(c, out var t) ? t.Teu * m.Moves : 0m);
        int Count(IEnumerable<DashboardQueries.GateMoves> set, string? direction = null, string? load = null) =>
            set.Where(m => (direction is null || m.Direction == direction) && (load is null || m.FullEmpty == load)).Sum(m => m.Moves);

        GateMovesTally Tally(IEnumerable<DashboardQueries.GateMoves> source)
        {
            var set = source.ToList();
            return new GateMovesTally(
                Count(set), Count(set, GateRules.In), Count(set, GateRules.Out),
                Count(set, GateRules.In, GateRules.Full), Count(set, GateRules.In, GateRules.Empty),
                Count(set, GateRules.Out, GateRules.Full), Count(set, GateRules.Out, GateRules.Empty), Teu(set));
        }

        List<GateMovesGroupResponse> By(Func<DashboardQueries.GateMoves, string?> key, bool named) =>
            moves.GroupBy(key)
                .Select(g => new GateMovesGroupResponse(g.Key, named && g.Key is { } k ? parties.GetValueOrDefault(k)?.Name : null, Tally(g)))
                .OrderByDescending(g => g.Tally.Moves).ThenBy(g => g.Code, StringComparer.Ordinal)
                .ToList();

        // Every day in the range, zero-filled, so a quiet Sunday is a row.
        var days = Enumerable.Range(0, to.Value.DayNumber - from.Value.DayNumber + 1)
            .Select(i => from.Value.AddDays(i))
            .Select(d => new GateMovesDayResponse(d, Tally(moves.Where(m => m.Day == d))))
            .ToList();

        var movements = moves.GroupBy(m => (m.MovementCode, m.Direction, m.FullEmpty))
            .Select(g => new GateMovesMovementResponse(g.Key.MovementCode, g.Key.Direction, g.Key.FullEmpty, g.Sum(m => m.Moves), Teu(g)))
            .OrderBy(m => m.Direction, StringComparer.Ordinal).ThenByDescending(m => m.Moves)
            .ToList();

        return TypedResults.Ok(new GateMovesReportResponse(
            branch.BranchId, branch.BranchCode, from.Value, to.Value, Tally(moves), voided,
            days, movements, By(m => m.CustomerCode, named: true), By(m => m.LineCode, named: true), By(m => m.EquipmentTypeCode, named: false)));
    }
}

/// <summary>Moves counted: all, in, out, and in/out split full / empty; TEU from MDM's equipment types.</summary>
public sealed record GateMovesTally(int Moves, int In, int Out, int FullIn, int EmptyIn, int FullOut, int EmptyOut, decimal Teu);

public sealed record GateMovesDayResponse(DateOnly Day, GateMovesTally Tally);

public sealed record GateMovesMovementResponse(string MovementCode, string Direction, string FullEmpty, int Moves, decimal Teu);

/// <summary><c>Code</c> null = moves with nothing on that axis. <c>Name</c> is MDM's, for parties.</summary>
public sealed record GateMovesGroupResponse(string? Code, string? Name, GateMovesTally Tally);

public sealed record GateMovesReportResponse(
    Guid BranchId, string BranchCode, DateOnly From, DateOnly To, GateMovesTally Total, int Voided,
    IReadOnlyList<GateMovesDayResponse> Days, IReadOnlyList<GateMovesMovementResponse> Movements,
    IReadOnlyList<GateMovesGroupResponse> Customers, IReadOnlyList<GateMovesGroupResponse> Lines,
    IReadOnlyList<GateMovesGroupResponse> Types);
