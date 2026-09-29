using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Gecko.Data;
using Gecko.MasterData.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Endpoints.Commercial;

public sealed record VocabularyItem(string Code, string Name, string? NameLocal);

public sealed record TaxCodeItem(string Code, string Name, string TaxType, decimal RatePct);

public sealed record CommercialVocabularyResponse(
    IReadOnlyList<VocabularyItem> Modules,
    IReadOnlyList<string> ChargeTypes,
    IReadOnlyList<string> ChargeCategories,
    IReadOnlyList<VocabularyItem> BillingUnits,
    IReadOnlyList<VocabularyItem> BillToRoles,
    IReadOnlyList<VocabularyItem> PaymentTerms,
    IReadOnlyList<TaxCodeItem> TaxCodes);

/// <summary>
/// Every value a charge-code (and order-type) editor may offer, in one read, from
/// the same place the API validates against — so a screen never hard-codes a list
/// that the save then refuses. Charge types and categories are read off the
/// request's own [AllowedValues], the one copy there is.
///
/// Under mdm.commercial.view, not Revenue's tariff lookups: whoever maintains the
/// billing vocabulary need not be allowed to see prices.
/// </summary>
internal static class CommercialVocabularyEndpoint
{
    private static readonly string[] ChargeTypes = AllowedOf(nameof(SaveChargeCodeRequest.ChargeType));
    private static readonly string[] ChargeCategories = AllowedOf(nameof(SaveChargeCodeRequest.ChargeCategory));

    public static RouteGroupBuilder MapCommercialVocabulary(this RouteGroupBuilder master)
    {
        master.MapGet("/vocabulary/commercial", GetAsync)
            .RequirePermission(MasterDataPermissions.CommercialView)
            .WithTags("Master data — charge codes")
            .WithSummary("Modules, charge types and categories, billing units, bill-to roles, payment terms and tax codes an editor may offer");
        return master;
    }

    private static async Task<Ok<CommercialVocabularyResponse>> GetAsync(MasterDataDbContext db, CancellationToken ct)
    {
        // A module that does no depot work cannot raise a charge (13_module_vocabulary.sql).
        var modules = await db.Modules.AsNoTracking().Where(m => m.IsActive && m.IsOperational)
            .OrderBy(m => m.SortOrder).ThenBy(m => m.ModuleCode)
            .Select(m => new VocabularyItem(m.ModuleCode, m.DisplayName, null)).ToListAsync(ct);
        var units = await db.BillingUnits.AsNoTracking().Where(u => u.IsActive)
            .OrderBy(u => u.DisplayOrder).ThenBy(u => u.Code)
            .Select(u => new VocabularyItem(u.Code, u.DescriptionEn, u.DescriptionLocal)).ToListAsync(ct);
        var billTo = await db.BillToRoles.AsNoTracking().Where(b => b.IsActive)
            .OrderBy(b => b.SortOrder).ThenBy(b => b.Code)
            .Select(b => new VocabularyItem(b.Code, b.DescriptionEn, b.DescriptionLocal)).ToListAsync(ct);
        var terms = await db.PaymentTerms.AsNoTracking().Where(p => p.IsActive)
            .OrderBy(p => p.DisplayOrder).ThenBy(p => p.Code)
            .Select(p => new VocabularyItem(p.Code, p.DescriptionEn, p.DescriptionLocal)).ToListAsync(ct);
        var taxes = await db.TaxCodes.AsNoTracking().Where(t => t.IsActive)
            .OrderBy(t => t.TaxType).ThenBy(t => t.TaxCode1)
            .Select(t => new TaxCodeItem(t.TaxCode1, t.DescriptionEn, t.TaxType, t.RatePct)).ToListAsync(ct);

        return TypedResults.Ok(new CommercialVocabularyResponse(modules, ChargeTypes, ChargeCategories, units, billTo, terms, taxes));
    }

    private static string[] AllowedOf(string property) =>
        typeof(SaveChargeCodeRequest).GetProperty(property)!.GetCustomAttribute<AllowedValuesAttribute>()!
            .Values.Select(v => (string)v!).ToArray();
}
