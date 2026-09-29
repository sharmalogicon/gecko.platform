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

namespace Gecko.MasterData.Endpoints.Org;

public sealed record CountryReferenceResponse(
    string CountryCode, string Iso3Code, string NumericCode, string NameEn, string? OfficialNameEn, string? NameLocal,
    string? DefaultCurrency, bool IsActive);

public sealed record PublicHolidayResponse(
    Guid PublicHolidayId, Guid? BranchId, DateOnly HolidayDate, string NameEn, string? NameLocal, bool IsHalfDay, string RowVersion);

/// <summary>BranchId null = every depot of the tenant.</summary>
public sealed record SavePublicHolidayRequest(
    DateOnly HolidayDate,
    [property: Required, MaxLength(150)] string NameEn,
    [property: MaxLength(150)] string? NameLocal = null,
    bool IsHalfDay = false,
    Guid? BranchId = null,
    string? RowVersion = null);

/// <summary>
/// Countries (lookup.country — global, ISO 3166, read-only for tenants) and the
/// tenant's public holidays (org.public_holiday), which later feed gate hours
/// and storage-day counting. A holiday for every depot needs mdm.org.manage
/// tenant-wide; one depot's holiday needs it at that depot. KORAKIT starts with
/// none — its 2012–2015 legacy rows were SCT's and were not loaded.
/// </summary>
internal static class CalendarEndpoints
{
    public static RouteGroupBuilder MapCalendarEndpoints(this RouteGroupBuilder master)
    {
        master.MapGet("/countries", ListCountriesAsync).RequireAuthorization()
            .WithTags("Master data — organisation").WithSummary("The ISO 3166 country list (global, read-only)");

        var holidays = master.MapGroup("/public-holidays").WithTags("Master data — organisation");
        holidays.MapGet("/", ListHolidaysAsync).RequireBranchPermission(MasterDataPermissions.OrgView).WithSummary("A year's public holidays: the tenant's, plus a depot's own when branchId is given");
        holidays.MapPost("/", CreateHolidayAsync).RequireBranchPermission(MasterDataPermissions.OrgManage).Validate<SavePublicHolidayRequest>().WithSummary("Add a public holiday (every depot, or one)");
        holidays.MapPut("/{publicHolidayId:guid}", UpdateHolidayAsync).RequireBranchPermission(MasterDataPermissions.OrgManage).Validate<SavePublicHolidayRequest>().WithSummary("Update a public holiday (optimistic concurrency on rowVersion)");
        holidays.MapDelete("/{publicHolidayId:guid}", DeleteHolidayAsync).RequireBranchPermission(MasterDataPermissions.OrgManage).WithSummary("Soft-delete a public holiday (?rowVersion=)");
        return master;
    }

    private static async Task<Ok<IReadOnlyList<CountryReferenceResponse>>> ListCountriesAsync(
        MasterDataDbContext db, CancellationToken ct, string? search = null, bool includeInactive = false)
    {
        var countries = db.Countries.AsNoTracking();
        if (!includeInactive) countries = countries.Where(c => c.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            countries = countries.Where(c => c.CountryCode == s || c.Iso3Code == s || c.NumericCode == s
                || c.NameEn.Contains(s) || (c.OfficialNameEn != null && c.OfficialNameEn.Contains(s)) || (c.NameLocal != null && c.NameLocal.Contains(s)));
        }
        return TypedResults.Ok<IReadOnlyList<CountryReferenceResponse>>(await countries.OrderBy(c => c.NameEn)
            .Select(c => new CountryReferenceResponse(c.CountryCode, c.Iso3Code, c.NumericCode, c.NameEn, c.OfficialNameEn, c.NameLocal, c.DefaultCurrency, c.IsActive))
            .ToListAsync(ct));
    }

    private static PublicHolidayResponse Map(PublicHoliday h) =>
        new(h.PublicHolidayId, h.BranchId, h.HolidayDate, h.NameEn, h.NameLocal, h.IsHalfDay, Convert.ToBase64String(h.RowVersion));

    private static async Task<Ok<IReadOnlyList<PublicHolidayResponse>>> ListHolidaysAsync(
        MasterDataDbContext db, CancellationToken ct, int? year = null, Guid? branchId = null)
    {
        var y = year ?? DateTime.UtcNow.Year;
        var from = new DateOnly(y, 1, 1);
        var to = new DateOnly(y, 12, 31);
        var holidays = db.PublicHolidays.AsNoTracking()
            .Where(h => h.HolidayDate >= from && h.HolidayDate <= to && (h.BranchId == null || h.BranchId == branchId));
        return TypedResults.Ok<IReadOnlyList<PublicHolidayResponse>>(
            (await holidays.OrderBy(h => h.HolidayDate).ThenBy(h => h.BranchId).ToListAsync(ct)).Select(Map).ToList());
    }

    /// <summary>Every depot = tenant-wide grant; one depot = the grant at that depot.</summary>
    private static bool MayWrite(ICallerPermissions scope, Guid? branchId) =>
        branchId is { } b ? scope.HasAt(MasterDataPermissions.OrgManage, b) : scope.HasTenantWide(MasterDataPermissions.OrgManage);

    private static async Task<Results<Created<PublicHolidayResponse>, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> CreateHolidayAsync(
        SavePublicHolidayRequest request, MasterDataDbContext db, ITenantContext caller, ICallerPermissions scope, CancellationToken ct)
    {
        if (!MayWrite(scope, request.BranchId)) return TypedResults.Forbid();
        var (invalid, clash) = await ValidateAsync(db, request, null, ct);
        if (invalid is not null) return invalid;
        if (clash is not null) return clash;

        var holiday = new PublicHoliday { TenantId = caller.TenantId() };
        Apply(holiday, request);
        db.PublicHolidays.Add(holiday);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/master/public-holidays/{holiday.PublicHolidayId}", Map(holiday));
    }

    private static async Task<Results<Ok<PublicHolidayResponse>, NotFound, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> UpdateHolidayAsync(
        Guid publicHolidayId, SavePublicHolidayRequest request, MasterDataDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var holiday = await db.PublicHolidays.SingleOrDefaultAsync(h => h.PublicHolidayId == publicHolidayId, ct);
        if (holiday is null) return TypedResults.NotFound();
        // Both the scope it has and the scope it is moved to must be the caller's.
        if (!MayWrite(scope, holiday.BranchId) || !MayWrite(scope, request.BranchId)) return TypedResults.Forbid();
        if (db.ExpectVersion(holiday, request.RowVersion) is { } missing) return missing;
        var (invalid, clash) = await ValidateAsync(db, request, publicHolidayId, ct);
        if (invalid is not null) return invalid;
        if (clash is not null) return clash;

        Apply(holiday, request);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(Map(holiday));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> DeleteHolidayAsync(
        Guid publicHolidayId, string? rowVersion, MasterDataDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var holiday = await db.PublicHolidays.SingleOrDefaultAsync(h => h.PublicHolidayId == publicHolidayId, ct);
        if (holiday is null) return TypedResults.NotFound();
        if (!MayWrite(scope, holiday.BranchId)) return TypedResults.Forbid();
        if (db.ExpectVersion(holiday, rowVersion) is { } missing) return missing;
        db.PublicHolidays.Remove(holiday);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    /// <summary>uq_public_holiday__tenant / __branch: one holiday per date per scope — a 409 naming it, not a 500.</summary>
    private static async Task<(ValidationProblem? Invalid, ProblemHttpResult? Clash)> ValidateAsync(
        MasterDataDbContext db, SavePublicHolidayRequest r, Guid? except, CancellationToken ct)
    {
        if (r.HolidayDate == default)
            return (MasterDataSupport.InvalidReference("holidayDate", "Give the date of the holiday."), null);
        if (r.BranchId is { } b && !await db.BranchProfiles.AsNoTracking().AnyAsync(x => x.BranchId == b, ct))
            return (MasterDataSupport.InvalidReference("branchId", "Unknown depot for this tenant."), null);
        var same = await db.PublicHolidays.AsNoTracking()
            .Where(h => h.HolidayDate == r.HolidayDate && h.BranchId == r.BranchId && h.PublicHolidayId != except)
            .Select(h => h.NameEn).FirstOrDefaultAsync(ct);
        return (null, same is null ? null : MasterDataSupport.Conflict("There is already a holiday on that date.", $"{r.HolidayDate:yyyy-MM-dd} is already {same}."));
    }

    private static void Apply(PublicHoliday h, SavePublicHolidayRequest r)
    {
        h.BranchId = r.BranchId;
        h.HolidayDate = r.HolidayDate;
        h.NameEn = r.NameEn.Trim();
        h.NameLocal = string.IsNullOrWhiteSpace(r.NameLocal) ? null : r.NameLocal.Trim();
        h.IsHalfDay = r.IsHalfDay;
    }
}
