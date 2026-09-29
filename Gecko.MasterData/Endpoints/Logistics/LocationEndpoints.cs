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

public sealed record LocationResponse(
    Guid LocationId, string LocationCode, string LocationNameEn, string? LocationNameLocal, string LocationType,
    string? AreaCode, string? PartyCode, string? PartyName, string? Address1, string? Address2, string? City, string? State,
    string? Postcode, string? CountryCode, decimal? Latitude, decimal? Longitude, bool IsActive, string RowVersion);

public sealed record SaveLocationRequest(
    [property: Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9 ._/-]{0,29}$", ErrorMessage = "Letters, digits, space, '.', '_', '/' and '-', up to 30.")] string LocationCode,
    [property: Required, MaxLength(255)] string LocationNameEn,
    // ck_location__type as 17_tos_prerequisites.sql left it (TERMINAL added for vessel calls).
    [property: Required, AllowedValues("FACTORY", "WAREHOUSE", "INDUSTRIAL_ESTATE", "CFS", "DEPOT", "PORT_AREA", "TERMINAL", "CUSTOMS", "OTHER")] string LocationType,
    [property: MaxLength(255)] string? LocationNameLocal = null,
    [property: MaxLength(20)] string? AreaCode = null,
    [property: MaxLength(60)] string? PartyCode = null,
    [property: MaxLength(255)] string? Address1 = null,
    [property: MaxLength(255)] string? Address2 = null,
    [property: MaxLength(100)] string? City = null,
    [property: MaxLength(100)] string? State = null,
    [property: MaxLength(25)] string? Postcode = null,
    [property: StringLength(2, MinimumLength = 2)] string? CountryCode = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    bool IsActive = true,
    string? RowVersion = null);

/// <summary>
/// Locations (logistics.location) — a customer's factory or warehouse, an
/// industrial estate, a terminal at the port: where a box is delivered or picked
/// up, and the terminal of a vessel call. The party is whose premises it is.
/// KORAKIT starts with none (its 1,754 legacy rows were SCT's, not loaded).
/// </summary>
internal static class LocationEndpoints
{
    public static RouteGroupBuilder MapLocationEndpoints(this RouteGroupBuilder master)
    {
        var locations = master.MapGroup("/locations").WithTags("Master data — locations");
        locations.MapGet("/", ListAsync).RequirePermission(MasterDataPermissions.LogisticsView).WithSummary("Search locations by code, name, city or area");
        locations.MapGet("/{locationCode}", GetAsync).RequirePermission(MasterDataPermissions.LogisticsView).WithName("GetLocation").WithSummary("Get one location");
        locations.MapPost("/", CreateAsync).RequirePermission(MasterDataPermissions.LogisticsManage).Validate<SaveLocationRequest>().WithSummary("Add a location");
        locations.MapPut("/{locationCode}", UpdateAsync).RequirePermission(MasterDataPermissions.LogisticsManage).Validate<SaveLocationRequest>().WithSummary("Update a location (optimistic concurrency on rowVersion)");
        locations.MapDelete("/{locationCode}", DeleteAsync).RequirePermission(MasterDataPermissions.LogisticsManage).WithSummary("Soft-delete a location (?rowVersion=)");
        return master;
    }

    private static IQueryable<LocationResponse> Rows(MasterDataDbContext db, IQueryable<Location> locations) =>
        from l in locations
        join p in db.Parties.AsNoTracking() on l.PartyId equals (Guid?)p.PartyId into owners
        from p in owners.DefaultIfEmpty()
        select new LocationResponse(
            l.LocationId, l.LocationCode, l.LocationNameEn, l.LocationNameLocal, l.LocationType, l.AreaCode,
            p == null ? null : p.PartyCode, p == null ? null : p.NameEn, l.Address1, l.Address2, l.City, l.State,
            l.Postcode, l.CountryCode, l.Latitude, l.Longitude, l.IsActive, Convert.ToBase64String(l.RowVersion));

    private static async Task<Ok<PagedResult<LocationResponse>>> ListAsync(
        MasterDataDbContext db, CancellationToken ct,
        string? search = null, string? locationType = null, bool includeInactive = false, int? page = 1, int? pageSize = 50)
    {
        var rows = db.Locations.AsNoTracking();
        if (!includeInactive) rows = rows.Where(l => l.IsActive);
        if (Upper(locationType) is { } type) rows = rows.Where(l => l.LocationType == type);
        if (Clean(search) is { } s)
            rows = rows.Where(l => l.LocationCode.Contains(s) || l.LocationNameEn.Contains(s)
                || (l.LocationNameLocal != null && l.LocationNameLocal.Contains(s))
                || (l.City != null && l.City.Contains(s)) || (l.AreaCode != null && l.AreaCode.Contains(s)));
        return TypedResults.Ok(await Rows(db, rows.OrderBy(l => l.LocationCode)).ToPagedAsync(page, pageSize, ct));
    }

    private static async Task<Results<Ok<LocationResponse>, NotFound>> GetAsync(string locationCode, MasterDataDbContext db, CancellationToken ct) =>
        await Rows(db, db.Locations.AsNoTracking().Where(l => l.LocationCode == locationCode.FromRouteCode())).SingleOrDefaultAsync(ct) is { } l
            ? TypedResults.Ok(l)
            : TypedResults.NotFound();

    private static async Task<Results<CreatedAtRoute<LocationResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SaveLocationRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var code = request.LocationCode.Trim().ToUpperInvariant();
        var (partyId, invalid) = await ValidateAsync(db, request, ct);
        if (invalid is not null) return invalid;
        if (await db.Locations.AnyAsync(l => l.LocationCode == code, ct))
            return MasterDataSupport.Conflict($"Location '{code}' already exists.");

        var location = new Location { TenantId = caller.TenantId(), LocationCode = code };
        Apply(location, request, partyId);
        db.Locations.Add(location);
        await db.SaveChangesAsync(ct);
        var saved = await Rows(db, db.Locations.AsNoTracking().Where(l => l.LocationId == location.LocationId)).SingleAsync(ct);
        return TypedResults.CreatedAtRoute(saved, "GetLocation", new { locationCode = code });
    }

    private static async Task<Results<Ok<LocationResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        string locationCode, SaveLocationRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var location = await db.Locations.SingleOrDefaultAsync(l => l.LocationCode == locationCode.FromRouteCode(), ct);
        if (location is null) return TypedResults.NotFound();
        if (db.ExpectVersion(location, request.RowVersion) is { } missing) return missing;
        var (partyId, invalid) = await ValidateAsync(db, request, ct);
        if (invalid is not null) return invalid;

        Apply(location, request, partyId);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(await Rows(db, db.Locations.AsNoTracking().Where(l => l.LocationId == location.LocationId)).SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> DeleteAsync(
        string locationCode, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var location = await db.Locations.SingleOrDefaultAsync(l => l.LocationCode == locationCode.FromRouteCode(), ct);
        if (location is null) return TypedResults.NotFound();
        if (db.ExpectVersion(location, rowVersion) is { } missing) return missing;
        db.Locations.Remove(location);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static async Task<(Guid? PartyId, ValidationProblem? Invalid)> ValidateAsync(MasterDataDbContext db, SaveLocationRequest r, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        await CountryAsync(db, "countryCode", Upper(r.CountryCode), errors, ct);
        Coordinates(r.Latitude, r.Longitude, errors);
        var partyId = await PartyAsync(db, "partyCode", Upper(r.PartyCode), lineOnly: false, errors, ct);
        return (partyId, Problem(errors));
    }

    private static void Apply(Location l, SaveLocationRequest r, Guid? partyId)
    {
        l.LocationNameEn = r.LocationNameEn.Trim();
        l.LocationNameLocal = Clean(r.LocationNameLocal);
        l.LocationType = r.LocationType;
        l.AreaCode = Upper(r.AreaCode);
        l.PartyId = partyId;
        l.Address1 = Clean(r.Address1);
        l.Address2 = Clean(r.Address2);
        l.City = Clean(r.City);
        l.State = Clean(r.State);
        l.Postcode = Clean(r.Postcode);
        l.CountryCode = Upper(r.CountryCode);
        l.Latitude = r.Latitude;
        l.Longitude = r.Longitude;
        l.IsActive = r.IsActive;
    }
}
