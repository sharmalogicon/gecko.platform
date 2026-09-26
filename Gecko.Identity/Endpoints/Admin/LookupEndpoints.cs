using Gecko.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Endpoints.Admin;

public sealed record CountryResponse(string CountryCode, string DisplayName, string DefaultTimezone, string DefaultLocale, string DefaultCurrency);

public sealed record ModuleResponse(string ModuleCode, string DisplayName, string? Description, bool IsBranchScoped, bool RequiresOnboarding);

/// <summary>Platform reference data (lookup.*). Read-only; no tenant_id, so RLS does not apply.</summary>
internal static class LookupEndpoints
{
    public static RouteGroupBuilder MapLookupEndpoints(this RouteGroupBuilder api)
    {
        var lookups = api.MapGroup("/lookups").WithTags("Lookups").RequireAuthorization();

        lookups.MapGet("/countries", async (IdentityDbContext db, CancellationToken ct) => TypedResults.Ok(
                await db.Countries.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.DisplayName)
                    .Select(c => new CountryResponse(c.CountryCode, c.DisplayName, c.DefaultTimezone, c.DefaultLocale, c.DefaultCurrency))
                    .ToListAsync(ct)))
            .WithSummary("Countries");

        lookups.MapGet("/modules", async (IdentityDbContext db, CancellationToken ct) => TypedResults.Ok(
                await db.Modules.AsNoTracking().Where(m => m.IsActive).OrderBy(m => m.SortOrder)
                    .Select(m => new ModuleResponse(m.ModuleCode, m.DisplayName, m.Description, m.IsBranchScoped, m.RequiresOnboarding))
                    .ToListAsync(ct)))
            .WithSummary("Platform modules (TOS, EDI, NOTIFICATION, ...)");

        return api;
    }
}
