using System.ComponentModel.DataAnnotations;
using Gecko.Data;
using Gecko.MasterData.Infrastructure.Persistence;
using Gecko.MasterData.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using static Gecko.MasterData.Endpoints.Logistics.LogisticsSupport;

namespace Gecko.MasterData.Endpoints.Logistics;

public sealed record CommodityResponse(
    Guid CommodityId, string CommodityCode, string? HsCode, string DescriptionEn, string? DescriptionLocal,
    bool IsDangerous, string? ImdgClassCode, string? UnNumber, string? PackingGroup,
    bool IsTemperatureControlled, decimal? DefaultMinTempC, decimal? DefaultMaxTempC, bool IsActive, string RowVersion);

public sealed record SaveCommodityRequest(
    [property: Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,19}$", ErrorMessage = "Letters, digits, space, '.', '_' and '-', up to 20 — often the HS code.")] string CommodityCode,
    [property: Required, MaxLength(255)] string DescriptionEn,
    [property: MaxLength(255)] string? DescriptionLocal = null,
    [property: RegularExpression("^([0-9]{6}|[0-9]{8}|[0-9]{10})$", ErrorMessage = "An HS code is 6, 8 or 10 digits.")] string? HsCode = null,
    bool IsDangerous = false,
    [property: MaxLength(4)] string? ImdgClassCode = null,
    [property: RegularExpression("^[0-9]{4}$", ErrorMessage = "A UN number is 4 digits.")] string? UnNumber = null,
    [property: AllowedValues(null, "I", "II", "III")] string? PackingGroup = null,
    bool IsTemperatureControlled = false,
    [property: Range(-100, 100)] decimal? DefaultMinTempC = null,
    [property: Range(-100, 100)] decimal? DefaultMaxTempC = null,
    bool IsActive = true,
    string? RowVersion = null);

/// <summary>
/// Commodities (logistics.commodity) — what a booking carries; the DG and reefer
/// facts the gate checks. KORAKIT starts with none. The dangerous-goods fields
/// only mean something on a dangerous commodity, so they are refused on a
/// safe one (ck_commodity__dg) rather than silently kept.
/// </summary>
internal static class CommodityEndpoints
{
    public static RouteGroupBuilder MapCommodityEndpoints(this RouteGroupBuilder master)
    {
        var commodities = master.MapGroup("/commodities").WithTags("Master data — commodities");
        commodities.MapGet("/", ListAsync).RequirePermission(MasterDataPermissions.LogisticsView).WithSummary("Search commodities by code, HS code or description");
        commodities.MapGet("/{commodityCode}", GetAsync).RequirePermission(MasterDataPermissions.LogisticsView).WithName("GetCommodity").WithSummary("Get one commodity");
        commodities.MapPost("/", CreateAsync).RequirePermission(MasterDataPermissions.LogisticsManage).Validate<SaveCommodityRequest>().WithSummary("Add a commodity");
        commodities.MapPut("/{commodityCode}", UpdateAsync).RequirePermission(MasterDataPermissions.LogisticsManage).Validate<SaveCommodityRequest>().WithSummary("Update a commodity (optimistic concurrency on rowVersion)");
        commodities.MapDelete("/{commodityCode}", DeleteAsync).RequirePermission(MasterDataPermissions.LogisticsManage).WithSummary("Soft-delete a commodity (?rowVersion=)");
        return master;
    }

    private static CommodityResponse Map(Commodity c) => new(
        c.CommodityId, c.CommodityCode, c.HsCode, c.DescriptionEn, c.DescriptionLocal, c.IsDangerous, c.ImdgClassCode, c.UnNumber,
        c.PackingGroup, c.IsTemperatureControlled, c.DefaultMinTempC, c.DefaultMaxTempC, c.IsActive, Convert.ToBase64String(c.RowVersion));

    private static async Task<Ok<PagedResult<CommodityResponse>>> ListAsync(
        MasterDataDbContext db, CancellationToken ct,
        string? search = null, bool? isDangerous = null, bool includeInactive = false, int? page = 1, int? pageSize = 50)
    {
        var rows = db.Commodities.AsNoTracking();
        if (!includeInactive) rows = rows.Where(c => c.IsActive);
        if (isDangerous is { } dg) rows = rows.Where(c => c.IsDangerous == dg);
        if (Clean(search) is { } s)
            rows = rows.Where(c => c.CommodityCode.Contains(s) || (c.HsCode != null && c.HsCode.StartsWith(s))
                || c.DescriptionEn.Contains(s) || (c.DescriptionLocal != null && c.DescriptionLocal.Contains(s))
                || (c.UnNumber != null && c.UnNumber == s));
        var paged = await rows.OrderBy(c => c.CommodityCode).ToPagedAsync(page, pageSize, ct);
        return TypedResults.Ok(new PagedResult<CommodityResponse>(paged.Items.Select(Map).ToList(), paged.Page, paged.PageSize, paged.TotalCount));
    }

    private static async Task<Results<Ok<CommodityResponse>, NotFound>> GetAsync(string commodityCode, MasterDataDbContext db, CancellationToken ct) =>
        await db.Commodities.AsNoTracking().SingleOrDefaultAsync(c => c.CommodityCode == commodityCode.FromRouteCode(), ct) is { } c
            ? TypedResults.Ok(Map(c))
            : TypedResults.NotFound();

    private static async Task<Results<CreatedAtRoute<CommodityResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SaveCommodityRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var code = request.CommodityCode.Trim().ToUpperInvariant();
        if (await ValidateAsync(db, request, ct) is { } invalid) return invalid;
        if (await db.Commodities.AnyAsync(c => c.CommodityCode == code, ct))
            return MasterDataSupport.Conflict($"Commodity '{code}' already exists.");

        var commodity = new Commodity { TenantId = caller.TenantId(), CommodityCode = code };
        Apply(commodity, request);
        db.Commodities.Add(commodity);
        await db.SaveChangesAsync(ct);
        return TypedResults.CreatedAtRoute(Map(commodity), "GetCommodity", new { commodityCode = code });
    }

    private static async Task<Results<Ok<CommodityResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        string commodityCode, SaveCommodityRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var commodity = await db.Commodities.SingleOrDefaultAsync(c => c.CommodityCode == commodityCode.FromRouteCode(), ct);
        if (commodity is null) return TypedResults.NotFound();
        if (db.ExpectVersion(commodity, request.RowVersion) is { } missing) return missing;
        if (await ValidateAsync(db, request, ct) is { } invalid) return invalid;

        Apply(commodity, request);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(Map(commodity));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> DeleteAsync(
        string commodityCode, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var commodity = await db.Commodities.SingleOrDefaultAsync(c => c.CommodityCode == commodityCode.FromRouteCode(), ct);
        if (commodity is null) return TypedResults.NotFound();
        if (db.ExpectVersion(commodity, rowVersion) is { } missing) return missing;
        db.Commodities.Remove(commodity);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static async Task<ValidationProblem?> ValidateAsync(MasterDataDbContext db, SaveCommodityRequest r, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var imdg = Upper(r.ImdgClassCode);
        if (!r.IsDangerous)
        {
            const string notDg = "Only a dangerous commodity has this. Tick Dangerous, or leave it empty.";
            if (imdg is not null) errors["imdgClassCode"] = [notDg];
            if (Clean(r.UnNumber) is not null) errors["unNumber"] = [notDg];
            if (Clean(r.PackingGroup) is not null) errors["packingGroup"] = [notDg];
        }
        else
        {
            if (imdg is null) errors["imdgClassCode"] = ["A dangerous commodity needs its IMDG class."];
            else if (!await db.ImdgClasses.AsNoTracking().AnyAsync(c => c.ClassCode == imdg && c.IsActive, ct))
                errors["imdgClassCode"] = [$"Unknown IMDG class '{imdg}'."];
        }
        if (r.DefaultMinTempC is { } min && r.DefaultMaxTempC is { } max && min > max)
            errors["defaultMaxTempC"] = ["The highest temperature cannot be below the lowest."];
        if (!r.IsTemperatureControlled && (r.DefaultMinTempC is not null || r.DefaultMaxTempC is not null))
            errors[r.DefaultMinTempC is not null ? "defaultMinTempC" : "defaultMaxTempC"] = ["Only a temperature-controlled commodity has a set range. Tick Reefer, or leave it empty."];
        return Problem(errors);
    }

    private static void Apply(Commodity c, SaveCommodityRequest r)
    {
        c.HsCode = Clean(r.HsCode);
        c.DescriptionEn = r.DescriptionEn.Trim();
        c.DescriptionLocal = Clean(r.DescriptionLocal);
        c.IsDangerous = r.IsDangerous;
        c.ImdgClassCode = Upper(r.ImdgClassCode);
        c.UnNumber = Clean(r.UnNumber);
        c.PackingGroup = Clean(r.PackingGroup);
        c.IsTemperatureControlled = r.IsTemperatureControlled;
        c.DefaultMinTempC = r.DefaultMinTempC;
        c.DefaultMaxTempC = r.DefaultMaxTempC;
        c.IsActive = r.IsActive;
    }
}
