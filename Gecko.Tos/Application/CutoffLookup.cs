using System.Data;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Application;

/// <summary>One cut-off as it actually applies, after precedence (PLAN Q2).</summary>
internal sealed record EffectiveCutoff(string Kind, DateTimeOffset At, string Source, int Specificity)
{
    /// <summary>line + branch › line › branch › whole call.</summary>
    public string AppliesTo => Specificity switch
    {
        3 => "this line at this branch",
        2 => "this line",
        1 => "this branch",
        _ => "whole call",
    };
}

/// <summary>
/// The one place that asks <c>vessel.fn_cutoff_effective</c> — the same function the
/// gate reads in Phase 5. Inline, so RLS applies inside it and the plan is a seek.
///
/// One caller, not two: the schedule screen and the late-gate approval must agree
/// on which cut-off applies, or an exception is approved against a time the barrier
/// never judged by.
/// </summary>
internal static class CutoffLookup
{
    public static async Task<IReadOnlyList<EffectiveCutoff>> EffectiveAsync(
        TosDbContext db, Guid vesselCallId, Guid? lineId, Guid? branchId, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<EffectiveCutoff>(
                "SELECT cutoff_kind AS Kind, cutoff_at AS At, source AS Source, specificity AS Specificity " +
                "FROM vessel.fn_cutoff_effective(@call, @line, @branch)",
                Param("@call", vesselCallId), Param("@line", lineId), Param("@branch", branchId))
            .ToListAsync(ct);

    private static SqlParameter Param(string name, Guid? value) =>
        new(name, SqlDbType.UniqueIdentifier) { Value = (object?)value ?? DBNull.Value };
}
