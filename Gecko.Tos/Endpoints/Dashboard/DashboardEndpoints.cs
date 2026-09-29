using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.SharedKernel;
using Gecko.Tos.Application;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Gecko.Tos.Endpoints.Dashboard;

/// <summary>
/// The two TOS dashboards: /dashboard/overview and /dashboard/gate-traffic.
/// Read-only aggregates over the gate, for one depot and one depot day.
///
/// Built for the live steady state: a tenant migrated from Vector brings only the boxes
/// in the yard (their current stay), so these read near-zero until the pilot
/// records real moves — and nothing is seeded to make them look busier.
/// Appointments and gate lanes have no model yet, so they are not here at all.
/// </summary>
internal static class DashboardEndpoints
{
    private const int RecentOnOverview = 10;
    private const int RecentOnGateTraffic = 15;

    public static RouteGroupBuilder MapDashboardEndpoints(this RouteGroupBuilder tos)
    {
        var dashboard = tos.MapGroup("/dashboard").WithTags("TOS — dashboards");

        // A gate clerk sees their own depot's dashboard: branch-scoped tos.gate.view opens it.
        dashboard.MapGet("/overview", OverviewAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("The depot day at a glance: gate KPIs with trends, 12 months of moves, lines, closing voyages");
        dashboard.MapGet("/gate-traffic", GateTrafficAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("One depot day at the gate: trucks, turnaround, moves per hour");

        return tos;
    }

    private static async Task<Results<Ok<DashboardOverviewResponse>, NotFound, ProblemHttpResult>> OverviewAsync(
        Guid branchId, TosDbContext db, BranchClock clock, IMasterDataReferences masterData, ICallerPermissions scope,
        TimeProvider time, CancellationToken ct, DateOnly? date = null)
    {
        var branch = (await clock.BranchesAsync([branchId], ct)).GetValueOrDefault(branchId);
        if (branch is null) return TypedResults.NotFound();
        if (!scope.HasAt(TosPermissions.GateView, branchId)) return OutsideYourBranches();
        var day = date ?? clock.Today(branch);

        var eightDays = new DashboardQueries.Window(branch, day.AddDays(-7), day);
        var daily = await DashboardQueries.RunAsync<DashboardQueries.DailyMoves>(db, DashboardQueries.DailyMovesSql, eightDays, ct);
        var trucks = await DashboardQueries.RunAsync<DashboardQueries.DailyTrucks>(db, DashboardQueries.DailyTrucksSql, eightDays, ct);

        var firstMonth = new DateOnly(day.Year, day.Month, 1).AddMonths(-11);
        var monthly = await DashboardQueries.RunAsync<DashboardQueries.MonthlyMoves>(
            db, DashboardQueries.MonthlyMovesSql, new DashboardQueries.Window(branch, firstMonth, day), ct);
        var byLine = await DashboardQueries.RunAsync<DashboardQueries.LineMoves>(
            db, DashboardQueries.MovesByLineSql, new DashboardQueries.Window(branch, day.AddDays(-6), day), ct);
        var closing = await DashboardQueries.ClosingVoyagesAsync(db, branch.BranchId, time.GetUtcNow(), ct);
        var recent = await DashboardQueries.RecentAsync(db, new DashboardQueries.Window(branch, day, day), RecentOnOverview, ct);

        // ── KPIs: 8 depot days ending on the day asked for, oldest first ─────
        var days = Enumerable.Range(0, 8).Select(i => day.AddDays(i - 7)).ToList();
        DailyTrend Trend(Func<DashboardQueries.DailyMoves, bool> which)
        {
            var counts = days.Select(d => daily.Where(r => r.Day == d && which(r)).Sum(r => r.Moves)).ToList();
            return new DailyTrend(counts[7], counts[6], counts);
        }
        var turnaround = days.Select(d => Minutes(trucks.SingleOrDefault(t => t.Day == d)?.AvgTurnMinutes)).ToList();

        var kpis = new DashboardKpis(
            GateTransactions: Trend(_ => true),
            TruckTurnaroundMinutes: new DailyAverageTrend(turnaround[7], turnaround[6], turnaround),
            EirOut: Trend(r => r.Direction == "OUT"),
            EirIn: Trend(r => r.Direction == "IN"));

        // ── 12 depot months, zero-filled ────────────────────────────────────
        var months = Enumerable.Range(0, 12).Select(i => firstMonth.AddMonths(i))
            .Select(m => new MonthlyMovesResponse(
                $"{m:yyyy-MM}",
                monthly.SingleOrDefault(r => r.Year == m.Year && r.Month == m.Month)?.Moves ?? 0))
            .ToList();

        // ── the day, full / empty and TEU ───────────────────────────────────
        var today = daily.Where(r => r.Day == day).ToList();
        var typeCodes = today.Select(r => r.EquipmentTypeCode).OfType<string>().Distinct().ToList();
        var types = typeCodes.Count == 0
            ? (IReadOnlyDictionary<string, EquipmentTypeRef>)new Dictionary<string, EquipmentTypeRef>()
            : await masterData.EquipmentTypesAsync(typeCodes, ct);
        // A move with no equipment type (or one MDM no longer knows) adds no TEU — never a guessed size.
        var teuMoved = today.Sum(r => r.EquipmentTypeCode is { } code && types.TryGetValue(code, out var type) ? type.Teu * r.Moves : 0m);
        int Count(string direction, string fullEmpty) =>
            today.Where(r => r.Direction == direction && r.FullEmpty == fullEmpty).Sum(r => r.Moves);

        var summary = new DaySummaryResponse(
            EmptyIn: Count("IN", "EMPTY"), EmptyOut: Count("OUT", "EMPTY"),
            LadenIn: Count("IN", "FULL"), LadenOut: Count("OUT", "FULL"),
            TeuMoved: teuMoved,
            TeuCapacity: await masterData.YardCapacityTeuAsync(branch.BranchId, ct));

        // ── names from MDM, by code ─────────────────────────────────────────
        var lines = byLine.Count == 0
            ? (IReadOnlyDictionary<string, PartyRef>)new Dictionary<string, PartyRef>()
            : await masterData.PartiesAsync(byLine.Select(l => l.LineCode), ct);
        var vessels = closing.Count == 0
            ? (IReadOnlyDictionary<string, VesselRef>)new Dictionary<string, VesselRef>()
            : await masterData.VesselsAsync(closing.Select(c => c.VesselCode), ct);

        var now = time.GetUtcNow();
        return TypedResults.Ok(new DashboardOverviewResponse(
            day, branch.BranchId, kpis, months, summary,
            byLine.Select(l => new LineMovesResponse(l.LineCode, lines.GetValueOrDefault(l.LineCode)?.Name, l.Moves)).ToList(),
            closing.Select(c => new ClosingVoyageResponse(
                c.VesselCallId,
                c.VoyageOut ?? c.VoyageIn ?? c.CallRef,
                vessels.GetValueOrDefault(c.VesselCode)?.VesselName,
                c.CutoffAt,
                Math.Round((decimal)(c.CutoffAt - now).TotalHours, 1),
                FullPct: Percent(c.FullIn, c.Required),
                EmptyPct: Percent(c.EmptyOut, c.Required))).ToList(),
            recent.Select(r => new RecentTransactionResponse(
                r.GateTransactionId, r.ContainerNo, r.IsoCode, r.MovementCode, r.Direction,
                r.LineCode, r.TruckPlate, r.At, r.Status)).ToList()));
    }

    private static async Task<Results<Ok<GateTrafficResponse>, NotFound, ProblemHttpResult>> GateTrafficAsync(
        Guid branchId, TosDbContext db, BranchClock clock, ICallerPermissions scope, CancellationToken ct, DateOnly? date = null)
    {
        var branch = (await clock.BranchesAsync([branchId], ct)).GetValueOrDefault(branchId);
        if (branch is null) return TypedResults.NotFound();
        if (!scope.HasAt(TosPermissions.GateView, branchId)) return OutsideYourBranches();
        var day = date ?? clock.Today(branch);

        var theDay = new DashboardQueries.Window(branch, day, day);
        var hourly = await DashboardQueries.RunAsync<DashboardQueries.HourlyMoves>(db, DashboardQueries.HourlyMovesSql, theDay, ct);
        var trucks = await DashboardQueries.RunAsync<DashboardQueries.DailyTrucks>(
            db, DashboardQueries.DailyTrucksSql, new DashboardQueries.Window(branch, day.AddDays(-1), day), ct);
        var recent = await DashboardQueries.RecentAsync(db, theDay, RecentOnGateTraffic, ct);

        var buckets = Enumerable.Range(0, 24)
            .Select(h => new HourlyMovesResponse(h, hourly.SingleOrDefault(r => r.Hour == h)?.Moves ?? 0))
            .ToList();
        var moves = buckets.Sum(b => b.Moves);
        var activeHours = buckets.Count(b => b.Moves > 0);
        var peak = buckets.Where(b => b.Moves > 0).OrderByDescending(b => b.Moves).ThenBy(b => b.Hour).FirstOrDefault();

        var todayTrucks = trucks.SingleOrDefault(t => t.Day == day);
        var yesterdayTrucks = trucks.SingleOrDefault(t => t.Day == day.AddDays(-1));

        return TypedResults.Ok(new GateTrafficResponse(
            day, branch.BranchId,
            new GateTrafficKpis(
                new TodayVsPrevious(todayTrucks?.TrucksIn ?? 0, yesterdayTrucks?.TrucksIn ?? 0),
                new AverageTodayVsPrevious(Minutes(todayTrucks?.AvgTurnMinutes), Minutes(yesterdayTrucks?.AvgTurnMinutes)),
                ThroughputPerHour: activeHours == 0 ? 0m : Math.Round((decimal)moves / activeHours, 1),
                PeakHour: peak is null ? null : $"{peak.Hour:00}:00"),
            buckets,
            recent.Select(r => new GateActivityResponse(
                r.GateTransactionId, r.ContainerNo, r.MovementCode, r.Direction, r.TruckPlate, r.At, r.Status)).ToList()));
    }

    // A branch this tenant does not have — another tenant's included, RLS hides it in
    // MDM — is 404 above, as /api/master/company answers. One of the tenant's own
    // branches the caller does not cover is 403.
    private static ProblemHttpResult OutsideYourBranches() =>
        TosScope.OutsideYourBranches("That dashboard is for a depot you do not cover.");

    private static int? Minutes(decimal? average) =>
        average is { } a ? (int)Math.Round(a, MidpointRounding.AwayFromZero) : null;

    private static int Percent(int done, int of) =>
        of <= 0 ? 0 : (int)Math.Round(100m * done / of, MidpointRounding.AwayFromZero);
}
