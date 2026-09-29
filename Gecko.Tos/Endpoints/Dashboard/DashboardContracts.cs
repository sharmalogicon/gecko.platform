namespace Gecko.Tos.Endpoints.Dashboard;

// Every figure counts gate-recorded moves only (see DashboardQueries): migrated
// stock and voided EIRs are not moves. An average over nothing is null, not 0.

/// <summary>A daily count on the requested day, the day before, and the 8 days ending on the requested day (oldest first).</summary>
public sealed record DailyTrend(int Today, int PreviousDay, IReadOnlyList<int> Last8Days);

/// <summary>An average (minutes, rounded) — null on a day with nothing to average.</summary>
public sealed record DailyAverageTrend(int? Today, int? PreviousDay, IReadOnlyList<int?> Last8Days);

public sealed record DashboardKpis(
    DailyTrend GateTransactions, DailyAverageTrend TruckTurnaroundMinutes, DailyTrend EirOut, DailyTrend EirIn);

public sealed record MonthlyMovesResponse(string Month, int Moves);

/// <summary>
/// The whole depot day. There is no shift model (no shift or gate-hours table in
/// gecko_tos or gecko_master), so no shift boundaries are invented here.
/// TeuCapacity is the stated capacity of the branch's active yards; null when none is recorded.
/// </summary>
public sealed record DaySummaryResponse(
    int EmptyIn, int EmptyOut, int LadenIn, int LadenOut, decimal TeuMoved, int? TeuCapacity);

public sealed record LineMovesResponse(string LineCode, string? LineName, int Moves);

/// <summary>
/// A call whose YARD_DRY cut-off at this branch is within 48 hours. The percentages
/// are over this branch's OPEN bookings on the call: empties released and full boxes
/// received, each as a share of the boxes required.
/// </summary>
public sealed record ClosingVoyageResponse(
    Guid VesselCallId, string VoyageNo, string? VesselName, DateTimeOffset CutoffAt, decimal HoursToCutoff,
    int FullPct, int EmptyPct);

public sealed record RecentTransactionResponse(
    Guid GateTransactionId, string ContainerNo, string? IsoCode, string MovementCode, string Direction,
    string LineCode, string TruckPlate, DateTimeOffset At, string Status);

public sealed record DashboardOverviewResponse(
    DateOnly Date, Guid BranchId,
    DashboardKpis Kpis,
    IReadOnlyList<MonthlyMovesResponse> MonthlyMoves,
    DaySummaryResponse TodaySummary,
    IReadOnlyList<LineMovesResponse> MovementByLine,
    IReadOnlyList<ClosingVoyageResponse> ClosingVoyages,
    IReadOnlyList<RecentTransactionResponse> RecentTransactions);

public sealed record TodayVsPrevious(int Today, int PreviousDay);

public sealed record AverageTodayVsPrevious(int? Today, int? PreviousDay);

/// <summary>
/// ThroughputPerHour = the day's moves ÷ the hours that had at least one move.
/// PeakHour is "08:00"-style, the earliest busiest hour; null on a day with no moves.
/// </summary>
public sealed record GateTrafficKpis(
    TodayVsPrevious TrucksIn, AverageTodayVsPrevious AvgTurnMinutes, decimal ThroughputPerHour, string? PeakHour);

public sealed record HourlyMovesResponse(int Hour, int Moves);

public sealed record GateActivityResponse(
    Guid GateTransactionId, string ContainerNo, string MovementCode, string Direction,
    string TruckPlate, DateTimeOffset At, string Status);

public sealed record GateTrafficResponse(
    DateOnly Date, Guid BranchId,
    GateTrafficKpis Kpis,
    IReadOnlyList<HourlyMovesResponse> Hourly,
    IReadOnlyList<GateActivityResponse> RecentActivity);
