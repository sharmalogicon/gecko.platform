using System.ComponentModel.DataAnnotations;
using Gecko.Data;
using Gecko.MasterData.Application;
using Gecko.MasterData.Infrastructure.Persistence;
using Gecko.MasterData.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Endpoints.Config;

/// <summary>
/// RowVersion is the tenant's OWN row (a value it added, or its override of a
/// global one); null when the tenant has none and the value is the global row
/// as shipped. Send it back on PUT / DELETE.
/// </summary>
public sealed record CodeListValueResponse(
    string CategoryCode, string Code, string DescriptionEn, string? DescriptionLocal,
    string? IsoCode, short SortOrder, bool IsActive, bool IsTenantDefined, string? RowVersion = null);

/// <summary>A code list the tenant can see. Closed (AllowsTenantValues = false) = the platform branches on its values.</summary>
public sealed record CodeListCategoryResponse(
    string CategoryCode, string DescriptionEn, string OwningModule, bool AllowsTenantValues, int ValueCount);

public sealed record SaveCodeListValueRequest(
    [property: Required, MaxLength(40)] string CategoryCode,
    [property: Required, RegularExpression("^[A-Z0-9][A-Z0-9_-]{0,39}$")] string Code,
    [property: Required, MaxLength(200)] string DescriptionEn,
    [property: MaxLength(200)] string? DescriptionLocal = null,
    [property: MaxLength(20)] string? IsoCode = null,
    [property: Range(0, 9999)] short SortOrder = 100,
    bool IsActive = true,
    string? RowVersion = null);

public sealed record CodeMappingResponse(
    Guid CodeMappingId, string MappingType, string? CodeListCategory, Guid? PartyId, string? PartyCode,
    string Channel, string Direction, string ExternalCode, string InternalCode,
    string? Description, DateOnly? ValidFrom, DateOnly? ValidTo, bool IsActive, string RowVersion);

public sealed record SaveCodeMappingRequest(
    [property: Required, AllowedValues("CODE_LIST", "CARGO_CLASS", "PARTY", "VESSEL", "PORT", "ORDER_TYPE", "CHARGE_CODE", "MOVEMENT", "HOLD", "REPAIR_CODE", "DAMAGE_CODE", "CONTAINER_CONDITION", "EQUIPMENT_TYPE")] string MappingType,
    [property: Required, AllowedValues("ANY", "EDI_CODECO", "EDI_COPARN", "EDI_COARRI", "EDI_BAPLIE", "EDI_CUSCAR", "API", "EXCEL", "LEGACY_VECTOR")] string Channel,
    [property: Required, AllowedValues("INBOUND", "OUTBOUND", "BOTH")] string Direction,
    [property: Required, MaxLength(50)] string ExternalCode,
    [property: Required, MaxLength(50)] string InternalCode,
    [property: MaxLength(40)] string? CodeListCategory = null,
    Guid? PartyId = null,
    [property: MaxLength(200)] string? Description = null,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null,
    bool IsActive = true,
    string? RowVersion = null);

/// <summary>What an inbound code resolved to, and which layer answered.</summary>
public sealed record CodeResolutionResponse(
    string MappingType, string ExternalCode, string? InternalCode, bool Resolved,
    string ResolvedBy, string? Note);

public sealed record NumberSeriesResponse(
    Guid NumberSeriesId, Guid? BranchId, string SeriesKey, string? DocumentTypeCode, string? Description,
    string? Prefix, string Separator, bool IncludeBranchCode, string DatePartFormat, string ResetPeriod,
    byte NumberLength, long StartNumber, bool IsGapFreeRequired, bool IsActive, string RowVersion);

public sealed record SaveNumberSeriesRequest(
    [property: Required, RegularExpression("^[A-Z0-9][A-Z0-9_]{0,29}$")] string SeriesKey,
    [property: Required, AllowedValues("NONE", "YY", "YYYY", "YYMM", "YYYYMM")] string DatePartFormat,
    [property: Required, AllowedValues("NEVER", "YEARLY", "MONTHLY")] string ResetPeriod,
    [property: Range(3, 15)] byte NumberLength = 6,
    Guid? BranchId = null,
    [property: MaxLength(20)] string? DocumentTypeCode = null,
    [property: MaxLength(200)] string? Description = null,
    [property: MaxLength(20)] string? Prefix = null,
    [property: MaxLength(2)] string Separator = "-",
    bool IncludeBranchCode = false,
    [property: Range(1, 999999999)] long StartNumber = 1,
    bool IsGapFreeRequired = false,
    bool IsActive = true,
    string? RowVersion = null);

public sealed record NextNumberRequest(
    [property: Required, MaxLength(30)] string SeriesKey,
    Guid? BranchId = null,
    [property: MaxLength(30)] string? BranchCode = null);

public sealed record TenantSettingResponse(
    string SettingKey, string? Value, string? TenantValue, string? BranchValue, string? DefaultValue,
    string ValueType, string AllowedScope, string OwningModule, string DescriptionEn, string ResolvedFrom);

public sealed record SaveTenantSettingRequest(
    [property: Required, MaxLength(100)] string SettingKey,
    [property: MaxLength(4000)] string? SettingValue,
    Guid? BranchId = null);

/// <summary>
/// The tenant's own configuration layer: code lists it extends, partner codes it
/// translates, document numbers it issues, and settings that change behaviour.
///
/// CODE MAPPINGS ARE THE EDI SEAM. Resolution order, which the C# must implement
/// and this endpoint exposes: this partner's mapping -> the tenant's default
/// mapping -> the global standard. Vector kept this in Config.Mapper with a
/// CustomerCode and a free-text value, and it is how '20HC' came to resolve to
/// a 8'6" ISO code for one customer and not another.
/// </summary>
internal static class ConfigEndpoints
{
    public static RouteGroupBuilder MapConfigEndpoints(this RouteGroupBuilder master)
    {
        var codeLists = master.MapGroup("/code-lists").WithTags("Master data — code lists");
        codeLists.MapGet("/", ListCategoriesAsync).RequirePermission(MasterDataPermissions.ConfigView).WithSummary("List the code list categories with how many values this tenant sees in each");
        codeLists.MapGet("/{categoryCode}", ListCodeValuesAsync).RequirePermission(MasterDataPermissions.ConfigView).WithSummary("List a code list as this tenant sees it (global + tenant overrides)");
        codeLists.MapPut("/{categoryCode}/{code}", UpsertCodeValueAsync).RequirePermission(MasterDataPermissions.ConfigManage).Validate<SaveCodeListValueRequest>().WithSummary("Add or override a code list value for this tenant");
        codeLists.MapDelete("/{categoryCode}/{code}", DeleteCodeValueAsync).RequirePermission(MasterDataPermissions.ConfigManage).WithSummary("Remove this tenant's override (the global value, if any, reappears)");

        var mappings = master.MapGroup("/code-mappings").WithTags("Master data — code mappings");
        mappings.MapGet("/", ListMappingsAsync).RequirePermission(MasterDataPermissions.ConfigView).WithSummary("List partner code mappings");
        mappings.MapGet("/resolve", ResolveMappingAsync).RequirePermission(MasterDataPermissions.ConfigView).WithSummary("Resolve an inbound code: partner mapping, then tenant default, then the global standard");
        mappings.MapPost("/", CreateMappingAsync).RequirePermission(MasterDataPermissions.ConfigManage).Validate<SaveCodeMappingRequest>().WithSummary("Create a code mapping");
        mappings.MapPut("/{codeMappingId:guid}", UpdateMappingAsync).RequirePermission(MasterDataPermissions.ConfigManage).Validate<SaveCodeMappingRequest>().WithSummary("Update a code mapping");
        mappings.MapDelete("/{codeMappingId:guid}", DeleteMappingAsync).RequirePermission(MasterDataPermissions.ConfigManage).WithSummary("Soft-delete a code mapping");

        var series = master.MapGroup("/number-series").WithTags("Master data — number series");
        series.MapGet("/", ListSeriesAsync).RequirePermission(MasterDataPermissions.ConfigView).WithSummary("List document number series");
        series.MapPost("/", CreateSeriesAsync).RequirePermission(MasterDataPermissions.ConfigManage).Validate<SaveNumberSeriesRequest>().WithSummary("Create a number series");
        series.MapPost("/next", NextNumberAsync).RequirePermission(MasterDataPermissions.ConfigView).Validate<NextNumberRequest>().WithSummary("Issue the next number (branch series wins over tenant-wide)");
        series.MapDelete("/{numberSeriesId:guid}", DeleteSeriesAsync).RequirePermission(MasterDataPermissions.ConfigManage).WithSummary("Soft-delete a number series");

        var settings = master.MapGroup("/settings").WithTags("Master data — tenant settings");
        settings.MapGet("/", ListSettingsAsync).RequirePermission(MasterDataPermissions.ConfigView).WithSummary("List every declared setting with its resolved value");
        settings.MapPut("/", UpsertSettingAsync).RequirePermission(MasterDataPermissions.ConfigManage).Validate<SaveTenantSettingRequest>().WithSummary("Set or clear a tenant or branch setting value");

        return master;
    }

    // ── code lists ──────────────────────────────────────────────────────────

    private static async Task<Ok<IReadOnlyList<CodeListCategoryResponse>>> ListCategoriesAsync(
        MasterDataDbContext db, CancellationToken ct)
    {
        var counts = await db.VwCodeLists.AsNoTracking()
            .Where(v => v.IsActive == true)
            .GroupBy(v => v.CategoryCode)
            .Select(g => new { Category = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Category ?? "", g => g.Count, ct);
        var categories = await db.CodeListCategories.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.CategoryCode)
            .ToListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<CodeListCategoryResponse>>(categories
            .Select(c => new CodeListCategoryResponse(c.CategoryCode, c.DescriptionEn, c.OwningModule, c.AllowsTenantValues,
                counts.GetValueOrDefault(c.CategoryCode)))
            .ToList());
    }

    private static async Task<Ok<IReadOnlyList<CodeListValueResponse>>> ListCodeValuesAsync(
        string categoryCode, MasterDataDbContext db, CancellationToken ct, bool includeInactive = false)
    {
        var category = categoryCode.ToUpperInvariant();
        var values = db.VwCodeLists.AsNoTracking().Where(v => v.CategoryCode == category);
        if (!includeInactive) values = values.Where(v => v.IsActive == true);

        // The view resolves global + tenant but carries no row_version; the tenant's
        // own rows (few per category) are read beside it for the concurrency token.
        var own = await db.CodeListValues.AsNoTracking().Where(v => v.CategoryCode == category)
            .Select(v => new { v.Code, v.RowVersion }).ToListAsync(ct);
        var versions = own.ToDictionary(v => v.Code, v => Convert.ToBase64String(v.RowVersion), StringComparer.OrdinalIgnoreCase);

        var rows = await values.OrderBy(v => v.SortOrder).ThenBy(v => v.Code).ToListAsync(ct);
        // A VIEW scaffolds every column nullable, so the non-null contract this
        // API promises is restored here rather than leaked to the caller.
        return TypedResults.Ok<IReadOnlyList<CodeListValueResponse>>(rows
            .Select(v => new CodeListValueResponse(
                v.CategoryCode ?? category, v.Code ?? "", v.DescriptionEn ?? "", v.DescriptionLocal, v.IsoCode,
                v.SortOrder ?? (short)100, v.IsActive ?? false, v.IsTenantDefined ?? false,
                versions.GetValueOrDefault(v.Code ?? "")))
            .ToList());
    }

    private static async Task<Results<Ok<CodeListValueResponse>, ValidationProblem, ProblemHttpResult>> UpsertCodeValueAsync(
        string categoryCode, string code, SaveCodeListValueRequest request,
        MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var category = categoryCode.ToUpperInvariant();
        var value = code.ToUpperInvariant();

        var definition = await db.CodeListCategories.AsNoTracking()
            .Where(c => c.CategoryCode == category && c.IsActive)
            .Select(c => new { c.AllowsTenantValues })
            .SingleOrDefaultAsync(ct);
        if (definition is null)
            return MasterDataSupport.InvalidReference("categoryCode", $"Unknown code list category '{category}'.");

        // A CLOSED category is one the code branches on — HOLD_EVENT, REPAIR_MODE,
        // PICKUP_DROPOFF_MODE. A tenant value there is a row nothing ever acts on.
        var isGlobal = await db.CodeListValues1.AsNoTracking().AnyAsync(v => v.CategoryCode == category && v.Code == value, ct);
        if (!definition.AllowsTenantValues && !isGlobal)
            return MasterDataSupport.InvalidReference("code",
                $"'{category}' is a closed category — the platform branches on its values, so a new one would never be acted on.");

        // The tenant's own row is versioned like every tenant row: editing one
        // needs the version read with it. No own row yet = this PUT creates it; a
        // caller that sent a version for a row that is gone lost a race.
        var existing = await db.CodeListValues.SingleOrDefaultAsync(v => v.CategoryCode == category && v.Code == value, ct);
        if (existing is not null)
        {
            if (db.ExpectVersion(existing, request.RowVersion) is { } missing) return missing;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(request.RowVersion))
                return MasterDataSupport.Conflict("The record changed since you loaded it.",
                    "This tenant's value was removed meanwhile. Re-read the list and apply your change again.");
            existing = new CodeListValue { TenantId = caller.TenantId(), CategoryCode = category, Code = value };
            db.CodeListValues.Add(existing);
        }

        existing.DescriptionEn = request.DescriptionEn;
        existing.DescriptionLocal = request.DescriptionLocal;
        existing.IsoCode = request.IsoCode;
        existing.SortOrder = request.SortOrder;
        existing.IsActive = request.IsActive;

        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        return TypedResults.Ok(new CodeListValueResponse(
            category, value, existing.DescriptionEn, existing.DescriptionLocal,
            existing.IsoCode, existing.SortOrder, existing.IsActive, !isGlobal, Convert.ToBase64String(existing.RowVersion)));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> DeleteCodeValueAsync(
        string categoryCode, string code, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var value = await db.CodeListValues.SingleOrDefaultAsync(
            v => v.CategoryCode == categoryCode.ToUpperInvariant() && v.Code == code.ToUpperInvariant(), ct);
        if (value is null) return TypedResults.NotFound();
        if (db.ExpectVersion(value, rowVersion) is { } missing) return missing;

        db.CodeListValues.Remove(value);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    // ── code mappings ───────────────────────────────────────────────────────

    private static async Task<Ok<PagedResult<CodeMappingResponse>>> ListMappingsAsync(
        [AsParameters] ListQuery query, MasterDataDbContext db, CancellationToken ct,
        string? mappingType = null, Guid? partyId = null, string? channel = null)
    {
        var mappings = db.CodeMappings.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(mappingType)) mappings = mappings.Where(m => m.MappingType == mappingType.ToUpperInvariant());
        if (partyId is not null) mappings = mappings.Where(m => m.PartyId == partyId);
        if (!string.IsNullOrWhiteSpace(channel)) mappings = mappings.Where(m => m.Channel == channel.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(query.Search))
            mappings = mappings.Where(m => m.ExternalCode.Contains(query.Search) || m.InternalCode.Contains(query.Search));

        var ordered = mappings.OrderBy(m => m.MappingType).ThenBy(m => m.ExternalCode);
        return TypedResults.Ok(await Project(db, ordered).ToPagedAsync(query.Page, query.PageSize, ct));
    }

    /// <summary>
    /// The resolution the EDI reader performs, exposed so it can be tested and
    /// explained rather than reimplemented per message type.
    /// </summary>
    private static async Task<Ok<CodeResolutionResponse>> ResolveMappingAsync(
        string mappingType, string externalCode, MasterDataDbContext db, CancellationToken ct,
        Guid? partyId = null, string channel = "ANY")
    {
        var type = mappingType.ToUpperInvariant();
        var external = externalCode.ToUpperInvariant();
        var wire = channel.ToUpperInvariant();

        var inbound = db.CodeMappings.AsNoTracking()
            .Where(m => m.MappingType == type && m.ExternalCode == external && m.IsActive
                        && (m.Direction == "INBOUND" || m.Direction == "BOTH")
                        && (m.Channel == wire || m.Channel == "ANY"));

        // 1. this partner's own spelling
        if (partyId is not null)
        {
            var partnerMatch = await inbound.Where(m => m.PartyId == partyId).Select(m => m.InternalCode).FirstOrDefaultAsync(ct);
            if (partnerMatch is not null)
                return TypedResults.Ok(new CodeResolutionResponse(type, external, partnerMatch, true, "PARTNER_MAPPING", null));
        }

        // 2. the tenant's default mapping
        var tenantMatch = await inbound.Where(m => m.PartyId == null).Select(m => m.InternalCode).FirstOrDefaultAsync(ct);
        if (tenantMatch is not null)
            return TypedResults.Ok(new CodeResolutionResponse(type, external, tenantMatch, true, "TENANT_MAPPING", null));

        // 3. the global standard — only equipment types have one
        if (type == "EQUIPMENT_TYPE")
        {
            var viaIso = await db.EquipmentTypeIsoCodes.AsNoTracking()
                .Where(m => m.IsoCode == external)
                .Join(db.EquipmentTypes, m => m.EquipmentTypeId, t => t.EquipmentTypeId, (m, t) => t.TypeCode)
                .FirstOrDefaultAsync(ct);
            if (viaIso is not null)
                return TypedResults.Ok(new CodeResolutionResponse(type, external, viaIso, true, "ISO_STANDARD", null));
        }

        return TypedResults.Ok(new CodeResolutionResponse(type, external, null, false, "NONE",
            "No partner mapping, no tenant mapping and no global standard match. Reject the message rather than guessing."));
    }

    private static async Task<Results<Ok<CodeMappingResponse>, ValidationProblem, ProblemHttpResult>> CreateMappingAsync(
        SaveCodeMappingRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        if (await ValidateMappingAsync(db, request, ct) is { } problem) return problem;
        if (await DuplicateMappingAsync(db, request, exceptId: null, ct) is { } duplicate) return duplicate;

        var mapping = new CodeMapping { TenantId = caller.TenantId() };
        Apply(mapping, request);
        db.CodeMappings.Add(mapping);
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await Project(db, db.CodeMappings.AsNoTracking().Where(m => m.CodeMappingId == mapping.CodeMappingId)).SingleAsync(ct));
    }

    private static async Task<Results<Ok<CodeMappingResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateMappingAsync(
        Guid codeMappingId, SaveCodeMappingRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var mapping = await db.CodeMappings.SingleOrDefaultAsync(m => m.CodeMappingId == codeMappingId, ct);
        if (mapping is null) return TypedResults.NotFound();
        if (!db.TrySetExpectedVersion(mapping, request.RowVersion))
            return MasterDataSupport.InvalidReference("rowVersion", "Send the rowVersion you received when reading the record.");
        if (await ValidateMappingAsync(db, request, ct) is { } problem) return problem;
        if (await DuplicateMappingAsync(db, request, exceptId: codeMappingId, ct) is { } duplicate) return duplicate;

        Apply(mapping, request);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        return TypedResults.Ok(await Project(db, db.CodeMappings.AsNoTracking().Where(m => m.CodeMappingId == codeMappingId)).SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> DeleteMappingAsync(
        Guid codeMappingId, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var mapping = await db.CodeMappings.SingleOrDefaultAsync(m => m.CodeMappingId == codeMappingId, ct);
        if (mapping is null) return TypedResults.NotFound();
        if (db.ExpectVersion(mapping, rowVersion) is { } missing) return missing;
        db.CodeMappings.Remove(mapping);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static void Apply(CodeMapping mapping, SaveCodeMappingRequest request)
    {
        mapping.MappingType = request.MappingType;
        mapping.CodeListCategory = request.CodeListCategory?.ToUpperInvariant();
        mapping.PartyId = request.PartyId;
        mapping.Channel = request.Channel;
        mapping.Direction = request.Direction;
        mapping.ExternalCode = request.ExternalCode.ToUpperInvariant();
        mapping.InternalCode = request.InternalCode.ToUpperInvariant();
        mapping.Description = request.Description;
        mapping.ValidFrom = request.ValidFrom;
        mapping.ValidTo = request.ValidTo;
        mapping.IsActive = request.IsActive;
    }

    private static async Task<ValidationProblem?> ValidateMappingAsync(
        MasterDataDbContext db, SaveCodeMappingRequest request, CancellationToken ct)
    {
        // Mirrors ck_code_mapping__category: only a CODE_LIST mapping names a category.
        if (request.MappingType == "CODE_LIST" && request.CodeListCategory is null)
            return MasterDataSupport.InvalidReference("codeListCategory", "A CODE_LIST mapping must name the category it maps.");
        if (request.MappingType != "CODE_LIST" && request.CodeListCategory is not null)
            return MasterDataSupport.InvalidReference("codeListCategory", $"A {request.MappingType} mapping does not take a code list category.");

        if (request.ValidTo is not null && request.ValidFrom is not null && request.ValidTo < request.ValidFrom)
            return MasterDataSupport.InvalidReference("validTo", "Valid-to cannot be before valid-from.");

        if (request.PartyId is not null && !await db.Parties.AnyAsync(p => p.PartyId == request.PartyId, ct))
            return MasterDataSupport.InvalidReference("partyId", "Unknown party for this tenant.");

        // internal_code is a soft reference into the master the type names. A
        // mapping to a code that does not exist resolves an inbound message to
        // nothing the platform knows — worse than no mapping, which is rejected.
        var code = request.InternalCode.Trim().ToUpperInvariant();
        var category = request.CodeListCategory?.ToUpperInvariant();
        var known = request.MappingType switch
        {
            "CODE_LIST" => await db.VwCodeLists.AnyAsync(v => v.CategoryCode == category && v.Code == code, ct),
            "CARGO_CLASS" => await db.CargoClasses.AnyAsync(x => x.Code == code, ct),
            "PARTY" => await db.Parties.AnyAsync(x => x.PartyCode == code, ct),
            "VESSEL" => await db.Vessels.AnyAsync(x => x.VesselCode == code, ct),
            "PORT" => await db.Ports.AnyAsync(x => x.PortCode == code, ct),
            "ORDER_TYPE" => await db.OrderTypes.AnyAsync(x => x.OrderTypeCode == code, ct),
            "CHARGE_CODE" => await db.ChargeCodes.AnyAsync(x => x.ChargeCode1 == code, ct),
            "MOVEMENT" => await db.Movements.AnyAsync(x => x.MovementCode == code, ct),
            "HOLD" => await db.Holds.AnyAsync(x => x.HoldCode == code, ct),
            "REPAIR_CODE" => await db.RepairCodes.AnyAsync(x => x.RepairCode1 == code, ct),
            "DAMAGE_CODE" => await db.DamageCodes.AnyAsync(x => x.DamageCode1 == code, ct),
            "CONTAINER_CONDITION" => await db.ContainerConditions.AnyAsync(x => x.ConditionCode == code, ct),
            "EQUIPMENT_TYPE" => await db.EquipmentTypes.AnyAsync(x => x.TypeCode == code, ct),
            _ => true,
        };
        if (!known)
            return MasterDataSupport.InvalidReference("internalCode",
                request.MappingType == "CODE_LIST"
                    ? $"'{code}' is not a value of the {category} code list."
                    : $"'{code}' is not a {request.MappingType.Replace('_', ' ').ToLowerInvariant()} in this tenant's master data.");

        return null;
    }

    /// <summary>
    /// The four filtered unique indexes, checked first so a clash is a 409 that
    /// names the mapping already there instead of a 500 from the index: inbound,
    /// one internal code per external code; outbound, one external code per
    /// internal code — per partner, or tenant-wide when there is none.
    /// </summary>
    private static async Task<ProblemHttpResult?> DuplicateMappingAsync(
        MasterDataDbContext db, SaveCodeMappingRequest request, Guid? exceptId, CancellationToken ct)
    {
        var category = request.CodeListCategory?.ToUpperInvariant();
        var external = request.ExternalCode.Trim().ToUpperInvariant();
        var internalCode = request.InternalCode.Trim().ToUpperInvariant();
        var same = db.CodeMappings.AsNoTracking().Where(m =>
            m.MappingType == request.MappingType && m.CodeListCategory == category && m.PartyId == request.PartyId
            && m.Channel == request.Channel && m.CodeMappingId != exceptId);

        if (request.Direction is "INBOUND" or "BOTH"
            && await same.Where(m => (m.Direction == "INBOUND" || m.Direction == "BOTH") && m.ExternalCode == external)
                .Select(m => m.InternalCode).FirstOrDefaultAsync(ct) is { } already)
            return MasterDataSupport.Conflict("This inbound code is already mapped.",
                $"{external} already maps to {already} on {request.Channel}. Edit that mapping instead of adding a second one.");

        if (request.Direction is "OUTBOUND" or "BOTH"
            && await same.Where(m => (m.Direction == "OUTBOUND" || m.Direction == "BOTH") && m.InternalCode == internalCode)
                .Select(m => m.ExternalCode).FirstOrDefaultAsync(ct) is { } sent)
            return MasterDataSupport.Conflict("This outbound code is already mapped.",
                $"{internalCode} is already sent as {sent} on {request.Channel}. Edit that mapping instead of adding a second one.");

        return null;
    }

    private static IQueryable<CodeMappingResponse> Project(MasterDataDbContext db, IQueryable<CodeMapping> mappings) =>
        from m in mappings
        join p in db.Parties on m.PartyId equals p.PartyId into parties
        from p in parties.DefaultIfEmpty()
        select new CodeMappingResponse(
            m.CodeMappingId, m.MappingType, m.CodeListCategory, m.PartyId, p == null ? null : p.PartyCode,
            m.Channel, m.Direction, m.ExternalCode, m.InternalCode, m.Description,
            m.ValidFrom, m.ValidTo, m.IsActive, Convert.ToBase64String(m.RowVersion));

    // ── number series ───────────────────────────────────────────────────────

    private static readonly HashSet<string> TosOwnedSeriesKeys = ["BOOKING", "EIR", "TRUCK_VISIT", "GATE_PASS"];

    private static async Task<Ok<IReadOnlyList<NumberSeriesResponse>>> ListSeriesAsync(
        MasterDataDbContext db, CancellationToken ct, bool includeInactive = false)
    {
        var series = db.NumberSeries.AsNoTracking();
        if (!includeInactive) series = series.Where(s => s.IsActive);
        return TypedResults.Ok<IReadOnlyList<NumberSeriesResponse>>(await series
            .OrderBy(s => s.SeriesKey).ThenBy(s => s.BranchId)
            .Select(s => new NumberSeriesResponse(
                s.NumberSeriesId, s.BranchId, s.SeriesKey, s.DocumentTypeCode, s.Description, s.Prefix,
                s.Separator, s.IncludeBranchCode, s.DatePartFormat, s.ResetPeriod, s.NumberLength,
                s.StartNumber, s.IsGapFreeRequired, s.IsActive, Convert.ToBase64String(s.RowVersion)))
            .ToListAsync(ct));
    }

    private static async Task<Results<Ok<NumberSeriesResponse>, ValidationProblem, ProblemHttpResult>> CreateSeriesAsync(
        SaveNumberSeriesRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var key = request.SeriesKey.ToUpperInvariant();

        // Mirrors ck_number_series__not_tos_owned (gecko_master 18): these documents
        // are numbered inside gecko_tos, where the barrier issues them.
        if (TosOwnedSeriesKeys.Contains(key))
            return MasterDataSupport.InvalidReference("seriesKey", $"'{key}' is numbered by the TOS module, not master data.");

        // Mirrors ck_number_series__reset_needs_date: a series that resets yearly
        // but carries no year in the number produces duplicates every January.
        if (request.ResetPeriod == "YEARLY" && request.DatePartFormat == "NONE")
            return MasterDataSupport.InvalidReference("datePartFormat", "A YEARLY reset needs a year in the number, or it repeats itself every January.");
        if (request.ResetPeriod == "MONTHLY" && request.DatePartFormat is not ("YYMM" or "YYYYMM"))
            return MasterDataSupport.InvalidReference("datePartFormat", "A MONTHLY reset needs YYMM or YYYYMM in the number.");

        if (request.DocumentTypeCode is not null &&
            !await db.DocumentTypes.AnyAsync(d => d.Code == request.DocumentTypeCode.ToUpperInvariant(), ct))
            return MasterDataSupport.InvalidReference("documentTypeCode", $"Unknown document type '{request.DocumentTypeCode}'.");

        var clash = await db.NumberSeries.AnyAsync(s => s.SeriesKey == key && s.BranchId == request.BranchId, ct);
        if (clash)
            return MasterDataSupport.Conflict($"A number series '{key}' already exists for that scope.");

        var series = new NumberSeries
        {
            TenantId = caller.TenantId(),
            BranchId = request.BranchId,
            SeriesKey = key,
            DocumentTypeCode = request.DocumentTypeCode?.ToUpperInvariant(),
            Description = request.Description,
            Prefix = request.Prefix?.ToUpperInvariant(),
            Separator = request.Separator,
            IncludeBranchCode = request.IncludeBranchCode,
            DatePartFormat = request.DatePartFormat,
            ResetPeriod = request.ResetPeriod,
            NumberLength = request.NumberLength,
            StartNumber = request.StartNumber,
            IsGapFreeRequired = request.IsGapFreeRequired,
            IsActive = request.IsActive,
        };
        db.NumberSeries.Add(series);
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(new NumberSeriesResponse(
            series.NumberSeriesId, series.BranchId, series.SeriesKey, series.DocumentTypeCode, series.Description,
            series.Prefix, series.Separator, series.IncludeBranchCode, series.DatePartFormat, series.ResetPeriod,
            series.NumberLength, series.StartNumber, series.IsGapFreeRequired, series.IsActive,
            Convert.ToBase64String(series.RowVersion)));
    }

    /// <summary>
    /// Issues the next number through config.usp_next_number — the proc, not a
    /// SELECT-then-UPDATE here. The proc takes UPDLOCK/HOLDLOCK on the counter row,
    /// which is what stops two gates issuing the same EIR number under load.
    /// </summary>
    private static async Task<Results<Ok<object>, ValidationProblem, ProblemHttpResult>> NextNumberAsync(
        NextNumberRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var key = request.SeriesKey.ToUpperInvariant();
        try
        {
            var number = new Microsoft.Data.SqlClient.SqlParameter
            {
                ParameterName = "@number",
                SqlDbType = System.Data.SqlDbType.NVarChar,
                Size = 60,
                Direction = System.Data.ParameterDirection.Output,
            };

            await db.Database.ExecuteSqlRawAsync(
                "EXEC config.usp_next_number @series_key, @branch_id, @branch_code, NULL, @number OUTPUT",
                [
                    new Microsoft.Data.SqlClient.SqlParameter("@series_key", key),
                    new Microsoft.Data.SqlClient.SqlParameter("@branch_id", (object?)request.BranchId ?? DBNull.Value),
                    new Microsoft.Data.SqlClient.SqlParameter("@branch_code", (object?)request.BranchCode ?? DBNull.Value),
                    number,
                ], ct);

            return TypedResults.Ok<object>(new { seriesKey = key, number = number.Value as string });
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 50020 or 50021 or 50022)
        {
            // The proc's own THROWs: no such series, branch code required, overflow.
            return MasterDataSupport.InvalidReference("seriesKey", e.Message);
        }
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> DeleteSeriesAsync(
        Guid numberSeriesId, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var series = await db.NumberSeries.SingleOrDefaultAsync(s => s.NumberSeriesId == numberSeriesId, ct);
        if (series is null) return TypedResults.NotFound();
        if (db.ExpectVersion(series, rowVersion) is { } missing) return missing;
        db.NumberSeries.Remove(series);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    // ── settings ────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists every DECLARED setting with its resolved value and where that value
    /// came from — not just the rows a tenant happens to have overridden. An admin
    /// screen that only shows overrides cannot answer "what is free storage set to"
    /// for the 90% of settings nobody has touched.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<TenantSettingResponse>>> ListSettingsAsync(
        MasterDataDbContext db, CancellationToken ct, Guid? branchId = null, string? owningModule = null)
    {
        var definitions = db.SettingDefinitions.AsNoTracking().Where(d => d.IsActive);
        if (!string.IsNullOrWhiteSpace(owningModule))
            definitions = definitions.Where(d => d.OwningModule == owningModule.ToUpperInvariant());

        var declared = await definitions.OrderBy(d => d.OwningModule).ThenBy(d => d.SettingKey).ToListAsync(ct);
        var overrides = await db.TenantSettings.AsNoTracking()
            .Select(s => new { s.SettingKey, s.BranchId, s.SettingValue }).ToListAsync(ct);

        var result = declared.Select(d =>
        {
            var branchValue = branchId is null || d.AllowedScope != "BRANCH"
                ? null
                : overrides.SingleOrDefault(o => o.SettingKey == d.SettingKey && o.BranchId == branchId)?.SettingValue;
            var tenantValue = overrides.SingleOrDefault(o => o.SettingKey == d.SettingKey && o.BranchId == null)?.SettingValue;

            var (value, from) =
                branchValue is not null ? (branchValue, "BRANCH")
                : tenantValue is not null ? (tenantValue, "TENANT")
                : (d.DefaultValue, "DEFAULT");

            return new TenantSettingResponse(
                d.SettingKey, value, tenantValue, branchValue, d.DefaultValue,
                d.ValueType, d.AllowedScope, d.OwningModule, d.DescriptionEn, from);
        }).ToList();

        return TypedResults.Ok<IReadOnlyList<TenantSettingResponse>>(result);
    }

    private static async Task<Results<Ok<TenantSettingResponse>, ValidationProblem, ProblemHttpResult>> UpsertSettingAsync(
        SaveTenantSettingRequest request, MasterDataDbContext db, ITenantContext caller,
        TenantSettingReader reader, CancellationToken ct)
    {
        var key = request.SettingKey;
        var definition = await db.SettingDefinitions.AsNoTracking()
            .Where(d => d.SettingKey == key && d.IsActive).SingleOrDefaultAsync(ct);
        if (definition is null)
            return MasterDataSupport.InvalidReference("settingKey", $"'{key}' is not a declared setting.");

        if (request.BranchId is not null && definition.AllowedScope != "BRANCH")
            return MasterDataSupport.InvalidReference("branchId", $"'{key}' is TENANT-scoped; it cannot be set per branch.");

        if (request.SettingValue is not null && !ParsesAs(definition.ValueType, request.SettingValue))
            return MasterDataSupport.InvalidReference("settingValue", $"'{key}' is declared {definition.ValueType}; '{request.SettingValue}' does not parse as one.");

        var existing = await db.TenantSettings.SingleOrDefaultAsync(
            s => s.SettingKey == key && s.BranchId == request.BranchId, ct);

        if (request.SettingValue is null)
        {
            // Clearing the value falls back to the next layer down, which is the
            // point of having declared defaults at all.
            if (existing is not null) db.TenantSettings.Remove(existing);
        }
        else if (existing is null)
        {
            db.TenantSettings.Add(new TenantSetting
            {
                TenantId = caller.TenantId(), BranchId = request.BranchId,
                SettingKey = key, SettingValue = request.SettingValue,
            });
        }
        else
        {
            existing.SettingValue = request.SettingValue;
        }

        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        var resolved = await reader.GetAsync(key, request.BranchId, ct);
        return TypedResults.Ok(new TenantSettingResponse(
            key, resolved, null, null, definition.DefaultValue,
            definition.ValueType, definition.AllowedScope, definition.OwningModule, definition.DescriptionEn,
            request.SettingValue is null ? "DEFAULT" : request.BranchId is null ? "TENANT" : "BRANCH"));
    }

    private static bool ParsesAs(string valueType, string value) => valueType switch
    {
        "BOOL" => value is "true" or "false",
        "INT" => long.TryParse(value, out _),
        "DECIMAL" => decimal.TryParse(value, out _),
        "DATE" => DateOnly.TryParse(value, out _),
        "JSON" => value.TrimStart().StartsWith('{') || value.TrimStart().StartsWith('['),
        _ => true,
    };
}
