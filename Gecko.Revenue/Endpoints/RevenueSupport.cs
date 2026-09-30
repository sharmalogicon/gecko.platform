using Gecko.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Endpoints;

/// <summary>Permission codes as seeded in iam.permission (gecko_identity 15_revenue_permissions.sql).</summary>
internal static class RevenuePermissions
{
    public const string TariffView = "revenue.tariff.view";
    public const string TariffManage = "revenue.tariff.manage";
    public const string TariffApprove = "revenue.tariff.approve";
    public const string ImportManage = "revenue.import.manage";

    /// <summary>gecko_identity 18_cashier_permissions.sql — the drawer, payment, the receipt. Branch-scoped through `bpm`.</summary>
    public const string CashCollect = "revenue.cash.collect";

    /// <summary>gecko_identity 18_cashier_permissions.sql — forgive a quoted line, with a reason.</summary>
    public const string ChargeWaive = "revenue.charge.waive";

    /// <summary>gecko_identity 22_charge_view_permission.sql — read the charge register and the unbilled lines. Branch-scoped through `bpm`.</summary>
    public const string ChargeView = "revenue.charge.view";
}

internal static class RevenueSupport
{
    public static Guid TenantId(this ITenantContext caller) =>
        caller.TenantId ?? throw new InvalidOperationException("Revenue endpoints require an authenticated tenant.");

    public static Guid UserId(this ITenantContext caller) =>
        caller.UserId ?? throw new InvalidOperationException("Revenue endpoints require an authenticated user.");

    public static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    public static ValidationProblem Invalid(IDictionary<string, List<string>> errors) =>
        TypedResults.ValidationProblem(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));

    public static ProblemHttpResult Conflict(string title, string? detail = null) =>
        TypedResults.Problem(title: title, detail: detail, statusCode: StatusCodes.Status409Conflict);

    /// <summary>Same contract as MasterData: a stale rowVersion is a 409, never last-write-wins.</summary>
    public static bool TrySetExpectedVersion<TEntity>(this DbContext db, TEntity entity, string? base64RowVersion)
        where TEntity : class
    {
        if (string.IsNullOrWhiteSpace(base64RowVersion)) return false;

        Span<byte> buffer = stackalloc byte[16];
        if (!Convert.TryFromBase64String(base64RowVersion, buffer, out var written) || written == 0) return false;

        db.Entry(entity).Property("RowVersion").OriginalValue = buffer[..written].ToArray();
        return true;
    }

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
                "The tariff changed since you loaded it.",
                "Re-read it and re-apply your change. The rowVersion you sent is no longer current.");
        }
    }

    /// <summary>
    /// Adds one message under a field key. Validation here collects every problem
    /// in a rate table at once — a 700-row tariff that reports one error per
    /// round trip is unusable.
    /// </summary>
    public static void Add(this IDictionary<string, List<string>> errors, string field, string message)
    {
        if (!errors.TryGetValue(field, out var list)) errors[field] = list = [];
        list.Add(message);
    }
}
