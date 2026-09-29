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

namespace Gecko.MasterData.Endpoints.Org;

public sealed record CompanyResponse(
    Guid CompanyId, string CompanyCode, string NameEn, string? NameLocal, string? ShortName,
    string? TaxId, string? TaxBranchCode, string? DefaultCurrency, string CountryCode, bool IsActive);

public sealed record YardResponse(
    Guid YardId, string YardCode, string NameEn, string? NameLocal, string YardType, string FullEmpty,
    string? DirectionCode, int? CapacityTeu, bool IsActive, string RowVersion);

/// <summary>
/// What a yard edit may change. The code stays (gate transactions and visits in
/// gecko_tos point at the yard), and a PUT replaces every field listed here.
/// </summary>
public sealed record SaveYardRequest(
    [property: Required, MaxLength(200)] string NameEn,
    [property: Required, AllowedValues("CY", "EMPTY", "EXPORT", "IMPORT", "CFS", "REEFER", "DG", "MNR", "MIXED")] string YardType,
    [property: Required, AllowedValues("FULL", "EMPTY", "BOTH")] string FullEmpty,
    [property: MaxLength(200)] string? NameLocal = null,
    [property: MaxLength(20)] string? DirectionCode = null,
    [property: Range(0, 1_000_000)] int? CapacityTeu = null,
    string? RowVersion = null);

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

        master.MapPut("/yards/{yardId:guid}", UpdateYardAsync)
            .RequireBranchPermission(MasterDataPermissions.OrgManage)
            .Validate<SaveYardRequest>()
            .WithTags("Master data — organisation")
            .WithSummary("Rename, retype or resize a yard (mdm.org.manage at the yard's branch; optimistic concurrency on rowVersion)");

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

    /// <summary>
    /// A yard belongs to a branch, so the grant must be at THAT branch (or
    /// tenant-wide): the policy only proved "somewhere". Blocks, rows and slots
    /// are not edited here — KORAKIT locates boxes at yard level.
    /// </summary>
    private static async Task<Results<Ok<YardResponse>, NotFound, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> UpdateYardAsync(
        Guid yardId, SaveYardRequest request, MasterDataDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var yard = await db.Yards.SingleOrDefaultAsync(y => y.YardId == yardId, ct);
        if (yard is null) return TypedResults.NotFound();
        if (!scope.HasAt(MasterDataPermissions.OrgManage, yard.BranchId)) return TypedResults.Forbid();
        if (db.ExpectVersion(yard, request.RowVersion) is { } missing) return missing;

        var direction = string.IsNullOrWhiteSpace(request.DirectionCode) ? null : request.DirectionCode.Trim().ToUpperInvariant();
        if (direction is not null && !await db.DirectionTypes.AnyAsync(d => d.Code == direction && d.IsActive, ct))
        {
            var known = await db.DirectionTypes.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.DisplayOrder).Select(d => d.Code).ToListAsync(ct);
            return MasterDataSupport.InvalidReference("directionCode", $"Unknown direction '{direction}'. Use one of: {string.Join(", ", known)} — or none for any.");
        }

        yard.NameEn = request.NameEn.Trim();
        yard.NameLocal = string.IsNullOrWhiteSpace(request.NameLocal) ? null : request.NameLocal.Trim();
        yard.YardType = request.YardType;
        yard.FullEmpty = request.FullEmpty;
        yard.DirectionCode = direction;
        yard.CapacityTeu = request.CapacityTeu;
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        return TypedResults.Ok(await db.Yards.AsNoTracking().Where(y => y.YardId == yardId).Select(ToYard).SingleAsync(ct));
    }

    private static readonly Expression<Func<Company, CompanyResponse>> ToCompany = c => new CompanyResponse(
        c.CompanyId, c.CompanyCode, c.NameEn, c.NameLocal, c.ShortName,
        c.TaxId, c.TaxBranchCode, c.DefaultCurrency, c.CountryCode, c.IsActive);

    private static readonly Expression<Func<Yard, YardResponse>> ToYard = y => new YardResponse(
        y.YardId, y.YardCode, y.NameEn, y.NameLocal, y.YardType, y.FullEmpty,
        y.DirectionCode, y.CapacityTeu, y.IsActive, Convert.ToBase64String(y.RowVersion));
}
