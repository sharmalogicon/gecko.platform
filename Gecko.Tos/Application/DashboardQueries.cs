using System.Data;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Application;

/// <summary>
/// The SQL behind /api/tos/dashboard/*. Every figure on the two dashboards is one
/// of these statements, aggregated in the database — kept as plain SQL so the
/// same text can be pasted into SSMS to spot-check a number (declare @branch,
/// @tz, @from, @to, @now, @take; RLS still needs the session's tenant).
///
/// What counts as a MOVE, everywhere:
///   - a gate transaction at the branch, status COMPLETED (a voided EIR moved nothing);
///   - on a truck visit the gate recorded — NOT source MIGRATED. A Vector migration
///     brought in only the current stay of each in-yard box, stamped
///     gate_in = gate_out: a stock snapshot, not flow, and a 0-minute turnaround.
/// IN / OUT and FULL / EMPTY come from direction and full_empty, never from the
/// movement code: tenants use different vocabularies (FULL_IN vs GIF).
/// Days and hours are the DEPOT's (AT TIME ZONE the branch zone), not UTC.
/// </summary>
internal static class DashboardQueries
{
    private const string Moves = """
        FROM gate.gate_transaction g
        JOIN gate.truck_visit v ON v.truck_visit_id = g.truck_visit_id
        CROSS APPLY (SELECT g.transaction_at AT TIME ZONE @tz AS local_at) l
        WHERE g.branch_id = @branch AND g.status = 'COMPLETED' AND g.deleted_at IS NULL
          AND v.source <> 'MIGRATED'
          AND g.transaction_at >= @from AND g.transaction_at < @to
        """;

    public const string DailyMovesSql = $"""
        SELECT CAST(l.local_at AS date) AS Day, g.direction AS Direction, g.full_empty AS FullEmpty,
               g.equipment_type_code AS EquipmentTypeCode, COUNT(*) AS Moves
        {Moves}
        GROUP BY CAST(l.local_at AS date), g.direction, g.full_empty, g.equipment_type_code
        """;

    public const string MonthlyMovesSql = $"""
        SELECT YEAR(l.local_at) AS Year, MONTH(l.local_at) AS Month, COUNT(*) AS Moves
        {Moves}
        GROUP BY YEAR(l.local_at), MONTH(l.local_at)
        """;

    public const string HourlyMovesSql = $"""
        SELECT DATEPART(hour, l.local_at) AS Hour, COUNT(*) AS Moves
        {Moves}
        GROUP BY DATEPART(hour, l.local_at)
        """;

    public const string MovesByLineSql = $"""
        SELECT TOP (5) g.line_party_code AS LineCode, COUNT(*) AS Moves
        {Moves}
        GROUP BY g.line_party_code
        ORDER BY COUNT(*) DESC, g.line_party_code
        """;

    /// <summary>
    /// Trucks by the depot day they gated in; turnaround = gate in → gate out, over
    /// the ones that have left. The arrived_at bound is only there to seek
    /// ix_truck_visit__day (gate_in_at is never before arrived_at).
    /// </summary>
    public const string DailyTrucksSql = """
        SELECT CAST(v.gate_in_at AT TIME ZONE @tz AS date) AS Day, COUNT(*) AS TrucksIn,
               AVG(CASE WHEN v.gate_out_at IS NOT NULL THEN DATEDIFF(second, v.gate_in_at, v.gate_out_at) / 60.0 END) AS AvgTurnMinutes
        FROM gate.truck_visit v
        WHERE v.branch_id = @branch AND v.deleted_at IS NULL AND v.source <> 'MIGRATED'
          AND v.arrived_at < @to AND v.gate_in_at >= @from AND v.gate_in_at < @to
        GROUP BY CAST(v.gate_in_at AT TIME ZONE @tz AS date)
        """;

    /// <summary>The latest EIRs up to the end of the day asked for — voided ones included, with their status.</summary>
    public const string RecentSql = """
        SELECT TOP (@take) g.gate_transaction_id AS GateTransactionId, g.container_no AS ContainerNo, g.iso_code AS IsoCode,
               g.movement_code AS MovementCode, g.direction AS Direction, g.line_party_code AS LineCode,
               v.truck_plate AS TruckPlate, g.transaction_at AS At, g.status AS Status
        FROM gate.gate_transaction g
        JOIN gate.truck_visit v ON v.truck_visit_id = g.truck_visit_id
        WHERE g.branch_id = @branch AND g.deleted_at IS NULL AND v.source <> 'MIGRATED'
          AND g.transaction_at < @to
        ORDER BY g.transaction_at DESC, g.gate_transaction_id DESC
        """;

    /// <summary>
    /// Calls whose effective YARD_DRY cut-off at this branch (vessel.fn_cutoff_effective,
    /// the function the barrier judges by) falls in the next 48 hours, and that this
    /// branch holds OPEN bookings for. Progress over those bookings: boxes required
    /// (requirement qty), empties released (DONE plan steps whose EIR is OUT/EMPTY) and
    /// full returns received (DONE steps whose EIR is IN/FULL).
    /// </summary>
    public const string ClosingVoyagesSql = """
        SELECT vc.vessel_call_id AS VesselCallId, vc.call_ref AS CallRef, vc.vessel_code AS VesselCode,
               vc.operator_voyage_out AS VoyageOut, vc.operator_voyage_in AS VoyageIn, c.cutoff_at AS CutoffAt,
               req.required AS Required, ISNULL(done.empty_out, 0) AS EmptyOut, ISNULL(done.full_in, 0) AS FullIn
        FROM vessel.vessel_call vc
        CROSS APPLY vessel.fn_cutoff_effective(vc.vessel_call_id, NULL, @branch) c
        CROSS APPLY (
            SELECT SUM(r.qty) AS required
            FROM booking.booking b
            JOIN booking.equipment_requirement r ON r.booking_id = b.booking_id AND r.deleted_at IS NULL
            WHERE b.vessel_call_id = vc.vessel_call_id AND b.branch_id = @branch AND b.status = 'OPEN' AND b.deleted_at IS NULL
        ) req
        OUTER APPLY (
            SELECT SUM(CASE WHEN g.direction = 'OUT' AND g.full_empty = 'EMPTY' THEN 1 ELSE 0 END) AS empty_out,
                   SUM(CASE WHEN g.direction = 'IN' AND g.full_empty = 'FULL' THEN 1 ELSE 0 END) AS full_in
            FROM booking.booking b
            JOIN booking.booking_container bc ON bc.booking_id = b.booking_id AND bc.deleted_at IS NULL
            JOIN booking.movement_plan mp ON mp.booking_container_id = bc.booking_container_id AND mp.status = 'DONE' AND mp.deleted_at IS NULL
            JOIN gate.gate_transaction g ON g.gate_transaction_id = mp.gate_transaction_id AND g.status = 'COMPLETED' AND g.deleted_at IS NULL
            WHERE b.vessel_call_id = vc.vessel_call_id AND b.branch_id = @branch AND b.status = 'OPEN' AND b.deleted_at IS NULL
        ) done
        WHERE vc.deleted_at IS NULL AND vc.is_cancelled = 0
          AND c.cutoff_kind = 'YARD_DRY' AND c.cutoff_at > @now AND c.cutoff_at <= DATEADD(hour, 48, @now)
          AND req.required > 0
        ORDER BY c.cutoff_at, vc.call_ref
        """;

    public sealed record DailyMoves(DateOnly Day, string Direction, string FullEmpty, string? EquipmentTypeCode, int Moves);
    public sealed record MonthlyMoves(int Year, int Month, int Moves);
    public sealed record HourlyMoves(int Hour, int Moves);
    public sealed record LineMoves(string LineCode, int Moves);
    public sealed record DailyTrucks(DateOnly Day, int TrucksIn, decimal? AvgTurnMinutes);
    public sealed record RecentMove(
        Guid GateTransactionId, string ContainerNo, string? IsoCode, string MovementCode, string Direction,
        string LineCode, string TruckPlate, DateTimeOffset At, string Status);
    public sealed record ClosingVoyage(
        Guid VesselCallId, string CallRef, string VesselCode, string? VoyageOut, string? VoyageIn,
        DateTimeOffset CutoffAt, int Required, int EmptyOut, int FullIn);

    /// <summary>A depot-day window: [start of <paramref name="from"/>, start of the day after <paramref name="to"/>).</summary>
    public sealed record Window(BranchClock.Branch Branch, DateOnly From, DateOnly To)
    {
        public SqlParameter[] Parameters() =>
        [
            new("@branch", SqlDbType.UniqueIdentifier) { Value = Branch.BranchId },
            new("@tz", SqlDbType.NVarChar, 64) { Value = BranchClock.SqlZoneName(Branch) },
            new("@from", SqlDbType.DateTimeOffset) { Value = BranchClock.StartOf(Branch, From) },
            new("@to", SqlDbType.DateTimeOffset) { Value = BranchClock.StartOf(Branch, To.AddDays(1)) },
        ];
    }

    public static Task<List<T>> RunAsync<T>(TosDbContext db, string sql, Window window, CancellationToken ct) =>
        db.Database.SqlQueryRaw<T>(sql, window.Parameters()).ToListAsync(ct);

    public static Task<List<RecentMove>> RecentAsync(TosDbContext db, Window window, int take, CancellationToken ct) =>
        db.Database.SqlQueryRaw<RecentMove>(RecentSql,
            [.. window.Parameters(), new SqlParameter("@take", SqlDbType.Int) { Value = take }]).ToListAsync(ct);

    public static Task<List<ClosingVoyage>> ClosingVoyagesAsync(TosDbContext db, Guid branchId, DateTimeOffset now, CancellationToken ct) =>
        db.Database.SqlQueryRaw<ClosingVoyage>(ClosingVoyagesSql,
                new SqlParameter("@branch", SqlDbType.UniqueIdentifier) { Value = branchId },
                new SqlParameter("@now", SqlDbType.DateTimeOffset) { Value = now })
            .ToListAsync(ct);
}
