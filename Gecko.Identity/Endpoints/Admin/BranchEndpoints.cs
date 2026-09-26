using System.ComponentModel.DataAnnotations;
using Gecko.Data;
using Gecko.Identity.Infrastructure.Persistence;
using Gecko.Identity.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Endpoints.Admin;

public sealed record BranchResponse(
    Guid BranchId, string BranchCode, string DisplayName, string BranchType, string? Unlocode,
    string? AddressLine1, string? AddressLine2, string? City, string CountryCode,
    string? Timezone, string? DefaultLocale, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CreateBranchRequest(
    [property: Required, RegularExpression("^[A-Z0-9][A-Z0-9-]{1,29}$", ErrorMessage = "Upper-case letters, digits and '-', 2-30 chars, e.g. LCB-01.")] string BranchCode,
    [property: Required, MaxLength(200)] string DisplayName,
    [property: Required, AllowedValues("DEPOT", "TERMINAL", "CFS", "YARD", "OFFICE", "WAREHOUSE")] string BranchType,
    [property: Required, StringLength(2, MinimumLength = 2)] string CountryCode,
    [property: RegularExpression("^[A-Z]{2}[A-Z0-9]{3}$", ErrorMessage = "UN/LOCODE, 5 chars, e.g. THLCH.")] string? Unlocode = null,
    [property: MaxLength(200)] string? AddressLine1 = null,
    [property: MaxLength(200)] string? AddressLine2 = null,
    [property: MaxLength(100)] string? City = null,
    [property: MaxLength(50)] string? Timezone = null,
    [property: MaxLength(10)] string? DefaultLocale = null);

public sealed record UpdateBranchRequest(
    [property: Required, MaxLength(200)] string DisplayName,
    [property: Required, AllowedValues("DEPOT", "TERMINAL", "CFS", "YARD", "OFFICE", "WAREHOUSE")] string BranchType,
    [property: Required, StringLength(2, MinimumLength = 2)] string CountryCode,
    bool IsActive,
    [property: RegularExpression("^[A-Z]{2}[A-Z0-9]{3}$", ErrorMessage = "UN/LOCODE, 5 chars, e.g. THLCH.")] string? Unlocode = null,
    [property: MaxLength(200)] string? AddressLine1 = null,
    [property: MaxLength(200)] string? AddressLine2 = null,
    [property: MaxLength(100)] string? City = null,
    [property: MaxLength(50)] string? Timezone = null,
    [property: MaxLength(10)] string? DefaultLocale = null);

/// <summary>
/// Tenant branches (depots, terminals, CFS...). Reading is open to every signed-in
/// user — the console needs the branch picker — writing needs admin.branch.manage.
/// The branch code is immutable: TOS, EDI and CODECO messages reference it.
/// </summary>
internal static class BranchEndpoints
{
    public static RouteGroupBuilder MapBranchEndpoints(this RouteGroupBuilder api)
    {
        var branches = api.MapGroup("/branches").WithTags("Branches");

        branches.MapGet("/", ListAsync).RequireAuthorization().WithSummary("List branches of the caller's tenant");
        branches.MapGet("/{branchId:guid}", GetAsync).RequireAuthorization().WithName("GetBranch").WithSummary("Get one branch");
        branches.MapPost("/", CreateAsync).RequirePermission(Permissions.BranchManage).Validate<CreateBranchRequest>().WithSummary("Create a branch");
        branches.MapPut("/{branchId:guid}", UpdateAsync).RequirePermission(Permissions.BranchManage).Validate<UpdateBranchRequest>().WithSummary("Update a branch (branch code is immutable)");
        branches.MapDelete("/{branchId:guid}", DeleteAsync).RequirePermission(Permissions.BranchManage).WithSummary("Soft-delete a branch");

        return api;
    }

    private static async Task<Ok<PagedResult<BranchResponse>>> ListAsync(
        [AsParameters] ListQuery query, IdentityDbContext db, CancellationToken ct, bool includeInactive = false)
    {
        var branches = db.Branches.AsNoTracking();
        if (!includeInactive) branches = branches.Where(b => b.IsActive);
        if (!string.IsNullOrWhiteSpace(query.Search))
            branches = branches.Where(b => b.BranchCode.Contains(query.Search) || b.DisplayName.Contains(query.Search));

        return TypedResults.Ok(await branches.OrderBy(b => b.BranchCode).Select(ToResponse).ToPagedAsync(query.Page, query.PageSize, ct));
    }

    private static async Task<Results<Ok<BranchResponse>, NotFound>> GetAsync(Guid branchId, IdentityDbContext db, CancellationToken ct) =>
        await db.Branches.AsNoTracking().Where(b => b.BranchId == branchId).Select(ToResponse).SingleOrDefaultAsync(ct) is { } branch
            ? TypedResults.Ok(branch)
            : TypedResults.NotFound();

    private static async Task<Results<CreatedAtRoute<BranchResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateBranchRequest request, IdentityDbContext db, ITenantContext caller, CancellationToken ct)
    {
        if (!await db.Countries.AnyAsync(c => c.CountryCode == request.CountryCode && c.IsActive, ct))
            return AdminSupport.InvalidReference("countryCode", $"Unknown country '{request.CountryCode}'.");
        if (await db.Branches.AnyAsync(b => b.BranchCode == request.BranchCode, ct))
            return AdminSupport.Conflict($"Branch code '{request.BranchCode}' already exists.");

        var branch = new Branch
        {
            TenantId = caller.TenantId(),
            BranchCode = request.BranchCode,
            DisplayName = request.DisplayName,
            BranchType = request.BranchType,
            Unlocode = request.Unlocode,
            AddressLine1 = request.AddressLine1,
            AddressLine2 = request.AddressLine2,
            City = request.City,
            CountryCode = request.CountryCode,
            Timezone = request.Timezone,
            DefaultLocale = request.DefaultLocale,
            IsActive = true,
        };
        // branch_id is NEWSEQUENTIALID() in the database, so the audit row needs a
        // second save — one transaction keeps a branch from ever existing unaudited.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Branches.Add(branch);
        await db.SaveChangesAsync(ct);

        var response = Map(branch);
        db.RecordChange(caller, "BRANCH", branch.BranchId, "CREATE", after: response);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return TypedResults.CreatedAtRoute(response, "GetBranch", new { branchId = branch.BranchId });
    }

    private static async Task<Results<Ok<BranchResponse>, NotFound, ValidationProblem>> UpdateAsync(
        Guid branchId, UpdateBranchRequest request, IdentityDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var branch = await db.Branches.SingleOrDefaultAsync(b => b.BranchId == branchId, ct);
        if (branch is null) return TypedResults.NotFound();
        if (!await db.Countries.AnyAsync(c => c.CountryCode == request.CountryCode && c.IsActive, ct))
            return AdminSupport.InvalidReference("countryCode", $"Unknown country '{request.CountryCode}'.");

        var before = Map(branch);
        branch.DisplayName = request.DisplayName;
        branch.BranchType = request.BranchType;
        branch.Unlocode = request.Unlocode;
        branch.AddressLine1 = request.AddressLine1;
        branch.AddressLine2 = request.AddressLine2;
        branch.City = request.City;
        branch.CountryCode = request.CountryCode;
        branch.Timezone = request.Timezone;
        branch.DefaultLocale = request.DefaultLocale;
        branch.IsActive = request.IsActive;

        var after = Map(branch);
        db.RecordChange(caller, "BRANCH", branch.BranchId, before.IsActive == after.IsActive ? "UPDATE" : after.IsActive ? "ACTIVATE" : "SUSPEND", before, after);
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(after);
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        Guid branchId, IdentityDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var branch = await db.Branches.SingleOrDefaultAsync(b => b.BranchId == branchId, ct);
        if (branch is null) return TypedResults.NotFound();

        // A branch with live users or subscriptions is still in use; removing it would
        // strand them (no FKs to stop it). Deactivate instead, or move them first.
        if (await db.UserBranches.AnyAsync(ub => ub.BranchId == branchId, ct))
            return AdminSupport.Conflict("Branch still has users assigned. Remove them first, or set isActive = false.");
        if (await db.Entitlements.AnyAsync(e => e.BranchId == branchId, ct))
            return AdminSupport.Conflict("Branch still has module subscriptions. Cancel them first, or set isActive = false.");

        db.RecordChange(caller, "BRANCH", branch.BranchId, "SOFT_DELETE", before: Map(branch));
        db.Branches.Remove(branch);   // AuditStampInterceptor turns this into UPDATE deleted_at
        await db.SaveChangesAsync(ct);

        return TypedResults.NoContent();
    }

    private static readonly System.Linq.Expressions.Expression<Func<Branch, BranchResponse>> ToResponse = b => new BranchResponse(
        b.BranchId, b.BranchCode, b.DisplayName, b.BranchType, b.Unlocode, b.AddressLine1, b.AddressLine2, b.City,
        b.CountryCode, b.Timezone, b.DefaultLocale, b.IsActive, b.CreatedAt, b.UpdatedAt);

    private static readonly Func<Branch, BranchResponse> Map = ToResponse.Compile();
}
