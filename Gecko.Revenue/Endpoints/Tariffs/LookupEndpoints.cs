using Gecko.Data;
using Gecko.Revenue.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Endpoints.Tariffs;

/// <summary>
/// GET /api/revenue/lookups — the closed vocabularies a tariff row is written in,
/// read from the lookup.* replicas in gecko_revenue. These are exactly the sets
/// <see cref="RateSetValidator"/> checks against, so a drop-down built from this
/// answer can never offer a value the server will refuse.
/// </summary>
internal static class LookupEndpoints
{
    public static RouteGroupBuilder MapLookupEndpoints(this RouteGroupBuilder revenue)
    {
        revenue.MapGet("/lookups", LookupsAsync)
            .RequirePermission(RevenuePermissions.TariffView)
            .WithTags("Revenue — tariffs")
            .WithSummary("Bill-to roles, payment terms, billing units and currencies a tariff row may use");
        return revenue;
    }

    private static async Task<Ok<RevenueLookupsResponse>> LookupsAsync(RevenueDbContext db, CancellationToken ct)
    {
        var billTo = await db.BillToRoles.AsNoTracking().Where(b => b.IsActive)
            .OrderBy(b => b.SortOrder).ThenBy(b => b.Code)
            .Select(b => new BillToRoleLookup(b.Code, b.DescriptionEn, b.DescriptionLocal, b.SortOrder))
            .ToListAsync(ct);
        var terms = await db.PaymentTerms.AsNoTracking().Where(p => p.IsActive)
            .OrderBy(p => p.DisplayOrder).ThenBy(p => p.Code)
            .Select(p => new PaymentTermLookup(p.Code, p.DescriptionEn, p.DescriptionLocal, p.DisplayOrder,
                p.SettlesBeforeRelease, p.RequiresCreditAccount))
            .ToListAsync(ct);
        var units = await db.BillingUnits.AsNoTracking().Where(u => u.IsActive)
            .OrderBy(u => u.DisplayOrder).ThenBy(u => u.Code)
            .Select(u => new BillingUnitLookup(u.Code, u.DescriptionEn, u.DescriptionLocal, u.DisplayOrder,
                u.QuantitySource, u.IsTimeBased))
            .ToListAsync(ct);
        var currencies = await db.Currencies.AsNoTracking().Where(c => c.IsActive)
            .OrderBy(c => c.CurrencyCode)
            .Select(c => new CurrencyLookup(c.CurrencyCode, c.NameEn, c.Symbol, c.MinorUnits))
            .ToListAsync(ct);

        return TypedResults.Ok(new RevenueLookupsResponse(billTo, terms, units, currencies));
    }
}

public sealed record RevenueLookupsResponse(
    IReadOnlyList<BillToRoleLookup> BillToRoles,
    IReadOnlyList<PaymentTermLookup> PaymentTerms,
    IReadOnlyList<BillingUnitLookup> BillingUnits,
    IReadOnlyList<CurrencyLookup> Currencies);

public sealed record BillToRoleLookup(string Code, string Name, string? NameLocal, short SortOrder);

/// <param name="SettlesBeforeRelease">Paid before the box moves (CASH, PREPAID) — the cash window's terms.</param>
public sealed record PaymentTermLookup(string Code, string Name, string? NameLocal, short SortOrder,
    bool SettlesBeforeRelease, bool RequiresCreditAccount);

/// <param name="QuantitySource">What the quantity counts — CONTAINER, TEU, DAY, HOUR…; a DAY/HOUR tier basis needs a unit whose source matches.</param>
/// <param name="IsTimeBased">Prices a duration; the only units a DAY or HOUR tier basis may use.</param>
public sealed record BillingUnitLookup(string Code, string Name, string? NameLocal, short SortOrder,
    string QuantitySource, bool IsTimeBased);

public sealed record CurrencyLookup(string Code, string Name, string? Symbol, byte? MinorUnits);
