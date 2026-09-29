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

public sealed record PortResponse(
    Guid PortId, string PortCode, string? UnLocode, string PortNameEn, string? PortNameLocal, string PortType,
    string CountryCode, string TradeMode, decimal? Latitude, decimal? Longitude, string? Timezone, string? Postcode,
    string? EdiMappingCode, string? PaperlessCode, bool IsActive, string RowVersion);

public sealed record SavePortRequest(
    [property: Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9 ._/-]{0,19}$", ErrorMessage = "Letters, digits, space, '.', '_', '/' and '-', up to 20 — usually the UN/LOCODE.")] string PortCode,
    [property: Required, MaxLength(255)] string PortNameEn,
    [property: Required, AllowedValues("SEAPORT", "RIVER_PORT", "DRY_PORT", "ICD", "AIRPORT", "RAIL_TERMINAL", "BORDER", "OTHER")] string PortType,
    [property: Required, StringLength(2, MinimumLength = 2)] string CountryCode,
    [property: AllowedValues("DOMESTIC", "INTERNATIONAL", "BOTH")] string TradeMode = "INTERNATIONAL",
    [property: RegularExpression("^[A-Za-z]{2}[A-Za-z2-9]{3}$", ErrorMessage = "A UN/LOCODE is 5 characters: the country (TH) and 3 letters or digits 2–9 (LCH).")] string? UnLocode = null,
    [property: MaxLength(255)] string? PortNameLocal = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    [property: MaxLength(50)] string? Timezone = null,
    [property: MaxLength(25)] string? Postcode = null,
    [property: MaxLength(25)] string? EdiMappingCode = null,
    [property: MaxLength(10)] string? PaperlessCode = null,
    bool IsActive = true,
    string? RowVersion = null);

/// <summary>
/// Ports (logistics.port) — POL / POD / final destination on bookings, the port
/// of a vessel call. KORAKIT starts with none (0 of its 138,926 bookings carry a
/// port); the tenant adds the ones it needs. port_code is what the tenant types,
/// usually the UN/LOCODE.
///
/// Delete is refused while a party contact points at the port. Bookings and
/// vessel calls in gecko_tos keep the code they were made with (decision A).
/// </summary>
internal static class PortEndpoints
{
    public static RouteGroupBuilder MapPortEndpoints(this RouteGroupBuilder master)
    {
        var ports = master.MapGroup("/ports").WithTags("Master data — ports");
        ports.MapGet("/", ListAsync).RequirePermission(MasterDataPermissions.LogisticsView).WithSummary("Search ports by code, UN/LOCODE or name");
        ports.MapGet("/{portCode}", GetAsync).RequirePermission(MasterDataPermissions.LogisticsView).WithName("GetPort").WithSummary("Get one port");
        ports.MapPost("/", CreateAsync).RequirePermission(MasterDataPermissions.LogisticsManage).Validate<SavePortRequest>().WithSummary("Add a port");
        ports.MapPut("/{portCode}", UpdateAsync).RequirePermission(MasterDataPermissions.LogisticsManage).Validate<SavePortRequest>().WithSummary("Update a port (optimistic concurrency on rowVersion)");
        ports.MapDelete("/{portCode}", DeleteAsync).RequirePermission(MasterDataPermissions.LogisticsManage).WithSummary("Soft-delete a port (?rowVersion=)");
        return master;
    }

    private static PortResponse Map(Port p) => new(
        p.PortId, p.PortCode, p.UnLocode, p.PortNameEn, p.PortNameLocal, p.PortType, p.CountryCode, p.TradeMode,
        p.Latitude, p.Longitude, p.Timezone, p.Postcode, p.EdiMappingCode, p.PaperlessCode, p.IsActive, Convert.ToBase64String(p.RowVersion));

    private static async Task<Ok<PagedResult<PortResponse>>> ListAsync(
        MasterDataDbContext db, CancellationToken ct,
        string? search = null, string? countryCode = null, bool includeInactive = false, int? page = 1, int? pageSize = 50)
    {
        var ports = db.Ports.AsNoTracking();
        if (!includeInactive) ports = ports.Where(p => p.IsActive);
        if (Upper(countryCode) is { } country) ports = ports.Where(p => p.CountryCode == country);
        if (Clean(search) is { } s)
            ports = ports.Where(p => p.PortCode.Contains(s) || (p.UnLocode != null && p.UnLocode.Contains(s))
                || p.PortNameEn.Contains(s) || (p.PortNameLocal != null && p.PortNameLocal.Contains(s)));
        var paged = await ports.OrderBy(p => p.PortCode).ToPagedAsync(page, pageSize, ct);
        return TypedResults.Ok(new PagedResult<PortResponse>(paged.Items.Select(Map).ToList(), paged.Page, paged.PageSize, paged.TotalCount));
    }

    private static async Task<Results<Ok<PortResponse>, NotFound>> GetAsync(string portCode, MasterDataDbContext db, CancellationToken ct) =>
        await db.Ports.AsNoTracking().SingleOrDefaultAsync(p => p.PortCode == portCode.FromRouteCode(), ct) is { } port
            ? TypedResults.Ok(Map(port))
            : TypedResults.NotFound();

    private static async Task<Results<CreatedAtRoute<PortResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SavePortRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var code = request.PortCode.Trim().ToUpperInvariant();
        if (await ValidateAsync(db, request, null, ct) is { } invalid) return invalid;
        if (await db.Ports.AnyAsync(p => p.PortCode == code, ct))
            return MasterDataSupport.Conflict($"Port '{code}' already exists.");
        if (await LocodeHolderAsync(db, request, null, ct) is { } holder)
            return MasterDataSupport.Conflict("This UN/LOCODE is already a port.", $"{Upper(request.UnLocode)} is port {holder}.");

        var port = new Port { TenantId = caller.TenantId(), PortCode = code };
        Apply(port, request);
        db.Ports.Add(port);
        await db.SaveChangesAsync(ct);
        return TypedResults.CreatedAtRoute(Map(port), "GetPort", new { portCode = code });
    }

    private static async Task<Results<Ok<PortResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        string portCode, SavePortRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var port = await db.Ports.SingleOrDefaultAsync(p => p.PortCode == portCode.FromRouteCode(), ct);
        if (port is null) return TypedResults.NotFound();
        if (db.ExpectVersion(port, request.RowVersion) is { } missing) return missing;
        if (await ValidateAsync(db, request, port.PortId, ct) is { } invalid) return invalid;
        if (await LocodeHolderAsync(db, request, port.PortId, ct) is { } holder)
            return MasterDataSupport.Conflict("This UN/LOCODE is already a port.", $"{Upper(request.UnLocode)} is port {holder}.");

        Apply(port, request);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(Map(port));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> DeleteAsync(
        string portCode, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var port = await db.Ports.SingleOrDefaultAsync(p => p.PortCode == portCode.FromRouteCode(), ct);
        if (port is null) return TypedResults.NotFound();
        if (db.ExpectVersion(port, rowVersion) is { } missing) return missing;
        if (await db.Contacts.AnyAsync(c => c.PortId == port.PortId, ct))
            return MasterDataSupport.Conflict("The port still has contacts.", "Remove its contacts first, or set it inactive.");

        db.Ports.Remove(port);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static async Task<ValidationProblem?> ValidateAsync(MasterDataDbContext db, SavePortRequest r, Guid? portId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var country = Upper(r.CountryCode);
        await CountryAsync(db, "countryCode", country, errors, ct);
        // THLCH is in TH: a LOCODE whose country is not the port's is a typo on one of them.
        if (Upper(r.UnLocode) is { } locode && country is not null && !locode.StartsWith(country, StringComparison.Ordinal))
            errors["unLocode"] = [$"{locode} is a {locode[..2]} code, but the port's country is {country}."];
        Coordinates(r.Latitude, r.Longitude, errors);
        if (Clean(r.Timezone) is { } tz && !TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _))
            errors["timezone"] = [$"Unknown time zone '{tz}'. Use an IANA name such as Asia/Bangkok."];
        return Problem(errors);
    }

    /// <summary>uq_port__locode: one port per UN/LOCODE, checked first so a clash is a 409 that names the port, not a 500.</summary>
    private static Task<string?> LocodeHolderAsync(MasterDataDbContext db, SavePortRequest r, Guid? except, CancellationToken ct) =>
        Upper(r.UnLocode) is { } locode
            ? db.Ports.AsNoTracking().Where(p => p.UnLocode == locode && p.PortId != except).Select(p => p.PortCode).FirstOrDefaultAsync(ct)
            : Task.FromResult<string?>(null);

    private static void Apply(Port p, SavePortRequest r)
    {
        p.UnLocode = Upper(r.UnLocode);
        p.PortNameEn = r.PortNameEn.Trim();
        p.PortNameLocal = Clean(r.PortNameLocal);
        p.PortType = r.PortType;
        p.CountryCode = r.CountryCode.Trim().ToUpperInvariant();
        p.TradeMode = r.TradeMode;
        p.Latitude = r.Latitude;
        p.Longitude = r.Longitude;
        p.Timezone = Clean(r.Timezone);
        p.Postcode = Clean(r.Postcode);
        p.EdiMappingCode = Upper(r.EdiMappingCode);
        p.PaperlessCode = Upper(r.PaperlessCode);
        p.IsActive = r.IsActive;
    }
}
