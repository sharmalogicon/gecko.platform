using System.Net;
using System.Net.Http.Json;
using Gecko.Tos.Endpoints.Dashboard;
using Gecko.Tos.Endpoints.Vessels;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// The two dashboards. Their numbers are checked against a SECOND count — EF over
/// the same rows, days and hours worked out in .NET rather than by SQL's AT TIME
/// ZONE — so "the UI and a spot-check agree" is tested, not assumed. SCT's gate
/// fixture (and whatever earlier gate tests recorded) is the data; the assertions
/// never hard-code a count that another test could move.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class DashboardApiTests(TosApiFactory api)
{
    private const string Overview = "/api/tos/dashboard/overview";
    private const string GateTraffic = "/api/tos/dashboard/gate-traffic";

    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    /// <summary>The last day of SCT's gate fixture (dev scripts): moves in, out and voided.</summary>
    private static readonly DateOnly FixtureDay = new(2026, 9, 22);

    private static readonly TimeZoneInfo Bangkok = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");

    private static DateOnly LocalDay(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Bangkok).DateTime);

    // ── the second count ────────────────────────────────────────────────────

    private sealed record Move(Guid Id, string Direction, string FullEmpty, string Status, DateTimeOffset At, string TruckSource);

    private static async Task<List<Move>> MovesAtLcbAsync(CancellationToken ct)
    {
        await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
        return await (
            from g in db.GateTransactions.AsNoTracking()
            join v in db.TruckVisits on g.TruckVisitId equals v.TruckVisitId
            where g.BranchId == SctLcb01
            select new Move(g.GateTransactionId, g.Direction, g.FullEmpty, g.Status, g.TransactionAt, v.Source)).ToListAsync(ct);
    }

    /// <summary>What the dashboards count as a move: completed, and recorded at the gate (not migrated stock).</summary>
    private static IEnumerable<Move> Counted(IEnumerable<Move> moves) =>
        moves.Where(m => m.Status == "COMPLETED" && m.TruckSource != "MIGRATED");

    // ── overview ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_overview_agrees_with_a_count_of_the_gate()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);

        var overview = (await client.GetFromJsonAsync<DashboardOverviewResponse>(
            $"{Overview}?branchId={SctLcb01}&date={FixtureDay:yyyy-MM-dd}", ct))!;
        var moves = await MovesAtLcbAsync(ct);
        var counted = Counted(moves).ToList();

        Assert.Equal(FixtureDay, overview.Date);
        Assert.Equal(SctLcb01, overview.BranchId);

        // KPIs: 8 depot days ending on the day asked for, oldest first.
        var days = Enumerable.Range(0, 8).Select(i => FixtureDay.AddDays(i - 7)).ToList();
        int On(DateOnly d, string? direction = null) =>
            counted.Count(m => LocalDay(m.At) == d && (direction is null || m.Direction == direction));

        Assert.Equal(days.Select(d => On(d)), overview.Kpis.GateTransactions.Last8Days);
        Assert.Equal(days.Select(d => On(d, "IN")), overview.Kpis.EirIn.Last8Days);
        Assert.Equal(days.Select(d => On(d, "OUT")), overview.Kpis.EirOut.Last8Days);
        Assert.Equal(On(FixtureDay), overview.Kpis.GateTransactions.Today);
        Assert.Equal(On(FixtureDay.AddDays(-1)), overview.Kpis.GateTransactions.PreviousDay);
        Assert.True(overview.Kpis.GateTransactions.Today > 0, "the fixture day should have gate moves at SCT-LCB01");

        // A voided EIR moved nothing.
        var voided = moves.Count(m => m.Status == "VOIDED" && LocalDay(m.At) == FixtureDay);
        Assert.Equal(moves.Count(m => LocalDay(m.At) == FixtureDay) - voided, overview.Kpis.GateTransactions.Today);

        // The day, split by full / empty — no shift boundaries.
        var today = counted.Where(m => LocalDay(m.At) == FixtureDay).ToList();
        var summary = overview.TodaySummary;
        Assert.Equal(today.Count(m => m is { Direction: "IN", FullEmpty: "EMPTY" }), summary.EmptyIn);
        Assert.Equal(today.Count(m => m is { Direction: "OUT", FullEmpty: "EMPTY" }), summary.EmptyOut);
        Assert.Equal(today.Count(m => m is { Direction: "IN", FullEmpty: "FULL" }), summary.LadenIn);
        Assert.Equal(today.Count(m => m is { Direction: "OUT", FullEmpty: "FULL" }), summary.LadenOut);
        Assert.True(summary.TeuMoved >= today.Count, "every fixture box is at least one TEU");
        Assert.Equal(4800 + 180 + 320, summary.TeuCapacity);   // org.yard at SCT-LCB01: Y-EMPTY, Y-MNR, Y-REEFER

        // 12 depot months ending with the day's month, zero-filled.
        Assert.Equal(12, overview.MonthlyMoves.Count);
        Assert.Equal("2025-10", overview.MonthlyMoves[0].Month);
        Assert.Equal("2026-09", overview.MonthlyMoves[^1].Month);
        Assert.Equal(counted.Count(m => LocalDay(m.At) is { Year: 2026, Month: 9 } d && d <= FixtureDay), overview.MonthlyMoves[^1].Moves);

        // The latest 10 EIRs up to the end of the day — voided ones included, with their status.
        var latest = moves.Where(m => m.TruckSource != "MIGRATED" && LocalDay(m.At) <= FixtureDay)
            .OrderByDescending(m => m.At).ThenByDescending(m => m.Id).Take(10).Select(m => m.At).ToList();
        Assert.Equal(latest, overview.RecentTransactions.Select(r => r.At));

        // Top 5 lines over the 7 days ending on the day, busiest first.
        Assert.True(overview.MovementByLine.Count <= 5);
        Assert.Equal(overview.MovementByLine.OrderByDescending(l => l.Moves).Select(l => l.Moves), overview.MovementByLine.Select(l => l.Moves));
        Assert.True(overview.MovementByLine.Sum(l => l.Moves) <= Enumerable.Range(0, 7).Sum(i => On(FixtureDay.AddDays(-i))));
    }

    [Fact]
    public async Task A_day_with_no_moves_reads_zero_not_invented()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);

        var overview = (await client.GetFromJsonAsync<DashboardOverviewResponse>($"{Overview}?branchId={SctLcb01}&date=2020-01-15", ct))!;
        var traffic = (await client.GetFromJsonAsync<GateTrafficResponse>($"{GateTraffic}?branchId={SctLcb01}&date=2020-01-15", ct))!;

        Assert.All(overview.Kpis.GateTransactions.Last8Days, n => Assert.Equal(0, n));
        Assert.Equal(8, overview.Kpis.TruckTurnaroundMinutes.Last8Days.Count);
        Assert.All(overview.Kpis.TruckTurnaroundMinutes.Last8Days, m => Assert.Null(m));   // nothing to average is not "0 minutes"
        Assert.All(overview.MonthlyMoves, m => Assert.Equal(0, m.Moves));
        Assert.Equal((0, 0, 0, 0, 0m), (overview.TodaySummary.EmptyIn, overview.TodaySummary.EmptyOut,
            overview.TodaySummary.LadenIn, overview.TodaySummary.LadenOut, overview.TodaySummary.TeuMoved));
        Assert.Empty(overview.MovementByLine);
        Assert.Empty(overview.RecentTransactions);

        Assert.Equal(Enumerable.Range(0, 24), traffic.Hourly.Select(h => h.Hour));
        Assert.All(traffic.Hourly, h => Assert.Equal(0, h.Moves));
        Assert.Null(traffic.Kpis.PeakHour);
        Assert.Equal(0m, traffic.Kpis.ThroughputPerHour);
        Assert.Null(traffic.Kpis.AvgTurnMinutes.Today);
        Assert.Equal(0, traffic.Kpis.TrucksIn.Today);
    }

    /// <summary>
    /// A call whose YARD_DRY cut-off is a day away, with an OPEN booking for two boxes
    /// at this branch: on the list, nothing released or received yet.
    /// </summary>
    [Fact]
    public async Task A_voyage_closing_within_48_hours_is_listed_with_its_progress()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var callRef = $"ZZ-{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var carrierRef = $"ZZ-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(24);
        try
        {
            var created = await client.PostAsJsonAsync("/api/tos/vessel-calls", new
            {
                callRef, vesselCode = "CHAOPHRAYA", portCode = "THLCH", terminalCode = "LCB-A0",
                operatorVoyageOut = "D48", eta = now.AddHours(30), etd = now.AddHours(40),
                lines = new object[] { new { lineCode = "MAEU", voyageOut = "D48M" } },
                cutoffs = new object[]
                {
                    new { kind = "PORT_DRY", at = now.AddHours(28) },
                    new { kind = "YARD_DRY", at = cutoff },
                },
            }, ct);
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
            var call = (await created.Content.ReadFromJsonAsync<VesselCallDetailResponse>(ct))!;

            var booking = await client.PostAsJsonAsync("/api/tos/bookings", new
            {
                branchId = SctLcb01, orderTypeCode = "EXP CY/CY", lineCode = "MAEU", customerCode = "CUS-BKF", carrierRef,
                vesselCallId = call.Call.VesselCallId, podPortCode = "SGSIN",
                requirements = new object[] { new { equipmentTypeCode = "40HC", qty = 2 } },
            }, ct);
            Assert.True(booking.StatusCode == HttpStatusCode.Created, await booking.Content.ReadAsStringAsync(ct));

            var overview = (await client.GetFromJsonAsync<DashboardOverviewResponse>($"{Overview}?branchId={SctLcb01}", ct))!;

            var closing = Assert.Single(overview.ClosingVoyages, v => v.VesselCallId == call.Call.VesselCallId);
            Assert.Equal("D48", closing.VoyageNo);
            Assert.False(string.IsNullOrWhiteSpace(closing.VesselName));
            Assert.Equal(cutoff.ToUnixTimeSeconds(), closing.CutoffAt.ToUnixTimeSeconds());
            Assert.InRange(closing.HoursToCutoff, 23.5m, 24.1m);
            Assert.Equal((0, 0), (closing.FullPct, closing.EmptyPct));

            // Another depot holds no booking for it: not its voyage to watch.
            var bkk = (await client.GetFromJsonAsync<DashboardOverviewResponse>($"{Overview}?branchId={TestDatabase.SctBkk01}", ct))!;
            Assert.DoesNotContain(bkk.ClosingVoyages, v => v.VesselCallId == call.Call.VesselCallId);
        }
        finally
        {
            await TestDatabase.RemoveBookingsAsync(carrierRef);
            await TestDatabase.RemoveCallAsync(callRef);
        }
    }

    // ── gate traffic ────────────────────────────────────────────────────────

    [Fact]
    public async Task Gate_traffic_agrees_with_a_count_of_the_gate_by_hour()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);

        var traffic = (await client.GetFromJsonAsync<GateTrafficResponse>(
            $"{GateTraffic}?branchId={SctLcb01}&date={FixtureDay:yyyy-MM-dd}", ct))!;
        var today = Counted(await MovesAtLcbAsync(ct)).Where(m => LocalDay(m.At) == FixtureDay).ToList();

        var expected = Enumerable.Range(0, 24)
            .Select(h => today.Count(m => TimeZoneInfo.ConvertTime(m.At, Bangkok).Hour == h)).ToList();
        Assert.Equal(expected, traffic.Hourly.Select(h => h.Moves));

        var busiest = expected.Max();
        Assert.Equal($"{expected.IndexOf(busiest):00}:00", traffic.Kpis.PeakHour);
        Assert.Equal(Math.Round((decimal)today.Count / expected.Count(n => n > 0), 1), traffic.Kpis.ThroughputPerHour);

        await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
        var visits = await db.TruckVisits.AsNoTracking()
            .Where(v => v.BranchId == SctLcb01 && v.Source != "MIGRATED" && v.GateInAt != null)
            .Select(v => new { GateInAt = v.GateInAt!.Value, v.GateOutAt }).ToListAsync(ct);
        var trucksToday = visits.Where(v => LocalDay(v.GateInAt) == FixtureDay).ToList();
        Assert.Equal(trucksToday.Count, traffic.Kpis.TrucksIn.Today);
        Assert.Equal(visits.Count(v => LocalDay(v.GateInAt) == FixtureDay.AddDays(-1)), traffic.Kpis.TrucksIn.PreviousDay);

        var turns = trucksToday.Where(v => v.GateOutAt is not null).Select(v => (v.GateOutAt!.Value - v.GateInAt).TotalSeconds / 60.0).ToList();
        Assert.Equal(turns.Count == 0 ? null : (int)Math.Round(turns.Average(), MidpointRounding.AwayFromZero), traffic.Kpis.AvgTurnMinutes.Today);

        Assert.True(traffic.RecentActivity.Count <= 15);
        Assert.Equal(traffic.RecentActivity.OrderByDescending(r => r.At).Select(r => r.At), traffic.RecentActivity.Select(r => r.At));
    }

    // ── who may look ────────────────────────────────────────────────────────

    /// <summary>A gate clerk sees their own depot's dashboard, and not a depot they do not cover.</summary>
    [Fact]
    public async Task A_gate_clerk_sees_their_own_depot_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var clerk = await api.ClientForAsync(TosApiFactory.SctGateLcb);

        Assert.Equal(HttpStatusCode.OK, (await clerk.GetAsync($"{Overview}?branchId={SctLcb01}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clerk.GetAsync($"{GateTraffic}?branchId={SctLcb01}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync($"{Overview}?branchId={TestDatabase.SctBkk01}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync($"{GateTraffic}?branchId={TestDatabase.SctBkk01}", ct)).StatusCode);
    }

    /// <summary>Another tenant's branch does not exist for the caller — 404, as /api/master/company answers.</summary>
    [Fact]
    public async Task Another_tenants_depot_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(TosApiFactory.SctOwner);
        var sss = await api.ClientForAsync(TosApiFactory.SssOwner);

        Assert.Equal(HttpStatusCode.NotFound, (await sct.GetAsync($"{Overview}?branchId={TestDatabase.SssLcb01}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sct.GetAsync($"{GateTraffic}?branchId={TestDatabase.SssLcb01}", ct)).StatusCode);

        // SSS's own depot: no gate history at all, so a well-formed, empty dashboard.
        var own = (await sss.GetFromJsonAsync<DashboardOverviewResponse>($"{Overview}?branchId={TestDatabase.SssLcb01}&date={FixtureDay:yyyy-MM-dd}", ct))!;
        Assert.Equal(0, own.Kpis.GateTransactions.Today);
        Assert.Empty(own.RecentTransactions);
    }

    [Fact]
    public async Task A_dashboard_needs_a_depot_and_a_signed_in_caller()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(TosApiFactory.SctOwner);
        using var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await sct.GetAsync(Overview, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sct.GetAsync($"{GateTraffic}?branchId={SctLcb01}&date=22-09-2026", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Overview}?branchId={SctLcb01}", ct)).StatusCode);
    }
}
