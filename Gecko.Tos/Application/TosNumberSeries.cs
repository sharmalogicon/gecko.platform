using System.Data;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Application;

/// <summary>
/// TOS's own document numbers (D-9): BK-SCT-LCB01-2609-00017. gecko_app cannot
/// touch config.* at all — a counter only moves through config.usp_next_number,
/// which takes its number INSIDE the caller's transaction, so a booking that
/// fails to save gives its number back.
/// </summary>
internal static class TosNumberSeries
{
    public const string Booking = "BOOKING";

    /// <summary>The EIR and the gate pass are gap-free: the procedure refuses them outside a transaction (Q4).</summary>
    public const string Eir = "EIR";

    public const string TruckVisit = "TRUCK_VISIT";
    public const string GatePass = "GATE_PASS";

    /// <param name="at">Branch-local "now" — the number's YYMM is the depot's month.</param>
    public static async Task<string> NextAsync(TosDbContext db, string seriesKey, Guid branchId, string branchCode,
        DateTimeOffset at, CancellationToken ct)
    {
        var number = new SqlParameter("@number", SqlDbType.NVarChar, 60) { Direction = ParameterDirection.Output };
        await db.Database.ExecuteSqlRawAsync(
            "EXEC config.usp_next_number @series_key = @key, @branch_id = @branch, @branch_code = @code, @at = @at, @number = @number OUTPUT",
            [
                new SqlParameter("@key", SqlDbType.VarChar, 30) { Value = seriesKey },
                new SqlParameter("@branch", SqlDbType.UniqueIdentifier) { Value = branchId },
                new SqlParameter("@code", SqlDbType.VarChar, 30) { Value = branchCode },
                new SqlParameter("@at", SqlDbType.DateTimeOffset) { Value = at },
                number,
            ],
            ct);
        return (string)number.Value;
    }
}
