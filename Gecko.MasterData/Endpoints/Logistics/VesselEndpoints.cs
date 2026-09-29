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

public sealed record VesselResponse(
    Guid VesselId, string VesselCode, string VesselName, string? VesselNameLocal, string? ImoNumber, string? CallSign,
    string? Mmsi, string? VesselType, string? OperatorPartyCode, string? OperatorName, string? FlagCountryCode,
    int? TeuCapacity, int? GrossTonnage, decimal? LoaM, bool IsActive, DateTimeOffset UpdatedAt, string RowVersion);

public sealed record SaveVesselRequest(
    [property: Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,19}$", ErrorMessage = "Letters, digits, space, '.', '_' and '-', up to 20.")] string VesselCode,
    [property: Required, MaxLength(255)] string VesselName,
    [property: MaxLength(255)] string? VesselNameLocal = null,
    [property: RegularExpression("^[0-9]{7}$", ErrorMessage = "An IMO number is 7 digits.")] string? ImoNumber = null,
    [property: RegularExpression("^[A-Za-z0-9]{1,10}$", ErrorMessage = "A call sign is up to 10 letters and digits.")] string? CallSign = null,
    [property: RegularExpression("^[0-9]{9}$", ErrorMessage = "An MMSI is 9 digits.")] string? Mmsi = null,
    [property: AllowedValues(null, "CONTAINER", "FEEDER", "BARGE", "RORO", "GENERAL_CARGO", "BULK", "TANKER", "OTHER")] string? VesselType = null,
    [property: MaxLength(60)] string? OperatorPartyCode = null,
    [property: StringLength(2, MinimumLength = 2)] string? FlagCountryCode = null,
    [property: Range(0, 100_000)] int? TeuCapacity = null,
    [property: Range(0, 1_000_000)] int? GrossTonnage = null,
    [property: Range(0, 500)] decimal? LoaM = null,
    bool IsActive = true,
    string? RowVersion = null);

/// <summary>
/// Vessels (logistics.vessel) — what a vessel call and a booking's vessel point
/// at. KORAKIT has none: its old system had one placeholder "DUMMY", which was
/// not loaded. The operator is a party holding the SHIPPING_LINE role.
///
/// The IMO number carries a check digit (the 7th digit is the sum of the first
/// six weighted 7…2, mod 10). Only a NEW or CHANGED number is checked, so a
/// legacy row with a bad one can still be edited for something else — the same
/// rule as a Thai tax id on a party.
/// </summary>
internal static class VesselEndpoints
{
    public static RouteGroupBuilder MapVesselEndpoints(this RouteGroupBuilder master)
    {
        var vessels = master.MapGroup("/vessels").WithTags("Master data — vessels");
        vessels.MapGet("/", ListAsync).RequirePermission(MasterDataPermissions.LogisticsView).WithSummary("Search vessels by code, name, IMO, call sign or MMSI");
        vessels.MapGet("/{vesselCode}", GetAsync).RequirePermission(MasterDataPermissions.LogisticsView).WithName("GetVessel").WithSummary("Get one vessel");
        vessels.MapPost("/", CreateAsync).RequirePermission(MasterDataPermissions.LogisticsManage).Validate<SaveVesselRequest>().WithSummary("Add a vessel");
        vessels.MapPut("/{vesselCode}", UpdateAsync).RequirePermission(MasterDataPermissions.LogisticsManage).Validate<SaveVesselRequest>().WithSummary("Update a vessel (optimistic concurrency on rowVersion)");
        vessels.MapDelete("/{vesselCode}", DeleteAsync).RequirePermission(MasterDataPermissions.LogisticsManage).WithSummary("Soft-delete a vessel (?rowVersion=)");
        return master;
    }

    /// <summary>The IMO check digit: first six digits weighted 7…2, summed, mod 10 = the seventh.</summary>
    public static bool ImoCheckDigitOk(string imo)
    {
        var sum = 0;
        for (var i = 0; i < 6; i++) sum += (imo[i] - '0') * (7 - i);
        return sum % 10 == imo[6] - '0';
    }

    private static IQueryable<VesselResponse> Rows(MasterDataDbContext db, IQueryable<Vessel> vessels) =>
        from v in vessels
        join p in db.Parties.AsNoTracking() on v.OperatorPartyId equals (Guid?)p.PartyId into ops
        from p in ops.DefaultIfEmpty()
        select new VesselResponse(
            v.VesselId, v.VesselCode, v.VesselName, v.VesselNameLocal, v.ImoNumber, v.CallSign, v.Mmsi, v.VesselType,
            p == null ? null : p.PartyCode, p == null ? null : p.NameEn, v.FlagCountryCode,
            v.TeuCapacity, v.GrossTonnage, v.LoaM, v.IsActive, v.UpdatedAt, Convert.ToBase64String(v.RowVersion));

    private static async Task<Ok<PagedResult<VesselResponse>>> ListAsync(
        MasterDataDbContext db, CancellationToken ct,
        string? search = null, bool includeInactive = false, int? page = 1, int? pageSize = 50)
    {
        var vessels = db.Vessels.AsNoTracking();
        if (!includeInactive) vessels = vessels.Where(v => v.IsActive);
        if (Clean(search) is { } s)
            vessels = vessels.Where(v => v.VesselCode.Contains(s) || v.VesselName.Contains(s)
                || (v.VesselNameLocal != null && v.VesselNameLocal.Contains(s))
                || (v.ImoNumber != null && v.ImoNumber.Contains(s)) || (v.CallSign != null && v.CallSign.Contains(s))
                || (v.Mmsi != null && v.Mmsi.Contains(s)));
        return TypedResults.Ok(await Rows(db, vessels.OrderBy(v => v.VesselName).ThenBy(v => v.VesselCode)).ToPagedAsync(page, pageSize, ct));
    }

    private static async Task<Results<Ok<VesselResponse>, NotFound>> GetAsync(string vesselCode, MasterDataDbContext db, CancellationToken ct) =>
        await Rows(db, db.Vessels.AsNoTracking().Where(v => v.VesselCode == vesselCode.FromRouteCode())).SingleOrDefaultAsync(ct) is { } vessel
            ? TypedResults.Ok(vessel)
            : TypedResults.NotFound();

    private static async Task<Results<CreatedAtRoute<VesselResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SaveVesselRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var code = request.VesselCode.Trim().ToUpperInvariant();
        var (operatorId, invalid) = await ValidateAsync(db, request, null, ct);
        if (invalid is not null) return invalid;
        if (await db.Vessels.AnyAsync(v => v.VesselCode == code, ct))
            return MasterDataSupport.Conflict($"Vessel '{code}' already exists.");
        if (await ImoHolderAsync(db, request.ImoNumber, null, ct) is { } holder)
            return MasterDataSupport.Conflict("This IMO number is already a vessel.", $"IMO {request.ImoNumber} is vessel {holder}.");

        var vessel = new Vessel { TenantId = caller.TenantId(), VesselCode = code };
        Apply(vessel, request, operatorId);
        db.Vessels.Add(vessel);
        await db.SaveChangesAsync(ct);
        var saved = await Rows(db, db.Vessels.AsNoTracking().Where(v => v.VesselId == vessel.VesselId)).SingleAsync(ct);
        return TypedResults.CreatedAtRoute(saved, "GetVessel", new { vesselCode = code });
    }

    private static async Task<Results<Ok<VesselResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        string vesselCode, SaveVesselRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var vessel = await db.Vessels.SingleOrDefaultAsync(v => v.VesselCode == vesselCode.FromRouteCode(), ct);
        if (vessel is null) return TypedResults.NotFound();
        if (db.ExpectVersion(vessel, request.RowVersion) is { } missing) return missing;
        var (operatorId, invalid) = await ValidateAsync(db, request, vessel, ct);
        if (invalid is not null) return invalid;
        if (await ImoHolderAsync(db, request.ImoNumber, vessel.VesselId, ct) is { } holder)
            return MasterDataSupport.Conflict("This IMO number is already a vessel.", $"IMO {request.ImoNumber} is vessel {holder}.");

        Apply(vessel, request, operatorId);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(await Rows(db, db.Vessels.AsNoTracking().Where(v => v.VesselId == vessel.VesselId)).SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> DeleteAsync(
        string vesselCode, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var vessel = await db.Vessels.SingleOrDefaultAsync(v => v.VesselCode == vesselCode.FromRouteCode(), ct);
        if (vessel is null) return TypedResults.NotFound();
        if (db.ExpectVersion(vessel, rowVersion) is { } missing) return missing;
        if (await db.Contacts.AnyAsync(c => c.VesselId == vessel.VesselId, ct))
            return MasterDataSupport.Conflict("The vessel still has contacts.", "Remove its contacts first, or set it inactive.");

        db.Vessels.Remove(vessel);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static async Task<(Guid? OperatorId, ValidationProblem? Invalid)> ValidateAsync(
        MasterDataDbContext db, SaveVesselRequest r, Vessel? existing, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var imo = Clean(r.ImoNumber);
        if (imo is { Length: 7 } && imo.All(char.IsAsciiDigit) && imo != existing?.ImoNumber && !ImoCheckDigitOk(imo))
            errors["imoNumber"] = [$"{imo} fails the IMO check digit — check the number on the vessel's certificate."];
        await CountryAsync(db, "flagCountryCode", Upper(r.FlagCountryCode), errors, ct);
        var operatorId = await PartyAsync(db, "operatorPartyCode", Upper(r.OperatorPartyCode), lineOnly: true, errors, ct);
        return (operatorId, Problem(errors));
    }

    /// <summary>uq_vessel__imo, checked first so a clash is a 409 that names the vessel.</summary>
    private static Task<string?> ImoHolderAsync(MasterDataDbContext db, string? imoNumber, Guid? except, CancellationToken ct) =>
        Clean(imoNumber) is { } imo
            ? db.Vessels.AsNoTracking().Where(v => v.ImoNumber == imo && v.VesselId != except).Select(v => v.VesselCode).FirstOrDefaultAsync(ct)
            : Task.FromResult<string?>(null);

    private static void Apply(Vessel v, SaveVesselRequest r, Guid? operatorId)
    {
        v.VesselName = r.VesselName.Trim();
        v.VesselNameLocal = Clean(r.VesselNameLocal);
        v.ImoNumber = Clean(r.ImoNumber);
        v.CallSign = Upper(r.CallSign);
        v.Mmsi = Clean(r.Mmsi);
        v.VesselType = r.VesselType;
        v.OperatorPartyId = operatorId;
        v.FlagCountryCode = Upper(r.FlagCountryCode);
        v.TeuCapacity = r.TeuCapacity;
        v.GrossTonnage = r.GrossTonnage;
        v.LoaM = r.LoaM;
        v.IsActive = r.IsActive;
    }
}
