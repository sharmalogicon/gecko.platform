using System.Linq.Expressions;
using Gecko.Data;
using Gecko.MasterData.Infrastructure.Persistence;
using Gecko.MasterData.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Endpoints.Org;

public sealed record CompanyResponse(
    Guid CompanyId, string CompanyCode, string NameEn, string? NameLocal, string? ShortName,
    string? TaxId, string? TaxBranchCode, string? DefaultCurrency, string CountryCode, bool IsActive);

public sealed record YardResponse(
    Guid YardId, string YardCode, string NameEn, string? NameLocal, string YardType, string FullEmpty,
    string? DirectionCode, int? CapacityTeu, bool IsActive);

/// <summary>
/// What the TOS header shows: the operating company's name and the branch's
/// yards. Open to every signed-in user, like /api/branches — a gate clerk holds
/// no mdm.* permission and still needs the header. RLS keeps it to the tenant.
///
/// A tenant may operate several companies (SCT invoices from SCT-HQ at Laem
/// Chabang and SCT-LOG at Lat Krabang), so the company is resolved through the
/// branch — org.branch_profile.company_id — never "the tenant's company".
/// </summary>
internal static class OrgEndpoints
{
    public static RouteGroupBuilder MapOrgEndpoints(this RouteGroupBuilder master)
    {
        master.MapGet("/company", GetCompanyAsync)
            .RequireAuthorization()
            .WithTags("Master data — organisation")
            .WithSummary("The company a branch operates under (sidebar wordmark, later EIRs and receipts)");

        master.MapGet("/yards", ListYardsAsync)
            .RequireAuthorization()
            .WithTags("Master data — organisation")
            .WithSummary("List a branch's yards");

        return master;
    }

    private static async Task<Results<Ok<CompanyResponse>, NotFound>> GetCompanyAsync(
        Guid branchId, MasterDataDbContext db, CancellationToken ct) =>
        await (from bp in db.BranchProfiles.AsNoTracking()
               join c in db.Companies.AsNoTracking() on bp.CompanyId equals c.CompanyId
               where bp.BranchId == branchId
               select c).Select(ToCompany).SingleOrDefaultAsync(ct) is { } company
            ? TypedResults.Ok(company)
            : TypedResults.NotFound();

    private static async Task<Ok<PagedResult<YardResponse>>> ListYardsAsync(
        Guid branchId, [AsParameters] ListQuery query, MasterDataDbContext db, CancellationToken ct, bool activeOnly = true)
    {
        var yards = db.Yards.AsNoTracking().Where(y => y.BranchId == branchId);
        if (activeOnly) yards = yards.Where(y => y.IsActive);
        if (!string.IsNullOrWhiteSpace(query.Search))
            yards = yards.Where(y => y.YardCode.Contains(query.Search) || y.NameEn.Contains(query.Search));

        return TypedResults.Ok(await yards.OrderBy(y => y.YardCode).Select(ToYard).ToPagedAsync(query.Page, query.PageSize, ct));
    }

    private static readonly Expression<Func<Company, CompanyResponse>> ToCompany = c => new CompanyResponse(
        c.CompanyId, c.CompanyCode, c.NameEn, c.NameLocal, c.ShortName,
        c.TaxId, c.TaxBranchCode, c.DefaultCurrency, c.CountryCode, c.IsActive);

    private static readonly Expression<Func<Yard, YardResponse>> ToYard = y => new YardResponse(
        y.YardId, y.YardCode, y.NameEn, y.NameLocal, y.YardType, y.FullEmpty,
        y.DirectionCode, y.CapacityTeu, y.IsActive);
}
