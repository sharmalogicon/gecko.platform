using Gecko.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Endpoints;

/// <summary>Permission codes as seeded in iam.permission (gecko_identity 14_mdm_permissions.sql).</summary>
internal static class MasterDataPermissions
{
    public const string OrgView = "mdm.org.view";
    public const string OrgManage = "mdm.org.manage";
    public const string PartyView = "mdm.party.view";
    public const string PartyManage = "mdm.party.manage";
    public const string LogisticsView = "mdm.logistics.view";
    public const string LogisticsManage = "mdm.logistics.manage";
    public const string EquipmentView = "mdm.equipment.view";
    public const string EquipmentManage = "mdm.equipment.manage";
    public const string CommercialView = "mdm.commercial.view";
    public const string CommercialManage = "mdm.commercial.manage";
    public const string ConfigView = "mdm.config.view";
    public const string ConfigManage = "mdm.config.manage";
}

internal static class MasterDataSupport
{
    public static Guid TenantId(this ITenantContext caller) =>
        caller.TenantId ?? throw new InvalidOperationException("Master data endpoints require an authenticated tenant.");

    /// <summary>
    /// There are no foreign keys in gecko_master (house convention), so a soft
    /// reference that points nowhere is caught here or not at all.
    /// </summary>
    public static ValidationProblem InvalidReference(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>
    /// Bill-to codes that are not active rows of lookup.bill_to_role. The list was
    /// a CHECK constraint plus two [AllowedValues] copies until
    /// 15_bill_to_and_tariff_code_lists.sql; it is data now, so it is read, never
    /// repeated here.
    /// </summary>
    public static async Task<IReadOnlyList<string>> UnknownBillToAsync(
        this Infrastructure.Persistence.MasterDataDbContext db, IEnumerable<string> codes, CancellationToken ct)
    {
        var wanted = codes.Distinct().ToList();
        var known = await db.BillToRoles.AsNoTracking()
            .Where(r => r.IsActive && wanted.Contains(r.Code))
            .Select(r => r.Code)
            .ToListAsync(ct);
        return wanted.Except(known).ToList();
    }

    /// <summary>
    /// A code taken from a route segment, normalised the way it is stored.
    /// ASP.NET Core decodes every escape in a route value EXCEPT %2F, so a real
    /// depot code such as 'EXP CY/CY' arrives as 'EXP CY%2FCY' and matches
    /// nothing. Only that escape is undone — the rest is already decoded, and
    /// unescaping twice would turn a literal '%41' into 'A'.
    /// </summary>
    public static string FromRouteCode(this string routeValue) =>
        routeValue.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();

    public static ProblemHttpResult Conflict(string title, string? detail = null) =>
        TypedResults.Problem(title: title, detail: detail, statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// NO CHANGE LOG HERE, unlike Identity. gecko_master puts SYSTEM VERSIONING on
    /// 43 tenant tables (11_temporal_history.sql): every UPDATE and soft delete
    /// already writes the previous row to history.&lt;schema&gt;_&lt;table&gt; with its
    /// validity period, by the engine, inside the same transaction. A hand-written
    /// audit row next to it would be a second version of the truth that can drift.
    /// Query the past with EF's TemporalAsOf / TemporalAll instead.
    /// </summary>
    public const string AuditNote = "History is captured by SQL Server system versioning, not an application audit table.";

    /// <summary>
    /// Applies the caller's expected row version so a concurrent edit fails loudly
    /// instead of last-write-wins. gecko_master tenant tables all carry ROWVERSION
    /// for exactly this; ignoring it would make the column decorative.
    /// </summary>
    public static bool TrySetExpectedVersion<TEntity>(this DbContext db, TEntity entity, string? base64RowVersion)
        where TEntity : class
    {
        if (string.IsNullOrWhiteSpace(base64RowVersion)) return false;

        Span<byte> buffer = stackalloc byte[16];
        if (!Convert.TryFromBase64String(base64RowVersion, buffer, out var written) || written == 0) return false;

        db.Entry(entity).Property("RowVersion").OriginalValue = buffer[..written].ToArray();
        return true;
    }

    /// <summary>Saves, turning a lost optimistic-concurrency race into 409 rather than an unhandled 500.</summary>
    public static async Task<ProblemHttpResult?> SaveOrConflictAsync(this DbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(
                "The record changed since you loaded it.",
                "Re-read the resource and re-apply your change. The rowVersion you sent is no longer current.");
        }
    }
}
