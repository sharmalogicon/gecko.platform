using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Gecko.Data;
using Gecko.MasterData.Infrastructure.Persistence;
using Gecko.MasterData.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Endpoints.Equipment;

public sealed record DamageCodeResponse(Guid Id, string Code, string DescriptionEn, string? DescriptionLocal, string CodeStandard, byte Severity, bool MakesUnserviceable, bool IsActive, string RowVersion);
public sealed record RepairCodeResponse(Guid Id, string Code, string DescriptionEn, string? DescriptionLocal, string CodeStandard, string? RepairMode, string? RepairGroup, string? DefaultUomCode, bool IsActive, string RowVersion);
public sealed record DamageLocationResponse(Guid Id, string Code, string DescriptionEn, string? DescriptionLocal, string CodeStandard, string? ContainerFace, bool IsActive, string RowVersion);
public sealed record ComponentResponse(Guid Id, string Code, string DescriptionEn, string? DescriptionLocal, string CodeStandard, string? ComponentGroup, string? BaseUomCode, bool IsOwnPart, bool IsActive, string RowVersion);

public sealed record SaveDamageCodeRequest(
    [property: Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9_-]{0,9}$")] string Code,
    [property: Required, MaxLength(200)] string DescriptionEn,
    [property: AllowedValues("CEDEX", "IICL", "LOCAL")] string CodeStandard = "LOCAL",
    [property: Range(1, 9)] byte Severity = 5,
    bool MakesUnserviceable = false,
    [property: MaxLength(200)] string? DescriptionLocal = null,
    bool IsActive = true, string? RowVersion = null);

public sealed record SaveRepairCodeRequest(
    [property: Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9_-]{0,9}$")] string Code,
    [property: Required, MaxLength(200)] string DescriptionEn,
    [property: AllowedValues("CEDEX", "IICL", "LOCAL")] string CodeStandard = "LOCAL",
    [property: MaxLength(20)] string? RepairMode = null,
    [property: MaxLength(20)] string? RepairGroup = null,
    [property: MaxLength(10)] string? DefaultUomCode = null,
    [property: MaxLength(200)] string? DescriptionLocal = null,
    bool IsActive = true, string? RowVersion = null);

public sealed record SaveDamageLocationRequest(
    [property: Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9_-]{0,9}$")] string Code,
    [property: Required, MaxLength(200)] string DescriptionEn,
    [property: AllowedValues("CEDEX", "IICL", "LOCAL")] string CodeStandard = "LOCAL",
    [property: AllowedValues(null, "LEFT", "RIGHT", "FRONT", "DOOR", "ROOF", "FLOOR", "UNDER", "INTERIOR", "MACHINERY")] string? ContainerFace = null,
    [property: MaxLength(200)] string? DescriptionLocal = null,
    bool IsActive = true, string? RowVersion = null);

public sealed record SaveComponentRequest(
    [property: Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9_-]{0,19}$")] string Code,
    [property: Required, MaxLength(200)] string DescriptionEn,
    [property: AllowedValues("CEDEX", "IICL", "LOCAL")] string CodeStandard = "LOCAL",
    [property: MaxLength(20)] string? ComponentGroup = null,
    [property: MaxLength(10)] string? BaseUomCode = null,
    bool IsOwnPart = false,
    [property: MaxLength(200)] string? DescriptionLocal = null,
    bool IsActive = true, string? RowVersion = null);

/// <summary>
/// The gate survey's vocabulary (Tier 3): damage codes, repair codes, damage
/// locations and components — CEDEX / IICL / the tenant's own (LOCAL). A code is
/// unique per STANDARD (uq_*__code is tenant + standard + code), so rows are
/// addressed by id. A survey in gecko_tos records the code it used; deleting one
/// here stops it being offered, it does not rewrite a survey (decision A).
/// Read mdm.equipment.view, change mdm.equipment.manage.
/// </summary>
internal static class SurveyCodeEndpoints
{
    public static RouteGroupBuilder MapSurveyCodeEndpoints(this RouteGroupBuilder master)
    {
        Map(master, "/damage-codes", "damage code", ListDamage, CreateDamage, UpdateDamage, DeleteAsync<DamageCode>);
        Map(master, "/repair-codes", "repair code", ListRepair, CreateRepair, UpdateRepair, DeleteAsync<RepairCode>);
        Map(master, "/damage-locations", "damage location", ListLocation, CreateLocation, UpdateLocation, DeleteAsync<DamageLocation>);
        Map(master, "/components", "component", ListComponent, CreateComponent, UpdateComponent, DeleteAsync<Component>);
        return master;
    }

    private static void Map(RouteGroupBuilder master, string path, string noun, Delegate list, Delegate create, Delegate update, Delegate delete)
    {
        var g = master.MapGroup(path).WithTags("Master data — survey codes");
        g.MapGet("/", list).RequirePermission(MasterDataPermissions.EquipmentView).WithSummary($"List {noun}s");
        g.MapPost("/", create).RequirePermission(MasterDataPermissions.EquipmentManage).WithSummary($"Add a {noun}");
        g.MapPut("/{id:guid}", update).RequirePermission(MasterDataPermissions.EquipmentManage).WithSummary($"Update a {noun} (optimistic concurrency on rowVersion)");
        g.MapDelete("/{id:guid}", delete).RequirePermission(MasterDataPermissions.EquipmentManage).WithSummary($"Soft-delete a {noun} (?rowVersion=)");
    }

    // Validation runs explicitly (not .Validate<T>() per route) because Map takes one delegate per verb.
    private static ValidationProblem? Annotations<T>(T request) where T : class
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        if (results.Count == 0) return null;
        return TypedResults.ValidationProblem(results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty("").Select(m => (Field: m.Length == 0 ? "" : char.ToLowerInvariant(m[0]) + m[1..], r.ErrorMessage ?? "Invalid value.")))
            .GroupBy(e => e.Field, e => e.Item2).ToDictionary(g => g.Key, g => g.ToArray()));
    }

    private static string Up(string s) => s.Trim().ToUpperInvariant();
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string V(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    private static async Task<string?> CodeListAsync(MasterDataDbContext db, string category, string? code, CancellationToken ct) =>
        Clean(code)?.ToUpperInvariant() is { } c && !await db.VwCodeLists.AnyAsync(v => v.CategoryCode == category && v.Code == c && v.IsActive == true, ct)
            ? $"'{c}' is not a value of the {category} code list."
            : null;

    private static async Task<string?> UomAsync(MasterDataDbContext db, string? code, CancellationToken ct) =>
        Clean(code)?.ToUpperInvariant() is { } c && !await db.Uoms.AnyAsync(u => u.UomCode == c && u.IsActive, ct) ? $"Unknown unit '{c}'." : null;

    private static ValidationProblem? Fields(params (string Field, string? Message)[] checks)
    {
        var errors = checks.Where(c => c.Message is not null).ToDictionary(c => c.Field, c => new[] { c.Message! });
        return errors.Count == 0 ? null : TypedResults.ValidationProblem(errors);
    }

    private static ProblemHttpResult Duplicate(string standard, string code) =>
        MasterDataSupport.Conflict($"{standard} code '{code}' already exists.", "A code is unique within its standard (CEDEX, IICL or LOCAL).");

    /// <summary>Update / delete share this: find by id, apply the version.</summary>
    private static async Task<(T? Row, IResult? Problem)> FindAsync<T>(DbSet<T> set, Expression<Func<T, bool>> byId, MasterDataDbContext db, string? rowVersion, CancellationToken ct) where T : class
    {
        var row = await set.SingleOrDefaultAsync(byId, ct);
        if (row is null) return (null, TypedResults.NotFound());
        if (db.ExpectVersion(row, rowVersion) is { } missing) return (null, missing);
        return (row, null);
    }

    private static async Task<IResult> SaveAsync(MasterDataDbContext db, Func<object> map, CancellationToken ct) =>
        await db.SaveOrConflictAsync(ct) is { } conflict ? conflict : TypedResults.Ok(map());

    private static async Task<IResult> DeleteAsync<T>(Guid id, string? rowVersion, MasterDataDbContext db, CancellationToken ct) where T : class
    {
        var set = db.Set<T>();
        var row = await set.FindAsync([id], ct);
        if (row is null || (db.Entry(row).Property("DeletedAt").CurrentValue is not null)) return TypedResults.NotFound();
        if (db.ExpectVersion(row, rowVersion) is { } missing) return missing;
        set.Remove(row);
        return await db.SaveOrConflictAsync(ct) is { } conflict ? conflict : TypedResults.NoContent();
    }

    // ── damage codes ────────────────────────────────────────────────────────

    private static DamageCodeResponse Map(DamageCode d) => new(d.DamageCodeId, d.DamageCode1, d.DescriptionEn, d.DescriptionLocal, d.CodeStandard, d.Severity, d.MakesUnserviceable, d.IsActive, V(d.RowVersion));

    private static async Task<IResult> ListDamage(MasterDataDbContext db, CancellationToken ct, bool includeInactive = false) =>
        TypedResults.Ok((await db.DamageCodes.AsNoTracking().Where(d => includeInactive || d.IsActive).OrderBy(d => d.CodeStandard).ThenBy(d => d.DamageCode1).ToListAsync(ct)).Select(Map));

    private static async Task<IResult> CreateDamage(SaveDamageCodeRequest r, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        if (Annotations(r) is { } invalid) return invalid;
        var code = Up(r.Code);
        if (await db.DamageCodes.AnyAsync(d => d.CodeStandard == r.CodeStandard && d.DamageCode1 == code, ct)) return Duplicate(r.CodeStandard, code);
        var d = new DamageCode { TenantId = caller.TenantId(), DamageCode1 = code };
        ApplyDamage(d, r);
        db.DamageCodes.Add(d);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/master/damage-codes/{d.DamageCodeId}", Map(d));
    }

    private static async Task<IResult> UpdateDamage(Guid id, SaveDamageCodeRequest r, MasterDataDbContext db, CancellationToken ct)
    {
        if (Annotations(r) is { } invalid) return invalid;
        var (d, problem) = await FindAsync(db.DamageCodes, x => x.DamageCodeId == id, db, r.RowVersion, ct);
        if (problem is not null) return problem;
        var code = Up(r.Code);
        if (await db.DamageCodes.AnyAsync(x => x.CodeStandard == r.CodeStandard && x.DamageCode1 == code && x.DamageCodeId != id, ct)) return Duplicate(r.CodeStandard, code);
        d!.DamageCode1 = code;
        ApplyDamage(d, r);
        return await SaveAsync(db, () => Map(d), ct);
    }

    private static void ApplyDamage(DamageCode d, SaveDamageCodeRequest r)
    {
        d.DescriptionEn = r.DescriptionEn.Trim(); d.DescriptionLocal = Clean(r.DescriptionLocal); d.CodeStandard = r.CodeStandard;
        d.Severity = r.Severity; d.MakesUnserviceable = r.MakesUnserviceable; d.IsActive = r.IsActive;
    }

    // ── repair codes ────────────────────────────────────────────────────────

    private static RepairCodeResponse Map(RepairCode d) => new(d.RepairCodeId, d.RepairCode1, d.DescriptionEn, d.DescriptionLocal, d.CodeStandard, d.RepairMode, d.RepairGroup, d.DefaultUomCode, d.IsActive, V(d.RowVersion));

    private static async Task<IResult> ListRepair(MasterDataDbContext db, CancellationToken ct, bool includeInactive = false) =>
        TypedResults.Ok((await db.RepairCodes.AsNoTracking().Where(d => includeInactive || d.IsActive).OrderBy(d => d.CodeStandard).ThenBy(d => d.RepairCode1).ToListAsync(ct)).Select(Map));

    private static async Task<ValidationProblem?> CheckRepair(MasterDataDbContext db, SaveRepairCodeRequest r, CancellationToken ct) =>
        Annotations(r) ?? Fields(
            ("repairMode", await CodeListAsync(db, "REPAIR_MODE", r.RepairMode, ct)),
            ("repairGroup", await CodeListAsync(db, "REPAIR_GROUP", r.RepairGroup, ct)),
            ("defaultUomCode", await UomAsync(db, r.DefaultUomCode, ct)));

    private static async Task<IResult> CreateRepair(SaveRepairCodeRequest r, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        if (await CheckRepair(db, r, ct) is { } invalid) return invalid;
        var code = Up(r.Code);
        if (await db.RepairCodes.AnyAsync(d => d.CodeStandard == r.CodeStandard && d.RepairCode1 == code, ct)) return Duplicate(r.CodeStandard, code);
        var d = new RepairCode { TenantId = caller.TenantId(), RepairCode1 = code };
        ApplyRepair(d, r);
        db.RepairCodes.Add(d);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/master/repair-codes/{d.RepairCodeId}", Map(d));
    }

    private static async Task<IResult> UpdateRepair(Guid id, SaveRepairCodeRequest r, MasterDataDbContext db, CancellationToken ct)
    {
        if (Annotations(r) is { } basic) return basic;
        var (d, problem) = await FindAsync(db.RepairCodes, x => x.RepairCodeId == id, db, r.RowVersion, ct);
        if (problem is not null) return problem;
        if (await CheckRepair(db, r, ct) is { } invalid) return invalid;
        var code = Up(r.Code);
        if (await db.RepairCodes.AnyAsync(x => x.CodeStandard == r.CodeStandard && x.RepairCode1 == code && x.RepairCodeId != id, ct)) return Duplicate(r.CodeStandard, code);
        d!.RepairCode1 = code;
        ApplyRepair(d, r);
        return await SaveAsync(db, () => Map(d), ct);
    }

    private static void ApplyRepair(RepairCode d, SaveRepairCodeRequest r)
    {
        d.DescriptionEn = r.DescriptionEn.Trim(); d.DescriptionLocal = Clean(r.DescriptionLocal); d.CodeStandard = r.CodeStandard;
        d.RepairMode = Clean(r.RepairMode)?.ToUpperInvariant(); d.RepairGroup = Clean(r.RepairGroup)?.ToUpperInvariant();
        d.DefaultUomCode = Clean(r.DefaultUomCode)?.ToUpperInvariant(); d.IsActive = r.IsActive;
    }

    // ── damage locations ────────────────────────────────────────────────────

    private static DamageLocationResponse Map(DamageLocation d) => new(d.DamageLocationId, d.LocationCode, d.DescriptionEn, d.DescriptionLocal, d.CodeStandard, d.ContainerFace, d.IsActive, V(d.RowVersion));

    private static async Task<IResult> ListLocation(MasterDataDbContext db, CancellationToken ct, bool includeInactive = false) =>
        TypedResults.Ok((await db.DamageLocations.AsNoTracking().Where(d => includeInactive || d.IsActive).OrderBy(d => d.CodeStandard).ThenBy(d => d.LocationCode).ToListAsync(ct)).Select(Map));

    private static async Task<IResult> CreateLocation(SaveDamageLocationRequest r, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        if (Annotations(r) is { } invalid) return invalid;
        var code = Up(r.Code);
        if (await db.DamageLocations.AnyAsync(d => d.CodeStandard == r.CodeStandard && d.LocationCode == code, ct)) return Duplicate(r.CodeStandard, code);
        var d = new DamageLocation { TenantId = caller.TenantId(), LocationCode = code };
        ApplyLocation(d, r);
        db.DamageLocations.Add(d);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/master/damage-locations/{d.DamageLocationId}", Map(d));
    }

    private static async Task<IResult> UpdateLocation(Guid id, SaveDamageLocationRequest r, MasterDataDbContext db, CancellationToken ct)
    {
        if (Annotations(r) is { } invalid) return invalid;
        var (d, problem) = await FindAsync(db.DamageLocations, x => x.DamageLocationId == id, db, r.RowVersion, ct);
        if (problem is not null) return problem;
        var code = Up(r.Code);
        if (await db.DamageLocations.AnyAsync(x => x.CodeStandard == r.CodeStandard && x.LocationCode == code && x.DamageLocationId != id, ct)) return Duplicate(r.CodeStandard, code);
        d!.LocationCode = code;
        ApplyLocation(d, r);
        return await SaveAsync(db, () => Map(d), ct);
    }

    private static void ApplyLocation(DamageLocation d, SaveDamageLocationRequest r)
    {
        d.DescriptionEn = r.DescriptionEn.Trim(); d.DescriptionLocal = Clean(r.DescriptionLocal); d.CodeStandard = r.CodeStandard;
        d.ContainerFace = r.ContainerFace; d.IsActive = r.IsActive;
    }

    // ── components ──────────────────────────────────────────────────────────

    private static ComponentResponse Map(Component d) => new(d.ComponentId, d.ComponentCode, d.DescriptionEn, d.DescriptionLocal, d.CodeStandard, d.ComponentGroup, d.BaseUomCode, d.IsOwnPart, d.IsActive, V(d.RowVersion));

    private static async Task<IResult> ListComponent(MasterDataDbContext db, CancellationToken ct, bool includeInactive = false) =>
        TypedResults.Ok((await db.Components.AsNoTracking().Where(d => includeInactive || d.IsActive).OrderBy(d => d.CodeStandard).ThenBy(d => d.ComponentCode).ToListAsync(ct)).Select(Map));

    private static async Task<ValidationProblem?> CheckComponent(MasterDataDbContext db, SaveComponentRequest r, CancellationToken ct) =>
        Annotations(r) ?? Fields(
            ("componentGroup", await CodeListAsync(db, "COMPONENT_GROUP", r.ComponentGroup, ct)),
            ("baseUomCode", await UomAsync(db, r.BaseUomCode, ct)));

    private static async Task<IResult> CreateComponent(SaveComponentRequest r, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        if (await CheckComponent(db, r, ct) is { } invalid) return invalid;
        var code = Up(r.Code);
        if (await db.Components.AnyAsync(d => d.CodeStandard == r.CodeStandard && d.ComponentCode == code, ct)) return Duplicate(r.CodeStandard, code);
        var d = new Component { TenantId = caller.TenantId(), ComponentCode = code };
        ApplyComponent(d, r);
        db.Components.Add(d);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/master/components/{d.ComponentId}", Map(d));
    }

    private static async Task<IResult> UpdateComponent(Guid id, SaveComponentRequest r, MasterDataDbContext db, CancellationToken ct)
    {
        if (Annotations(r) is { } basic) return basic;
        var (d, problem) = await FindAsync(db.Components, x => x.ComponentId == id, db, r.RowVersion, ct);
        if (problem is not null) return problem;
        if (await CheckComponent(db, r, ct) is { } invalid) return invalid;
        var code = Up(r.Code);
        if (await db.Components.AnyAsync(x => x.CodeStandard == r.CodeStandard && x.ComponentCode == code && x.ComponentId != id, ct)) return Duplicate(r.CodeStandard, code);
        d!.ComponentCode = code;
        ApplyComponent(d, r);
        return await SaveAsync(db, () => Map(d), ct);
    }

    private static void ApplyComponent(Component d, SaveComponentRequest r)
    {
        d.DescriptionEn = r.DescriptionEn.Trim(); d.DescriptionLocal = Clean(r.DescriptionLocal); d.CodeStandard = r.CodeStandard;
        d.ComponentGroup = Clean(r.ComponentGroup)?.ToUpperInvariant(); d.BaseUomCode = Clean(r.BaseUomCode)?.ToUpperInvariant();
        d.IsOwnPart = r.IsOwnPart; d.IsActive = r.IsActive;
    }
}
