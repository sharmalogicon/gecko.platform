using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Gecko.Data;
using Gecko.MasterData.Infrastructure.Persistence;
using Gecko.MasterData.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Endpoints.Commercial;

// ── charge codes ────────────────────────────────────────────────────────────

public sealed record ChargeCodeResponse(
    Guid ChargeCodeId, string ChargeCode, string DescriptionEn, string? DescriptionLocal,
    string ModuleCode, string ChargeType, string ChargeCategory, string BillingUnitCode,
    bool IsByService, bool IsActive, string RowVersion);

public sealed record ChargeCodeDetailResponse(ChargeCodeResponse Charge, IReadOnlyList<ChargeVariantResponse> Variants);

public sealed record ChargeVariantResponse(
    Guid ChargeCodeVariantId, string BillTo, string PaymentTermCode,
    Guid? TaxCodeId, string? TaxCode, Guid? WithholdingTaxCodeId, string? WithholdingTaxCode,
    short? CreditTermDays, string? RevenueGl, string? CostGl, string? LegacyChargeCode, bool IsActive);

public sealed record SaveChargeCodeRequest(
    [property: Required, RegularExpression("^[A-Z0-9][A-Z0-9._-]{0,14}$", ErrorMessage = "Upper-case letters, digits, '.', '_' and '-', up to 15 chars — e.g. SC006 or LIFTIN.")] string ChargeCode,
    [property: Required, MaxLength(200)] string DescriptionEn,
    [property: Required, MaxLength(20)] string ModuleCode,
    [property: Required, AllowedValues("GATE", "LIFT", "STORAGE", "HANDLING", "TRANSPORT", "REPAIR", "SURVEY", "REEFER", "DOCUMENT", "VAS", "PENALTY", "OTHER")] string ChargeType,
    [property: Required, MaxLength(20)] string BillingUnitCode,
    [property: AllowedValues("GENERAL", "LADEN", "EMPTY", "REEFER", "DG", "OOG")] string ChargeCategory = "GENERAL",
    [property: MaxLength(200)] string? DescriptionLocal = null,
    bool IsByService = false,
    bool IsActive = true,
    string? RowVersion = null);

public sealed record ChargeVariantItem(
    [property: Required, MaxLength(20)] string BillTo,   // soft ref -> lookup.bill_to_role
    [property: Required, MaxLength(20)] string PaymentTermCode,
    [property: MaxLength(20)] string? TaxCode = null,
    [property: MaxLength(20)] string? WithholdingTaxCode = null,
    [property: Range(0, 365)] short? CreditTermDays = null,
    [property: MaxLength(20)] string? RevenueGl = null,
    [property: MaxLength(20)] string? CostGl = null,
    [property: MaxLength(20)] string? LegacyChargeCode = null);

/// <summary>The whole matrix, plus the charge code's rowVersion: replacing the variants is an edit of the charge code.</summary>
public sealed record ReplaceChargeVariantsRequest(
    [property: Required, MinLength(1)] IReadOnlyList<ChargeVariantItem> Variants,
    string? RowVersion = null);

// ── tax codes ───────────────────────────────────────────────────────────────

public sealed record TaxCodeResponse(
    Guid TaxCodeId, string TaxCode, string DescriptionEn, string? DescriptionLocal, string CountryCode,
    string TaxType, decimal RatePct, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
    bool IsDefaultForType, string? OutputTaxGl, string? InputTaxGl, bool IsActive, string RowVersion);

public sealed record SaveTaxCodeRequest(
    [property: Required, RegularExpression("^[A-Z0-9][A-Z0-9_-]{0,19}$")] string TaxCode,
    [property: Required, MaxLength(200)] string DescriptionEn,
    [property: Required, StringLength(2, MinimumLength = 2)] string CountryCode,
    [property: Required, AllowedValues("VAT", "GST", "SST", "SALES_TAX", "WITHHOLDING", "ZERO_RATED", "EXEMPT")] string TaxType,
    [property: Required, Range(0, 100)] decimal RatePct,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo = null,
    bool IsDefaultForType = false,
    [property: MaxLength(200)] string? DescriptionLocal = null,
    [property: MaxLength(20)] string? OutputTaxGl = null,
    [property: MaxLength(20)] string? InputTaxGl = null,
    bool IsActive = true,
    string? RowVersion = null);

// ── movements ───────────────────────────────────────────────────────────────

public sealed record MovementResponse(
    Guid MovementId, string MovementCode, string DescriptionEn, string? DescriptionLocal,
    string FullEmpty, string Direction, string AppliesToModule, string? CodecoStatusCode,
    bool ChangesYardPosition, bool ChangesStatus, bool RequiresSurvey, bool IsActive, string RowVersion);

public sealed record SaveMovementRequest(
    // Real depot movement codes are phrases, not identifiers — Vector's are
    // 'FULL IN', 'MTY OUT', 'FULL IN (TRK)'. A tight identifier regex here would
    // reject every code the customer already uses.
    [property: Required, RegularExpression("^[A-Z0-9][A-Z0-9 ()/._-]{0,19}$")] string MovementCode,
    [property: Required, MaxLength(200)] string DescriptionEn,
    [property: Required, AllowedValues("FULL", "EMPTY", "ANY")] string FullEmpty,
    [property: Required, AllowedValues("IN", "OUT", "INTERNAL", "TRANSFER")] string Direction,
    [property: Required, AllowedValues("TOS", "CFS", "TRUCKING", "BOTH")] string AppliesToModule,
    [property: MaxLength(200)] string? DescriptionLocal = null,
    [property: MaxLength(3)] string? CodecoStatusCode = null,
    bool ChangesYardPosition = true,
    bool ChangesStatus = true,
    bool RequiresSurvey = false,
    bool IsActive = true,
    string? RowVersion = null);

// ── service types ───────────────────────────────────────────────────────────

public sealed record ServiceTypeResponse(
    Guid ServiceTypeId, string ServiceCode, string DescriptionEn, string? DescriptionLocal,
    string OriginForm, string DestinationForm, short DisplayOrder, bool IsActive, string RowVersion);

public sealed record SaveServiceTypeRequest(
    [property: Required, RegularExpression("^[A-Z0-9][A-Z0-9 /._-]{0,14}$")] string ServiceCode,
    [property: Required, MaxLength(200)] string DescriptionEn,
    [property: Required, AllowedValues("CY", "CFS", "DOOR", "RAMP", "VESSEL", "BREAKBULK")] string OriginForm,
    [property: Required, AllowedValues("CY", "CFS", "DOOR", "RAMP", "VESSEL", "BREAKBULK")] string DestinationForm,
    [property: Range(0, 9999)] short DisplayOrder = 100,
    [property: MaxLength(200)] string? DescriptionLocal = null,
    bool IsActive = true,
    string? RowVersion = null);

/// <summary>
/// The billing VOCABULARY — what is chargeable, in what unit, to whom and on
/// what terms. Not what it costs.
///
/// THE MDM / REVENUE SEAM, stated once because it is easy to erode:
/// a charge code says a lift-on is billable per container, that it can be billed
/// to the LINE on credit, that it attracts VAT7 and Thai withholding tax, and
/// which GL account it lands in. It does NOT say ฿850. The number lives in a
/// tariff in gecko_revenue, because a rate changes per customer, per contract and
/// per year while the fact that lifting is chargeable does not.
///
/// WHAT VECTOR TEACHES HERE: its 202 active charge codes are only 111 real
/// charges. `SA001-CA` and `SA001-CR` are one ADMISSION FEE billed cash or on
/// credit, with the payment term encoded in the CODE STRING. That is exactly what
/// charge_code_variant normalises, and the ETL must SPLIT the suffix rather than
/// import 202 codes and inherit the problem.
/// </summary>
internal static class CommercialEndpoints
{
    public static RouteGroupBuilder MapCommercialEndpoints(this RouteGroupBuilder master)
    {
        var charges = master.MapGroup("/charge-codes").WithTags("Master data — charge codes");
        charges.MapGet("/", ListChargesAsync).RequirePermission(MasterDataPermissions.CommercialView).WithSummary("List charge codes, optionally by module");
        charges.MapGet("/{chargeCode}", GetChargeAsync).RequirePermission(MasterDataPermissions.CommercialView).WithName("GetChargeCode").WithSummary("Get one charge code with its billing variants");
        charges.MapPost("/", CreateChargeAsync).RequirePermission(MasterDataPermissions.CommercialManage).Validate<SaveChargeCodeRequest>().WithSummary("Create a charge code");
        charges.MapPut("/{chargeCode}", UpdateChargeAsync).RequirePermission(MasterDataPermissions.CommercialManage).Validate<SaveChargeCodeRequest>().WithSummary("Update a charge code");
        charges.MapPut("/{chargeCode}/variants", ReplaceVariantsAsync).RequirePermission(MasterDataPermissions.CommercialManage).Validate<ReplaceChargeVariantsRequest>().WithSummary("Replace the billing variants (bill-to x payment term)");
        charges.MapDelete("/{chargeCode}", DeleteChargeAsync).RequirePermission(MasterDataPermissions.CommercialManage).WithSummary("Soft-delete a charge code");

        var taxes = master.MapGroup("/tax-codes").WithTags("Master data — tax codes");
        taxes.MapGet("/", ListTaxesAsync).RequirePermission(MasterDataPermissions.CommercialView).WithSummary("List tax codes");
        taxes.MapPost("/", CreateTaxAsync).RequirePermission(MasterDataPermissions.CommercialManage).Validate<SaveTaxCodeRequest>().WithSummary("Create a tax code");
        taxes.MapPut("/{taxCode}", UpdateTaxAsync).RequirePermission(MasterDataPermissions.CommercialManage).Validate<SaveTaxCodeRequest>().WithSummary("Update a tax code");
        taxes.MapDelete("/{taxCode}", DeleteTaxAsync).RequirePermission(MasterDataPermissions.CommercialManage).WithSummary("Soft-delete a tax code");

        var movements = master.MapGroup("/movements").WithTags("Master data — movements");
        movements.MapGet("/", ListMovementsAsync).RequirePermission(MasterDataPermissions.CommercialView).WithSummary("List container movements");
        movements.MapPost("/", CreateMovementAsync).RequirePermission(MasterDataPermissions.CommercialManage).Validate<SaveMovementRequest>().WithSummary("Create a movement");
        movements.MapPut("/{movementCode}", UpdateMovementAsync).RequirePermission(MasterDataPermissions.CommercialManage).Validate<SaveMovementRequest>().WithSummary("Update a movement");
        movements.MapDelete("/{movementCode}", DeleteMovementAsync).RequirePermission(MasterDataPermissions.CommercialManage).WithSummary("Soft-delete a movement");

        var services = master.MapGroup("/service-types").WithTags("Master data — service types");
        services.MapGet("/", ListServicesAsync).RequirePermission(MasterDataPermissions.CommercialView).WithSummary("List service types (CY/CY, CY/DOOR ...)");
        services.MapPost("/", CreateServiceAsync).RequirePermission(MasterDataPermissions.CommercialManage).Validate<SaveServiceTypeRequest>().WithSummary("Create a service type");
        services.MapPut("/{serviceCode}", UpdateServiceAsync).RequirePermission(MasterDataPermissions.CommercialManage).Validate<SaveServiceTypeRequest>().WithSummary("Update a service type");
        services.MapDelete("/{serviceCode}", DeleteServiceAsync).RequirePermission(MasterDataPermissions.CommercialManage).WithSummary("Soft-delete a service type");

        return master;
    }

    // ── charge codes ────────────────────────────────────────────────────────

    private static async Task<Ok<PagedResult<ChargeCodeResponse>>> ListChargesAsync(
        [AsParameters] ListQuery query, MasterDataDbContext db, CancellationToken ct,
        string? moduleCode = null, string? chargeType = null, bool includeInactive = false)
    {
        var charges = db.ChargeCodes.AsNoTracking();
        if (!includeInactive) charges = charges.Where(c => c.IsActive);
        if (!string.IsNullOrWhiteSpace(moduleCode)) charges = charges.Where(c => c.ModuleCode == moduleCode.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(chargeType)) charges = charges.Where(c => c.ChargeType == chargeType.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(query.Search))
            charges = charges.Where(c => c.ChargeCode1.Contains(query.Search) || c.DescriptionEn.Contains(query.Search));

        return TypedResults.Ok(await charges
            .OrderBy(c => c.ModuleCode).ThenBy(c => c.ChargeCode1)
            .Select(ChargeProjection)
            .ToPagedAsync(query.Page, query.PageSize, ct));
    }

    private static async Task<Results<Ok<ChargeCodeDetailResponse>, NotFound>> GetChargeAsync(
        string chargeCode, MasterDataDbContext db, CancellationToken ct)
    {
        var code = chargeCode.ToUpperInvariant();
        var charge = await db.ChargeCodes.AsNoTracking().Where(c => c.ChargeCode1 == code).Select(ChargeProjection).SingleOrDefaultAsync(ct);
        if (charge is null) return TypedResults.NotFound();

        return TypedResults.Ok(new ChargeCodeDetailResponse(charge, await VariantsOfAsync(db, charge.ChargeCodeId, ct)));
    }

    private static async Task<Results<CreatedAtRoute<ChargeCodeDetailResponse>, ValidationProblem, ProblemHttpResult>> CreateChargeAsync(
        SaveChargeCodeRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var code = request.ChargeCode.ToUpperInvariant();
        if (await ValidateChargeAsync(db, request, ct) is { } problem) return problem;
        if (await db.ChargeCodes.AnyAsync(c => c.ChargeCode1 == code, ct))
            return MasterDataSupport.Conflict($"Charge code '{code}' already exists.");

        var charge = new ChargeCode { TenantId = caller.TenantId(), ChargeCode1 = code };
        Apply(charge, request);
        db.ChargeCodes.Add(charge);
        await db.SaveChangesAsync(ct);

        return TypedResults.CreatedAtRoute(
            new ChargeCodeDetailResponse(MapCharge(charge), []), "GetChargeCode", new { chargeCode = code });
    }

    private static async Task<Results<Ok<ChargeCodeResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateChargeAsync(
        string chargeCode, SaveChargeCodeRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var charge = await db.ChargeCodes.SingleOrDefaultAsync(c => c.ChargeCode1 == chargeCode.ToUpperInvariant(), ct);
        if (charge is null) return TypedResults.NotFound();
        if (!db.TrySetExpectedVersion(charge, request.RowVersion))
            return MasterDataSupport.InvalidReference("rowVersion", "Send the rowVersion you received when reading the record.");
        if (await ValidateChargeAsync(db, request, ct) is { } problem) return problem;

        Apply(charge, request);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(MapCharge(charge));
    }

    /// <summary>
    /// Replace, not patch: uq_charge_code_variant__matrix is
    /// (tenant, charge_code, bill_to, payment_term), so a partial edit discovers a
    /// clash halfway through. Sending the whole intended matrix lets it be checked
    /// once, up front.
    /// </summary>
    private static async Task<Results<Ok<ChargeCodeDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> ReplaceVariantsAsync(
        string chargeCode, ReplaceChargeVariantsRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var charge = await db.ChargeCodes.SingleOrDefaultAsync(c => c.ChargeCode1 == chargeCode.ToUpperInvariant(), ct);
        if (charge is null) return TypedResults.NotFound();

        var wanted = request.Variants
            .Select(v => v with { BillTo = v.BillTo.ToUpperInvariant(), PaymentTermCode = v.PaymentTermCode.ToUpperInvariant() })
            .ToList();

        // Every problem, each on its own row and column (variants[1].taxCode), so the
        // editor can mark the bad cells in one pass instead of one per save.
        var errors = new RowErrors("variants");

        var unknownBillTo = (await db.UnknownBillToAsync(wanted.Select(v => v.BillTo), ct)).ToHashSet();
        var terms = wanted.Select(v => v.PaymentTermCode).Distinct().ToList();
        var knownTerms = (await db.PaymentTerms.AsNoTracking().Where(p => terms.Contains(p.Code) && p.IsActive)
            .Select(p => p.Code).ToListAsync(ct)).ToHashSet();

        // Tax codes are resolved by CODE, not id: the caller is editing a matrix of
        // strings, and making them look up GUIDs for VAT7 first would be hostile.
        var taxCodes = await db.TaxCodes.AsNoTracking()
            .Select(t => new { t.TaxCodeId, t.TaxCode1, t.TaxType }).ToListAsync(ct);

        var seen = new HashSet<(string, string)>();
        for (var i = 0; i < wanted.Count; i++)
        {
            var v = wanted[i];
            // uq_charge_code_variant__matrix: one row per (bill-to, payment term).
            if (!seen.Add((v.BillTo, v.PaymentTermCode)))
                errors.Add(i, null, $"Repeated bill-to / payment-term pair: {v.BillTo}/{v.PaymentTermCode}.");
            if (unknownBillTo.Contains(v.BillTo))
                errors.Add(i, "billTo", $"Unknown bill-to role(s): {v.BillTo}.");
            if (!knownTerms.Contains(v.PaymentTermCode))
                errors.Add(i, "paymentTermCode", $"Unknown payment term '{v.PaymentTermCode}'.");
            if (v.TaxCode is not null && taxCodes.All(t => t.TaxCode1 != v.TaxCode.ToUpperInvariant()))
                errors.Add(i, "taxCode", $"Unknown tax code '{v.TaxCode}'.");
            if (v.WithholdingTaxCode is not null)
            {
                var wht = taxCodes.SingleOrDefault(t => t.TaxCode1 == v.WithholdingTaxCode.ToUpperInvariant());
                if (wht is null)
                    errors.Add(i, "withholdingTaxCode", $"Unknown tax code '{v.WithholdingTaxCode}'.");
                // A withholding slot holding a VAT code silently deducts the wrong
                // amount from a supplier payment, which nobody notices until audit.
                else if (wht.TaxType != "WITHHOLDING")
                    errors.Add(i, "withholdingTaxCode", $"'{v.WithholdingTaxCode}' is a {wht.TaxType} code, not WITHHOLDING.");
            }
        }
        if (errors.Count > 0) return errors.Problem();

        if (db.TouchParent(charge, request.RowVersion) is { } missing) return missing;

        // One transaction: the soft deletes are flushed first (past the filtered unique
        // index) together with the version-guarded touch of the charge code, so a stale
        // editor is refused before anything changes, and a failed insert loses nothing.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.ChargeCodeVariants.Where(v => v.ChargeCodeId == charge.ChargeCodeId).ToListAsync(ct);
        foreach (var gone in existing) db.ChargeCodeVariants.Remove(gone);
        if (await db.SaveOrConflictAsync(ct) is { } stale) return stale;

        foreach (var v in wanted)
        {
            db.ChargeCodeVariants.Add(new ChargeCodeVariant
            {
                TenantId = caller.TenantId(),
                ChargeCodeId = charge.ChargeCodeId,
                BillTo = v.BillTo,
                PaymentTermCode = v.PaymentTermCode,
                TaxCodeId = v.TaxCode is null ? null : taxCodes.Single(t => t.TaxCode1 == v.TaxCode.ToUpperInvariant()).TaxCodeId,
                WithholdingTaxCodeId = v.WithholdingTaxCode is null ? null : taxCodes.Single(t => t.TaxCode1 == v.WithholdingTaxCode.ToUpperInvariant()).TaxCodeId,
                CreditTermDays = v.CreditTermDays,
                RevenueGl = v.RevenueGl,
                CostGl = v.CostGl,
                LegacyChargeCode = v.LegacyChargeCode,
                IsActive = true,
            });
        }

        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await tx.CommitAsync(ct);
        return TypedResults.Ok(new ChargeCodeDetailResponse(MapCharge(charge), await VariantsOfAsync(db, charge.ChargeCodeId, ct)));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult, ValidationProblem>> DeleteChargeAsync(
        string chargeCode, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var charge = await db.ChargeCodes.SingleOrDefaultAsync(c => c.ChargeCode1 == chargeCode.ToUpperInvariant(), ct);
        if (charge is null) return TypedResults.NotFound();
        if (db.ExpectVersion(charge, rowVersion) is { } missing) return missing;

        // An order type or movement still raising this charge would raise a charge
        // that no longer exists. No FKs, so this is the only place it is caught.
        if (await db.OrderTypeCharges.AnyAsync(c => c.ChargeCodeId == charge.ChargeCodeId, ct))
            return MasterDataSupport.Conflict("Charge code is still used by an order type.", "Remove it from the order types first, or set isActive = false.");
        if (await db.MovementCharges.AnyAsync(c => c.ChargeCodeId == charge.ChargeCodeId, ct))
            return MasterDataSupport.Conflict("Charge code is still used by a movement.", "Remove it from the movements first, or set isActive = false.");

        foreach (var variant in await db.ChargeCodeVariants.Where(v => v.ChargeCodeId == charge.ChargeCodeId).ToListAsync(ct))
            db.ChargeCodeVariants.Remove(variant);
        db.ChargeCodes.Remove(charge);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static void Apply(ChargeCode charge, SaveChargeCodeRequest request)
    {
        charge.DescriptionEn = request.DescriptionEn;
        charge.DescriptionLocal = request.DescriptionLocal;
        charge.ModuleCode = request.ModuleCode.ToUpperInvariant();
        charge.ChargeType = request.ChargeType;
        charge.ChargeCategory = request.ChargeCategory;
        charge.BillingUnitCode = request.BillingUnitCode.ToUpperInvariant();
        charge.IsByService = request.IsByService;
        charge.IsActive = request.IsActive;
    }

    private static async Task<ValidationProblem?> ValidateChargeAsync(
        MasterDataDbContext db, SaveChargeCodeRequest request, CancellationToken ct)
    {
        var module = request.ModuleCode.ToUpperInvariant();

        // module_code is a soft ref to lookup.module since 13_module_vocabulary.sql
        // retired the CHECK. is_operational matters: a charge on a module that does
        // no depot work can never be raised by anything.
        var moduleRow = await db.Modules.AsNoTracking()
            .Where(m => m.ModuleCode == module && m.IsActive)
            .Select(m => new { m.IsOperational })
            .SingleOrDefaultAsync(ct);

        if (moduleRow is null)
            return MasterDataSupport.InvalidReference("moduleCode", $"Unknown module '{module}'.");
        if (!moduleRow.IsOperational)
            return MasterDataSupport.InvalidReference("moduleCode", $"'{module}' does no depot work, so it cannot raise charges.");

        var unit = request.BillingUnitCode.ToUpperInvariant();
        if (!await db.BillingUnits.AnyAsync(b => b.Code == unit && b.IsActive, ct))
            return MasterDataSupport.InvalidReference("billingUnitCode", $"Unknown billing unit '{unit}'.");

        return null;
    }

    private static async Task<IReadOnlyList<ChargeVariantResponse>> VariantsOfAsync(
        MasterDataDbContext db, Guid chargeCodeId, CancellationToken ct) =>
        await (
            from v in db.ChargeCodeVariants.AsNoTracking().Where(v => v.ChargeCodeId == chargeCodeId)
            join t in db.TaxCodes on v.TaxCodeId equals t.TaxCodeId into taxes
            from t in taxes.DefaultIfEmpty()
            join w in db.TaxCodes on v.WithholdingTaxCodeId equals w.TaxCodeId into whts
            from w in whts.DefaultIfEmpty()
            orderby v.BillTo, v.PaymentTermCode
            select new ChargeVariantResponse(
                v.ChargeCodeVariantId, v.BillTo, v.PaymentTermCode,
                v.TaxCodeId, t == null ? null : t.TaxCode1,
                v.WithholdingTaxCodeId, w == null ? null : w.TaxCode1,
                v.CreditTermDays, v.RevenueGl, v.CostGl, v.LegacyChargeCode, v.IsActive)
        ).ToListAsync(ct);

    // ── tax codes ───────────────────────────────────────────────────────────

    private static async Task<Ok<IReadOnlyList<TaxCodeResponse>>> ListTaxesAsync(
        MasterDataDbContext db, CancellationToken ct, bool includeInactive = false)
    {
        var taxes = db.TaxCodes.AsNoTracking();
        if (!includeInactive) taxes = taxes.Where(t => t.IsActive);
        return TypedResults.Ok<IReadOnlyList<TaxCodeResponse>>(
            await taxes.OrderBy(t => t.TaxType).ThenBy(t => t.TaxCode1).Select(TaxProjection).ToListAsync(ct));
    }

    private static async Task<Results<Ok<TaxCodeResponse>, ValidationProblem, ProblemHttpResult>> CreateTaxAsync(
        SaveTaxCodeRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var code = request.TaxCode.ToUpperInvariant();
        if (await ValidateTaxAsync(db, request, null, ct) is { } problem) return problem;
        if (await db.TaxCodes.AnyAsync(t => t.TaxCode1 == code, ct))
            return MasterDataSupport.Conflict($"Tax code '{code}' already exists.");

        var tax = new TaxCode { TenantId = caller.TenantId(), TaxCode1 = code };
        Apply(tax, request);
        db.TaxCodes.Add(tax);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(MapTax(tax));
    }

    private static async Task<Results<Ok<TaxCodeResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateTaxAsync(
        string taxCode, SaveTaxCodeRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var tax = await db.TaxCodes.SingleOrDefaultAsync(t => t.TaxCode1 == taxCode.ToUpperInvariant(), ct);
        if (tax is null) return TypedResults.NotFound();
        if (!db.TrySetExpectedVersion(tax, request.RowVersion))
            return MasterDataSupport.InvalidReference("rowVersion", "Send the rowVersion you received when reading the record.");
        if (await ValidateTaxAsync(db, request, tax.TaxCodeId, ct) is { } problem) return problem;

        Apply(tax, request);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(MapTax(tax));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult, ValidationProblem>> DeleteTaxAsync(
        string taxCode, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var tax = await db.TaxCodes.SingleOrDefaultAsync(t => t.TaxCode1 == taxCode.ToUpperInvariant(), ct);
        if (tax is null) return TypedResults.NotFound();
        if (db.ExpectVersion(tax, rowVersion) is { } missing) return missing;

        if (await db.ChargeCodeVariants.AnyAsync(v => v.TaxCodeId == tax.TaxCodeId || v.WithholdingTaxCodeId == tax.TaxCodeId, ct))
            return MasterDataSupport.Conflict("Tax code is still referenced by a charge code variant.");

        db.TaxCodes.Remove(tax);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static void Apply(TaxCode tax, SaveTaxCodeRequest request)
    {
        tax.DescriptionEn = request.DescriptionEn;
        tax.DescriptionLocal = request.DescriptionLocal;
        tax.CountryCode = request.CountryCode.ToUpperInvariant();
        tax.TaxType = request.TaxType;
        tax.RatePct = request.RatePct;
        tax.EffectiveFrom = request.EffectiveFrom;
        tax.EffectiveTo = request.EffectiveTo;
        tax.IsDefaultForType = request.IsDefaultForType;
        tax.OutputTaxGl = request.OutputTaxGl;
        tax.InputTaxGl = request.InputTaxGl;
        tax.IsActive = request.IsActive;
    }

    private static async Task<ValidationProblem?> ValidateTaxAsync(
        MasterDataDbContext db, SaveTaxCodeRequest request, Guid? existingId, CancellationToken ct)
    {
        var country = request.CountryCode.ToUpperInvariant();
        if (!await db.Countries.AnyAsync(c => c.CountryCode == country, ct))
            return MasterDataSupport.InvalidReference("countryCode", $"Unknown country '{country}'.");

        // Mirrors ck_tax_code__zero: an EXEMPT or ZERO_RATED code with a rate is a
        // contradiction that would quietly add tax to an exempt invoice line.
        if (request.TaxType is "EXEMPT" or "ZERO_RATED" && request.RatePct != 0)
            return MasterDataSupport.InvalidReference("ratePct", $"A {request.TaxType} code must have a rate of 0.");

        if (request.EffectiveTo is not null && request.EffectiveTo < request.EffectiveFrom)
            return MasterDataSupport.InvalidReference("effectiveTo", "Effective-to cannot be before effective-from.");

        // uq_tax_code__default is (tenant, country, tax_type) WHERE is_default = 1.
        if (request.IsDefaultForType)
        {
            var clash = await db.TaxCodes.AnyAsync(t =>
                t.CountryCode == country && t.TaxType == request.TaxType && t.IsDefaultForType
                && (existingId == null || t.TaxCodeId != existingId), ct);
            if (clash)
                return MasterDataSupport.InvalidReference("isDefaultForType",
                    $"Another {request.TaxType} code is already the default for {country}.");
        }

        return null;
    }

    // ── movements ───────────────────────────────────────────────────────────

    private static async Task<Ok<IReadOnlyList<MovementResponse>>> ListMovementsAsync(
        MasterDataDbContext db, CancellationToken ct, string? appliesToModule = null, bool includeInactive = false)
    {
        var movements = db.Movements.AsNoTracking();
        if (!includeInactive) movements = movements.Where(m => m.IsActive);
        if (!string.IsNullOrWhiteSpace(appliesToModule))
        {
            var module = appliesToModule.ToUpperInvariant();
            movements = movements.Where(m => m.AppliesToModule == module || m.AppliesToModule == "BOTH");
        }
        return TypedResults.Ok<IReadOnlyList<MovementResponse>>(
            await movements.OrderBy(m => m.MovementCode).Select(MovementProjection).ToListAsync(ct));
    }

    private static async Task<Results<Ok<MovementResponse>, ValidationProblem, ProblemHttpResult>> CreateMovementAsync(
        SaveMovementRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var code = request.MovementCode.ToUpperInvariant();
        if (await db.Movements.AnyAsync(m => m.MovementCode == code, ct))
            return MasterDataSupport.Conflict($"Movement '{code}' already exists.");

        var movement = new Movement { TenantId = caller.TenantId(), MovementCode = code };
        Apply(movement, request);
        db.Movements.Add(movement);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(MapMovement(movement));
    }

    private static async Task<Results<Ok<MovementResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateMovementAsync(
        string movementCode, SaveMovementRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var movement = await db.Movements.SingleOrDefaultAsync(m => m.MovementCode == movementCode.FromRouteCode(), ct);
        if (movement is null) return TypedResults.NotFound();
        if (!db.TrySetExpectedVersion(movement, request.RowVersion))
            return MasterDataSupport.InvalidReference("rowVersion", "Send the rowVersion you received when reading the record.");

        Apply(movement, request);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(MapMovement(movement));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult, ValidationProblem>> DeleteMovementAsync(
        string movementCode, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var movement = await db.Movements.SingleOrDefaultAsync(m => m.MovementCode == movementCode.FromRouteCode(), ct);
        if (movement is null) return TypedResults.NotFound();
        if (db.ExpectVersion(movement, rowVersion) is { } missing) return missing;

        if (await db.OrderTypeMovements.AnyAsync(m => m.MovementId == movement.MovementId, ct))
            return MasterDataSupport.Conflict("Movement is still part of an order type.", "Remove it from the order types first, or set isActive = false.");

        foreach (var charge in await db.MovementCharges.Where(c => c.MovementId == movement.MovementId).ToListAsync(ct))
            db.MovementCharges.Remove(charge);
        db.Movements.Remove(movement);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static void Apply(Movement movement, SaveMovementRequest request)
    {
        movement.DescriptionEn = request.DescriptionEn;
        movement.DescriptionLocal = request.DescriptionLocal;
        movement.FullEmpty = request.FullEmpty;
        movement.Direction = request.Direction;
        movement.AppliesToModule = request.AppliesToModule;
        movement.CodecoStatusCode = request.CodecoStatusCode;
        movement.ChangesYardPosition = request.ChangesYardPosition;
        movement.ChangesStatus = request.ChangesStatus;
        movement.RequiresSurvey = request.RequiresSurvey;
        movement.IsActive = request.IsActive;
    }

    // ── service types ───────────────────────────────────────────────────────

    private static async Task<Ok<IReadOnlyList<ServiceTypeResponse>>> ListServicesAsync(
        MasterDataDbContext db, CancellationToken ct, bool includeInactive = false)
    {
        var services = db.ServiceTypes.AsNoTracking();
        if (!includeInactive) services = services.Where(s => s.IsActive);
        return TypedResults.Ok<IReadOnlyList<ServiceTypeResponse>>(
            await services.OrderBy(s => s.DisplayOrder).Select(ServiceProjection).ToListAsync(ct));
    }

    private static async Task<Results<Ok<ServiceTypeResponse>, ValidationProblem, ProblemHttpResult>> CreateServiceAsync(
        SaveServiceTypeRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var code = request.ServiceCode.ToUpperInvariant();
        if (await db.ServiceTypes.AnyAsync(s => s.ServiceCode == code, ct))
            return MasterDataSupport.Conflict($"Service type '{code}' already exists.");

        var service = new ServiceType { TenantId = caller.TenantId(), ServiceCode = code };
        Apply(service, request);
        db.ServiceTypes.Add(service);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(MapService(service));
    }

    private static async Task<Results<Ok<ServiceTypeResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateServiceAsync(
        string serviceCode, SaveServiceTypeRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var service = await db.ServiceTypes.SingleOrDefaultAsync(s => s.ServiceCode == serviceCode.FromRouteCode(), ct);
        if (service is null) return TypedResults.NotFound();
        if (!db.TrySetExpectedVersion(service, request.RowVersion))
            return MasterDataSupport.InvalidReference("rowVersion", "Send the rowVersion you received when reading the record.");

        Apply(service, request);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(MapService(service));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult, ValidationProblem>> DeleteServiceAsync(
        string serviceCode, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var service = await db.ServiceTypes.SingleOrDefaultAsync(s => s.ServiceCode == serviceCode.FromRouteCode(), ct);
        if (service is null) return TypedResults.NotFound();
        if (db.ExpectVersion(service, rowVersion) is { } missing) return missing;

        if (await db.OrderTypes.AnyAsync(o => o.ServiceTypeId == service.ServiceTypeId, ct))
            return MasterDataSupport.Conflict("Service type is still used by an order type.");

        db.ServiceTypes.Remove(service);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static void Apply(ServiceType service, SaveServiceTypeRequest request)
    {
        service.DescriptionEn = request.DescriptionEn;
        service.DescriptionLocal = request.DescriptionLocal;
        service.OriginForm = request.OriginForm;
        service.DestinationForm = request.DestinationForm;
        service.DisplayOrder = request.DisplayOrder;
        service.IsActive = request.IsActive;
    }

    // ── projections ─────────────────────────────────────────────────────────

    private static readonly Expression<Func<ChargeCode, ChargeCodeResponse>> ChargeProjection = c => new ChargeCodeResponse(
        c.ChargeCodeId, c.ChargeCode1, c.DescriptionEn, c.DescriptionLocal, c.ModuleCode, c.ChargeType,
        c.ChargeCategory, c.BillingUnitCode, c.IsByService, c.IsActive, Convert.ToBase64String(c.RowVersion));

    private static readonly Func<ChargeCode, ChargeCodeResponse> MapCharge = ChargeProjection.Compile();

    private static readonly Expression<Func<TaxCode, TaxCodeResponse>> TaxProjection = t => new TaxCodeResponse(
        t.TaxCodeId, t.TaxCode1, t.DescriptionEn, t.DescriptionLocal, t.CountryCode, t.TaxType, t.RatePct,
        t.EffectiveFrom, t.EffectiveTo, t.IsDefaultForType, t.OutputTaxGl, t.InputTaxGl, t.IsActive,
        Convert.ToBase64String(t.RowVersion));

    private static readonly Func<TaxCode, TaxCodeResponse> MapTax = TaxProjection.Compile();

    private static readonly Expression<Func<Movement, MovementResponse>> MovementProjection = m => new MovementResponse(
        m.MovementId, m.MovementCode, m.DescriptionEn, m.DescriptionLocal, m.FullEmpty, m.Direction,
        m.AppliesToModule, m.CodecoStatusCode, m.ChangesYardPosition, m.ChangesStatus, m.RequiresSurvey,
        m.IsActive, Convert.ToBase64String(m.RowVersion));

    private static readonly Func<Movement, MovementResponse> MapMovement = MovementProjection.Compile();

    private static readonly Expression<Func<ServiceType, ServiceTypeResponse>> ServiceProjection = s => new ServiceTypeResponse(
        s.ServiceTypeId, s.ServiceCode, s.DescriptionEn, s.DescriptionLocal, s.OriginForm, s.DestinationForm,
        s.DisplayOrder, s.IsActive, Convert.ToBase64String(s.RowVersion));

    private static readonly Func<ServiceType, ServiceTypeResponse> MapService = ServiceProjection.Compile();
}
