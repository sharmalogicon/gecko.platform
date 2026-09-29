using Gecko.MasterData.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Endpoints.Logistics;

/// <summary>
/// The checks ports, vessels, commodities and locations share. Every one adds its
/// message to an error map keyed by the field, so a screen gets all its bad
/// fields back in one 400.
/// </summary>
internal static class LogisticsSupport
{
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static string? Upper(string? value) => Clean(value)?.ToUpperInvariant();

    /// <summary>lookup.country is global and read-only; a code that is not in it is a typo, not a new country.</summary>
    public static async Task CountryAsync(MasterDataDbContext db, string field, string? countryCode, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        if (countryCode is null) return;
        if (!await db.Countries.AsNoTracking().AnyAsync(c => c.CountryCode == countryCode, ct))
            errors[field] = [$"Unknown country '{countryCode}'. Use the ISO 3166 two-letter code (TH, SG, CN…)."];
    }

    /// <summary>Mirrors ck_*__geo: both or neither, and on the globe.</summary>
    public static void Coordinates(decimal? latitude, decimal? longitude, Dictionary<string, string[]> errors)
    {
        if (latitude is null != longitude is null)
            errors[latitude is null ? "latitude" : "longitude"] = ["Give both latitude and longitude, or neither."];
        if (latitude is < -90 or > 90) errors["latitude"] = ["Latitude is between -90 and 90."];
        if (longitude is < -180 or > 180) errors["longitude"] = ["Longitude is between -180 and 180."];
    }

    /// <summary>
    /// A party named by code: its id, or an error on the field. <paramref name="lineOnly"/>
    /// = it must be a shipping line itself — an AGENT (APL-N acts for APL) holds the
    /// role too, but does not operate a vessel or hand out seals.
    /// </summary>
    public static async Task<Guid?> PartyAsync(MasterDataDbContext db, string field, string? partyCode, bool lineOnly,
        Dictionary<string, string[]> errors, CancellationToken ct)
    {
        if (partyCode is null) return null;
        var party = await db.Parties.AsNoTracking().Where(p => p.PartyCode == partyCode)
            .Select(p => new { p.PartyId, LineRole = db.ShippingLineExtensions.Where(e => e.PartyId == p.PartyId).Select(e => e.LineRole).FirstOrDefault() })
            .SingleOrDefaultAsync(ct);
        if (party is null) { errors[field] = [$"There is no party '{partyCode}'."]; return null; }
        if (lineOnly && party.LineRole is null) { errors[field] = [$"{partyCode} is not a shipping line."]; return null; }
        if (lineOnly && party.LineRole != "LINE") { errors[field] = [$"{partyCode} is an agent; name the line it acts for."]; return null; }
        return party.PartyId;
    }

    public static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem? Problem(Dictionary<string, string[]> errors) =>
        errors.Count == 0 ? null : Microsoft.AspNetCore.Http.TypedResults.ValidationProblem(errors);
}
