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

namespace Gecko.MasterData.Endpoints.Equipment;

public sealed record EquipmentTypeResponse(
    Guid EquipmentTypeId, string TypeCode, string DescriptionEn, string? DescriptionLocal,
    decimal LengthFt, string HeightClass, string IsoGroupCode, decimal Teu,
    bool IsReefer, bool IsOog, bool IsTank,
    decimal? TareWeightKg, decimal? MaxPayloadKg, decimal? MaxGrossKg,
    string? DisplayColorHex, short SortOrder, bool IsActive,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string RowVersion);

public sealed record EquipmentTypeDetailResponse(EquipmentTypeResponse Type, IReadOnlyList<IsoMappingResponse> IsoCodes);

public sealed record IsoMappingResponse(string IsoCode, bool IsDefaultOutbound, string? IsoDescription);

/// <summary>What the gate actually asks: "a partner sent me this ISO code — what is it to us?"</summary>
public sealed record IsoResolutionResponse(
    string IsoCode, bool KnownToStandard, Guid? EquipmentTypeId, string? TypeCode, string? DescriptionEn,
    string? SupersededBy, string? Note);

public sealed record CreateEquipmentTypeRequest(
    [property: Required, RegularExpression("^[A-Z0-9][A-Z0-9-]{0,9}$", ErrorMessage = "Upper-case letters, digits and '-', 1-10 chars, e.g. 20GP.")] string TypeCode,
    [property: Required, MaxLength(200)] string DescriptionEn,
    [property: Required, AllowedValues(10, 20, 24, 30, 40, 41, 43, 45, 48, 49, 53)] int LengthFt,
    [property: Required, AllowedValues("STANDARD", "HIGH_CUBE", "HALF")] string HeightClass,
    [property: Required, RegularExpression("^[A-Z]{2}$", ErrorMessage = "ISO type group, 2 upper-case letters, e.g. GP.")] string IsoGroupCode,
    [property: Required, Range(0.25, 4.0)] decimal Teu,
    bool IsReefer = false,
    bool IsOog = false,
    bool IsTank = false,
    [property: MaxLength(200)] string? DescriptionLocal = null,
    [property: Range(1, 50000)] decimal? TareWeightKg = null,
    [property: Range(1, 100000)] decimal? MaxPayloadKg = null,
    [property: Range(1, 100000)] decimal? MaxGrossKg = null,
    [property: RegularExpression("^#[0-9A-Fa-f]{6}$", ErrorMessage = "Hex colour, e.g. #3B82F6.")] string? DisplayColorHex = null,
    [property: Range(0, 9999)] short SortOrder = 100);

public sealed record UpdateEquipmentTypeRequest(
    [property: Required] string RowVersion,
    [property: Required, MaxLength(200)] string DescriptionEn,
    [property: Required, AllowedValues(10, 20, 24, 30, 40, 41, 43, 45, 48, 49, 53)] int LengthFt,
    [property: Required, AllowedValues("STANDARD", "HIGH_CUBE", "HALF")] string HeightClass,
    [property: Required, RegularExpression("^[A-Z]{2}$")] string IsoGroupCode,
    [property: Required, Range(0.25, 4.0)] decimal Teu,
    bool IsReefer,
    bool IsOog,
    bool IsTank,
    bool IsActive,
    [property: MaxLength(200)] string? DescriptionLocal = null,
    [property: Range(1, 50000)] decimal? TareWeightKg = null,
    [property: Range(1, 100000)] decimal? MaxPayloadKg = null,
    [property: Range(1, 100000)] decimal? MaxGrossKg = null,
    [property: RegularExpression("^#[0-9A-Fa-f]{6}$")] string? DisplayColorHex = null,
    [property: Range(0, 9999)] short SortOrder = 100);

public sealed record IsoMappingItem(
    [property: Required, RegularExpression("^[A-Z0-9]{4}$", ErrorMessage = "ISO 6346 size-type code, 4 characters, e.g. 22G1.")] string IsoCode,
    bool IsDefaultOutbound = false);

public sealed record ReplaceIsoMappingRequest(
    [property: Required, MinLength(1)] IReadOnlyList<IsoMappingItem> IsoCodes);

/// <summary>
/// The tenant's own equipment vocabulary, and the mapping from the global ISO
/// 6346 size-type codes onto it.
///
/// THE DESIGN IN ONE SENTENCE (decision 2026-09-15): ISO codes are GLOBAL and
/// nobody owns them; equipment_type is the TENANT's name for a box; the mapping
/// is MANY ISO -> ONE local type. SCT calls a 20ft dry box 20GP and SIAM-COMMERCIAL
/// calls it 20DV, and 22G1 resolves correctly for both because the mapping is
/// per tenant, not per code.
/// </summary>
internal static class EquipmentTypeEndpoints
{
    public static RouteGroupBuilder MapEquipmentTypeEndpoints(this RouteGroupBuilder master)
    {
        var types = master.MapGroup("/equipment-types").WithTags("Master data — equipment types");

        types.MapGet("/", ListAsync)
            .RequirePermission(MasterDataPermissions.EquipmentView)
            .WithSummary("List the tenant's equipment types");

        types.MapGet("/resolve/{isoCode}", ResolveAsync)
            .RequirePermission(MasterDataPermissions.EquipmentView)
            .WithSummary("Resolve a global ISO 6346 code to this tenant's local equipment type");

        types.MapGet("/{equipmentTypeId:guid}", GetAsync)
            .RequirePermission(MasterDataPermissions.EquipmentView)
            .WithName("GetEquipmentType")
            .WithSummary("Get one equipment type with its ISO code mapping");

        types.MapPost("/", CreateAsync)
            .RequirePermission(MasterDataPermissions.EquipmentManage)
            .Validate<CreateEquipmentTypeRequest>()
            .WithSummary("Create an equipment type");

        types.MapPut("/{equipmentTypeId:guid}", UpdateAsync)
            .RequirePermission(MasterDataPermissions.EquipmentManage)
            .Validate<UpdateEquipmentTypeRequest>()
            .WithSummary("Update an equipment type (type code is immutable)");

        types.MapPut("/{equipmentTypeId:guid}/iso-codes", ReplaceIsoCodesAsync)
            .RequirePermission(MasterDataPermissions.EquipmentManage)
            .Validate<ReplaceIsoMappingRequest>()
            .WithSummary("Replace the whole ISO code mapping for this type");

        types.MapDelete("/{equipmentTypeId:guid}", DeleteAsync)
            .RequirePermission(MasterDataPermissions.EquipmentManage)
            .WithSummary("Soft-delete an equipment type");

        return master;
    }

    private static async Task<Ok<PagedResult<EquipmentTypeResponse>>> ListAsync(
        [AsParameters] ListQuery query, MasterDataDbContext db, CancellationToken ct, bool includeInactive = false)
    {
        var types = db.EquipmentTypes.AsNoTracking();
        if (!includeInactive) types = types.Where(t => t.IsActive);
        if (!string.IsNullOrWhiteSpace(query.Search))
            types = types.Where(t => t.TypeCode.Contains(query.Search) || t.DescriptionEn.Contains(query.Search));

        return TypedResults.Ok(await types
            .OrderBy(t => t.SortOrder).ThenBy(t => t.TypeCode)
            .Select(ToResponse)
            .ToPagedAsync(query.Page, query.PageSize, ct));
    }

    private static async Task<Results<Ok<EquipmentTypeDetailResponse>, NotFound>> GetAsync(
        Guid equipmentTypeId, MasterDataDbContext db, CancellationToken ct)
    {
        var type = await db.EquipmentTypes.AsNoTracking()
            .Where(t => t.EquipmentTypeId == equipmentTypeId)
            .Select(ToResponse)
            .SingleOrDefaultAsync(ct);
        if (type is null) return TypedResults.NotFound();

        return TypedResults.Ok(new EquipmentTypeDetailResponse(type, await IsoCodesOfAsync(db, equipmentTypeId, ct)));
    }

    /// <summary>
    /// Three answers, and the difference matters at the gate:
    ///   known to us          -> the local type, use it
    ///   known to ISO only    -> a real code this tenant has not mapped; ask an admin, do not guess
    ///   unknown entirely     -> the partner sent rubbish, reject the message
    /// </summary>
    private static async Task<Ok<IsoResolutionResponse>> ResolveAsync(
        string isoCode, MasterDataDbContext db, CancellationToken ct)
    {
        var code = isoCode.Trim().ToUpperInvariant();

        var mapped = await db.EquipmentTypeIsoCodes.AsNoTracking()
            .Where(m => m.IsoCode == code)
            .Join(db.EquipmentTypes, m => m.EquipmentTypeId, t => t.EquipmentTypeId,
                  (m, t) => new { t.EquipmentTypeId, t.TypeCode, t.DescriptionEn })
            .SingleOrDefaultAsync(ct);

        var standard = await db.IsoContainerCodes.AsNoTracking()
            .Where(i => i.IsoCode == code)
            .Select(i => new { i.SupersededBy, i.DescriptionEn })
            .SingleOrDefaultAsync(ct);

        return TypedResults.Ok(new IsoResolutionResponse(
            code,
            KnownToStandard: standard is not null,
            mapped?.EquipmentTypeId,
            mapped?.TypeCode,
            mapped?.DescriptionEn ?? standard?.DescriptionEn,
            standard?.SupersededBy,
            Note: mapped is not null ? null
                : standard is not null ? "Valid ISO 6346 code, not mapped to a local equipment type for this tenant."
                : "Not an ISO 6346 size-type code known to the platform."));
    }

    private static async Task<Results<CreatedAtRoute<EquipmentTypeDetailResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateEquipmentTypeRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        if (await ValidateShapeAsync(db, request.IsoGroupCode, request.TareWeightKg, request.MaxGrossKg, ct) is { } problem)
            return problem;
        if (await db.EquipmentTypes.AnyAsync(t => t.TypeCode == request.TypeCode, ct))
            return MasterDataSupport.Conflict($"Equipment type '{request.TypeCode}' already exists.");

        var type = new EquipmentType
        {
            TenantId = caller.TenantId(),
            TypeCode = request.TypeCode,
            DescriptionEn = request.DescriptionEn,
            DescriptionLocal = request.DescriptionLocal,
            LengthFt = request.LengthFt,
            HeightClass = request.HeightClass,
            IsoGroupCode = request.IsoGroupCode,
            Teu = request.Teu,
            IsReefer = request.IsReefer,
            IsOog = request.IsOog,
            IsTank = request.IsTank,
            TareWeightKg = request.TareWeightKg,
            MaxPayloadKg = request.MaxPayloadKg,
            MaxGrossKg = request.MaxGrossKg,
            DisplayColorHex = request.DisplayColorHex,
            SortOrder = request.SortOrder,
            IsActive = true,
        };

        db.EquipmentTypes.Add(type);
        await db.SaveChangesAsync(ct);

        var response = new EquipmentTypeDetailResponse(Map(type), []);
        return TypedResults.CreatedAtRoute(response, "GetEquipmentType", new { equipmentTypeId = type.EquipmentTypeId });
    }

    private static async Task<Results<Ok<EquipmentTypeResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid equipmentTypeId, UpdateEquipmentTypeRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var type = await db.EquipmentTypes.SingleOrDefaultAsync(t => t.EquipmentTypeId == equipmentTypeId, ct);
        if (type is null) return TypedResults.NotFound();
        if (!db.TrySetExpectedVersion(type, request.RowVersion))
            return MasterDataSupport.InvalidReference("rowVersion", "Send the rowVersion you received when reading the record.");
        if (await ValidateShapeAsync(db, request.IsoGroupCode, request.TareWeightKg, request.MaxGrossKg, ct) is { } problem)
            return problem;

        type.DescriptionEn = request.DescriptionEn;
        type.DescriptionLocal = request.DescriptionLocal;
        type.LengthFt = request.LengthFt;
        type.HeightClass = request.HeightClass;
        type.IsoGroupCode = request.IsoGroupCode;
        type.Teu = request.Teu;
        type.IsReefer = request.IsReefer;
        type.IsOog = request.IsOog;
        type.IsTank = request.IsTank;
        type.TareWeightKg = request.TareWeightKg;
        type.MaxPayloadKg = request.MaxPayloadKg;
        type.MaxGrossKg = request.MaxGrossKg;
        type.DisplayColorHex = request.DisplayColorHex;
        type.SortOrder = request.SortOrder;
        type.IsActive = request.IsActive;

        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(Map(type));
    }

    /// <summary>
    /// Replace, not patch. The two unique indexes make a partial edit a minefield:
    /// uq_equipment_type_iso__iso means an ISO code belongs to exactly one local
    /// type per tenant, and uq_equipment_type_iso__outbound means exactly one
    /// default per type. Sending the whole intended set lets both be checked once,
    /// up front, instead of discovering the clash halfway through.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<IsoMappingResponse>>, NotFound, ValidationProblem, ProblemHttpResult>> ReplaceIsoCodesAsync(
        Guid equipmentTypeId, ReplaceIsoMappingRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        if (!await db.EquipmentTypes.AnyAsync(t => t.EquipmentTypeId == equipmentTypeId, ct))
            return TypedResults.NotFound();

        var wanted = request.IsoCodes
            .Select(i => new IsoMappingItem(i.IsoCode.Trim().ToUpperInvariant(), i.IsDefaultOutbound))
            .ToList();

        var duplicates = wanted.GroupBy(i => i.IsoCode).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            return MasterDataSupport.InvalidReference("isoCodes", $"Repeated ISO code(s): {string.Join(", ", duplicates)}.");

        if (wanted.Count(i => i.IsDefaultOutbound) != 1)
            return MasterDataSupport.InvalidReference("isoCodes",
                "Exactly one ISO code must be the default outbound code — it is what gets written back on EDI.");

        var codes = wanted.Select(i => i.IsoCode).ToList();
        var known = await db.IsoContainerCodes.Where(i => codes.Contains(i.IsoCode)).Select(i => i.IsoCode).ToListAsync(ct);
        var unknown = codes.Except(known).ToList();
        if (unknown.Count > 0)
            return MasterDataSupport.InvalidReference("isoCodes",
                $"Not ISO 6346 size-type codes known to the platform: {string.Join(", ", unknown)}.");

        var takenByOthers = await db.EquipmentTypeIsoCodes
            .Where(m => codes.Contains(m.IsoCode) && m.EquipmentTypeId != equipmentTypeId)
            .Join(db.EquipmentTypes, m => m.EquipmentTypeId, t => t.EquipmentTypeId, (m, t) => new { m.IsoCode, t.TypeCode })
            .ToListAsync(ct);
        if (takenByOthers.Count > 0)
            return MasterDataSupport.Conflict(
                "Some ISO codes are already mapped to another equipment type.",
                string.Join(", ", takenByOthers.Select(x => $"{x.IsoCode} -> {x.TypeCode}")));

        var existing = await db.EquipmentTypeIsoCodes.Where(m => m.EquipmentTypeId == equipmentTypeId).ToListAsync(ct);
        var keep = existing.Where(m => codes.Contains(m.IsoCode)).ToList();

        // Soft-delete the dropped rows and FLUSH before adding. deleted_at is what
        // takes them out of the filtered unique index, and SQL Server enforces the
        // index per statement — insert the replacements in the same SaveChanges and
        // the old row is still live when the new one lands.
        foreach (var gone in existing.Except(keep)) db.EquipmentTypeIsoCodes.Remove(gone);
        if (existing.Count != keep.Count) await db.SaveChangesAsync(ct);

        foreach (var item in wanted)
        {
            var row = keep.SingleOrDefault(m => m.IsoCode == item.IsoCode);
            if (row is null)
            {
                db.EquipmentTypeIsoCodes.Add(new EquipmentTypeIsoCode
                {
                    TenantId = caller.TenantId(),
                    EquipmentTypeId = equipmentTypeId,
                    IsoCode = item.IsoCode,
                    IsDefaultOutbound = item.IsDefaultOutbound,
                });
            }
            else
            {
                row.IsDefaultOutbound = item.IsDefaultOutbound;
            }
        }

        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(await IsoCodesOfAsync(db, equipmentTypeId, ct));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        Guid equipmentTypeId, MasterDataDbContext db, CancellationToken ct)
    {
        var type = await db.EquipmentTypes.SingleOrDefaultAsync(t => t.EquipmentTypeId == equipmentTypeId, ct);
        if (type is null) return TypedResults.NotFound();

        // No FKs to stop this, so the references have to be checked here. A container
        // whose type vanished is a box nobody can price, plan or gate out.
        if (await db.Containers.AnyAsync(c => c.EquipmentTypeId == equipmentTypeId, ct))
            return MasterDataSupport.Conflict(
                "Equipment type is still used by containers in the registry.",
                "Re-type those containers first, or set isActive = false to stop it being chosen on new work.");

        if (await db.CodeMappings.AnyAsync(m => m.MappingType == "EQUIPMENT_TYPE" && m.InternalCode == type.TypeCode, ct))
            return MasterDataSupport.Conflict(
                "Partner code mappings still resolve to this equipment type.",
                "Remove or repoint those mappings first, or inbound EDI will resolve to a deleted type.");

        foreach (var mapping in await db.EquipmentTypeIsoCodes.Where(m => m.EquipmentTypeId == equipmentTypeId).ToListAsync(ct))
            db.EquipmentTypeIsoCodes.Remove(mapping);

        db.EquipmentTypes.Remove(type);   // AuditStampInterceptor turns this into UPDATE deleted_at
        await db.SaveChangesAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<ValidationProblem?> ValidateShapeAsync(
        MasterDataDbContext db, string isoGroupCode, decimal? tare, decimal? gross, CancellationToken ct)
    {
        if (!await db.IsoTypeGroups.AnyAsync(g => g.GroupCode == isoGroupCode, ct))
            return MasterDataSupport.InvalidReference("isoGroupCode", $"Unknown ISO type group '{isoGroupCode}'.");

        // Mirrors ck_equipment_type__weights. Checked here so the caller gets a
        // field-level 400 instead of a raw constraint violation.
        if (tare is not null && gross is not null && gross <= tare)
            return MasterDataSupport.InvalidReference("maxGrossKg", "Max gross weight must be greater than the tare weight.");

        return null;
    }

    /// <summary>
    /// The ORDER BY sits on the entity, not on the projected record. EF Core cannot
    /// translate an ordering whose key is a property of a freshly constructed object
    /// — it gives up on the whole query and throws at runtime, not at compile time.
    /// </summary>
    private static async Task<IReadOnlyList<IsoMappingResponse>> IsoCodesOfAsync(
        MasterDataDbContext db, Guid equipmentTypeId, CancellationToken ct) =>
        await (
            from m in db.EquipmentTypeIsoCodes.AsNoTracking().Where(m => m.EquipmentTypeId == equipmentTypeId)
            join i in db.IsoContainerCodes on m.IsoCode equals i.IsoCode into standard
            from i in standard.DefaultIfEmpty()
            orderby m.IsDefaultOutbound descending, m.IsoCode
            select new IsoMappingResponse(m.IsoCode, m.IsDefaultOutbound, i == null ? null : i.DescriptionEn)
        ).ToListAsync(ct);

    private static readonly Expression<Func<EquipmentType, EquipmentTypeResponse>> ToResponse = t => new EquipmentTypeResponse(
        t.EquipmentTypeId, t.TypeCode, t.DescriptionEn, t.DescriptionLocal, t.LengthFt, t.HeightClass, t.IsoGroupCode,
        t.Teu, t.IsReefer, t.IsOog, t.IsTank, t.TareWeightKg, t.MaxPayloadKg, t.MaxGrossKg, t.DisplayColorHex,
        t.SortOrder, t.IsActive, t.CreatedAt, t.UpdatedAt, Convert.ToBase64String(t.RowVersion));

    private static readonly Func<EquipmentType, EquipmentTypeResponse> Map = ToResponse.Compile();
}
