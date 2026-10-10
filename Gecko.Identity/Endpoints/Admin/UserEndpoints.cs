using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Gecko.Data;
using Gecko.Identity.Infrastructure.Persistence;
using Gecko.Identity.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Endpoints.Admin;

public sealed record UserSummary(
    Guid UserId, string Email, string? UserName, string FullName, string? JobTitle, string UserType, string Status,
    bool IsLocked, DateTimeOffset? LastLoginAt, int BranchCount, IReadOnlyList<string> Roles);

public sealed record UserBranchResponse(Guid BranchId, string BranchCode, string DisplayName);

public sealed record UserRoleResponse(Guid UserRoleId, Guid RoleId, string RoleCode, Guid? BranchId, string? BranchCode);

public sealed record UserResponse(
    Guid UserId, string Email, string? UserName, string FullName, string? Phone, string? JobTitle, string UserType, string Status,
    Guid? DefaultBranchId, string? Locale, string? Timezone, DateTimeOffset? EmailVerifiedAt, DateTimeOffset? LastLoginAt,
    int FailedLoginCount, DateTimeOffset? LockedUntil, IReadOnlyList<UserBranchResponse> Branches, IReadOnlyList<UserRoleResponse> Roles,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record UpdateUserRequest(
    [property: Required, MaxLength(200)] string FullName,
    [property: MaxLength(30)] string? Phone = null,
    [property: MaxLength(100)] string? JobTitle = null,
    Guid? DefaultBranchId = null,
    [property: MaxLength(10)] string? Locale = null,
    [property: MaxLength(50)] string? Timezone = null);

/// <param name="UserName">The name the user may sign in with instead of their e-mail; null or blank removes it.</param>
public sealed record SetUserNameRequest(
    [property: MaxLength(64), RegularExpression(@"^[^@]*$", ErrorMessage = "A user name cannot contain '@'.")] string? UserName);

public sealed record SetUserBranchesRequest([property: Required] IReadOnlyList<Guid> BranchIds);

public sealed record AssignRoleRequest([property: Required] Guid RoleId, Guid? BranchId = null);

/// <summary>
/// Tenant users. There is deliberately NO create endpoint: a user comes into
/// existence by accepting an invitation and setting their own password (ADR-006).
/// An admin never chooses, sees or transmits someone else's password.
/// </summary>
internal static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users").WithTags("Users").RequirePermission(Permissions.UserManage);

        users.MapGet("/", ListAsync).WithSummary("List users (to add one, create an invitation)");
        users.MapGet("/{userId:guid}", GetAsync).WithName("GetUser").WithSummary("Get a user with branches and roles");
        users.MapPut("/{userId:guid}", UpdateAsync).Validate<UpdateUserRequest>().WithSummary("Update profile fields");
        users.MapPut("/{userId:guid}/user-name", SetUserNameAsync).Validate<SetUserNameRequest>()
            .WithSummary("Set or clear the user name the user may sign in with instead of their e-mail")
            .WithDescription("Unique across every tenant (sign-in has no tenant to narrow by) and case-insensitive; 409 when someone already holds it.");
        users.MapPost("/{userId:guid}/disable", DisableAsync).WithSummary("Disable sign-in and revoke sessions");
        users.MapPost("/{userId:guid}/enable", EnableAsync).WithSummary("Re-enable a disabled user");
        users.MapPost("/{userId:guid}/unlock", UnlockAsync).WithSummary("Clear a failed-login lockout");
        users.MapDelete("/{userId:guid}", DeleteAsync).WithSummary("Soft-delete a user (email becomes reusable)");
        users.MapPut("/{userId:guid}/branches", SetBranchesAsync).Validate<SetUserBranchesRequest>().WithSummary("Replace the branches a user can access");
        users.MapPost("/{userId:guid}/roles", AssignRoleAsync).Validate<AssignRoleRequest>().WithSummary("Assign a role, tenant-wide or at one branch");
        users.MapDelete("/{userId:guid}/roles/{userRoleId:guid}", RemoveRoleAsync).WithSummary("Remove a role assignment");

        return api;
    }

    private static async Task<Ok<PagedResult<UserSummary>>> ListAsync(
        [AsParameters] ListQuery query, IdentityDbContext db, TimeProvider clock, CancellationToken ct,
        string? status = null, Guid? branchId = null)
    {
        var now = clock.GetUtcNow();
        var users = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) users = users.Where(u => u.Status == status.ToUpperInvariant());
        if (branchId is not null) users = users.Where(u => db.UserBranches.Any(ub => ub.UserId == u.UserId && ub.BranchId == branchId));
        if (!string.IsNullOrWhiteSpace(query.Search))
            users = users.Where(u => u.EmailNormalised.Contains(query.Search.ToLower()) || u.FullName.Contains(query.Search) || u.UserName!.Contains(query.Search));

        var page = await users
            .OrderBy(u => u.FullName)
            .Select(u => new UserSummary(
                u.UserId, u.Email, u.UserName, u.FullName, u.JobTitle, u.UserType, u.Status,
                u.LockedUntil > now, u.LastLoginAt,
                db.UserBranches.Count(ub => ub.UserId == u.UserId),
                (from ur in db.UserRoles
                 join r in db.Roles on ur.RoleId equals r.RoleId
                 where ur.UserId == u.UserId
                 orderby r.RoleCode
                 select r.RoleCode).ToList()))
            .ToPagedAsync(query.Page, query.PageSize, ct);

        return TypedResults.Ok(page);
    }

    private static async Task<Results<Ok<UserResponse>, NotFound>> GetAsync(Guid userId, IdentityDbContext db, CancellationToken ct) =>
        await LoadAsync(db, userId, ct) is { } user ? TypedResults.Ok(user) : TypedResults.NotFound();

    private static async Task<Results<Ok<UserResponse>, NotFound, ValidationProblem>> UpdateAsync(
        Guid userId, UpdateUserRequest request, IdentityDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return TypedResults.NotFound();
        if (request.DefaultBranchId is { } defaultBranch && !await db.UserBranches.AnyAsync(ub => ub.UserId == userId && ub.BranchId == defaultBranch, ct))
            return AdminSupport.InvalidReference("defaultBranchId", "The default branch must be one of the user's branches.");

        var before = new { user.FullName, user.Phone, user.JobTitle, user.DefaultBranchId, user.Locale, user.Timezone };
        user.FullName = request.FullName;
        user.Phone = request.Phone;
        user.JobTitle = request.JobTitle;
        user.DefaultBranchId = request.DefaultBranchId;
        user.Locale = request.Locale;
        user.Timezone = request.Timezone;

        db.RecordChange(caller, "USER", userId, "UPDATE", before, new { user.FullName, user.Phone, user.JobTitle, user.DefaultBranchId, user.Locale, user.Timezone });
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok((await LoadAsync(db, userId, ct))!);
    }

    private static async Task<Results<Ok<UserResponse>, NotFound, ProblemHttpResult>> SetUserNameAsync(
        Guid userId, SetUserNameRequest request, IdentityDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return TypedResults.NotFound();

        var before = new { user.UserName };
        user.UserName = string.IsNullOrWhiteSpace(request.UserName) ? null : request.UserName.Trim();
        db.RecordChange(caller, "USER", userId, "UPDATE", before, new { user.UserName });

        // Another tenant's user is invisible here (RLS), so uq_user__user_name is the check.
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return AdminSupport.Conflict($"The user name '{user.UserName}' is already taken.");
        }

        return TypedResults.Ok((await LoadAsync(db, userId, ct))!);
    }

    private static async Task<Results<Ok<UserResponse>, NotFound, ProblemHttpResult>> DisableAsync(
        Guid userId, IdentityDbContext db, ITenantContext caller, TimeProvider clock, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return TypedResults.NotFound();
        if (userId == caller.UserId) return AdminSupport.Conflict("You cannot disable yourself.");
        if (user.Status != "ACTIVE") return AdminSupport.Conflict($"Only an ACTIVE user can be disabled (status is {user.Status}).");
        if (await AdminSupport.IsLastTenantAdminAsync(db, userId, ct))
            return AdminSupport.Conflict("This is the tenant's last active administrator. Grant the role to someone else first.");

        user.Status = "DISABLED";
        await RevokeSessionsAsync(db, userId, clock.GetUtcNow(), ct);
        db.RecordChange(caller, "USER", userId, "SUSPEND", new { Status = "ACTIVE" }, new { user.Status });
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok((await LoadAsync(db, userId, ct))!);
    }

    private static async Task<Results<Ok<UserResponse>, NotFound, ProblemHttpResult>> EnableAsync(
        Guid userId, IdentityDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return TypedResults.NotFound();
        if (user.Status != "DISABLED") return AdminSupport.Conflict($"Only a DISABLED user can be enabled (status is {user.Status}).");

        user.Status = "ACTIVE";
        db.RecordChange(caller, "USER", userId, "ACTIVATE", new { Status = "DISABLED" }, new { user.Status });
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok((await LoadAsync(db, userId, ct))!);
    }

    private static async Task<Results<Ok<UserResponse>, NotFound>> UnlockAsync(
        Guid userId, IdentityDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return TypedResults.NotFound();

        var before = new { user.FailedLoginCount, user.LockedUntil };
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        db.RecordChange(caller, "USER", userId, "UPDATE", before, new { user.FailedLoginCount, user.LockedUntil }, reason: "unlock");
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok((await LoadAsync(db, userId, ct))!);
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        Guid userId, IdentityDbContext db, ITenantContext caller, TimeProvider clock, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return TypedResults.NotFound();
        if (userId == caller.UserId) return AdminSupport.Conflict("You cannot delete yourself.");
        if (await AdminSupport.IsLastTenantAdminAsync(db, userId, ct))
            return AdminSupport.Conflict("This is the tenant's last active administrator. Grant the role to someone else first.");

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.UserRoles.RemoveRange(await db.UserRoles.Where(ur => ur.UserId == userId).ToListAsync(ct));
        db.UserBranches.RemoveRange(await db.UserBranches.Where(ub => ub.UserId == userId).ToListAsync(ct));
        await RevokeSessionsAsync(db, userId, now, ct);

        // gecko_app is DENIED SELECT on credential.password_hash, so the rows cannot be
        // loaded as entities — a set-based UPDATE never reads the column.
        await db.Credentials
            .Where(c => c.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeletedAt, now).SetProperty(c => c.DeletedBy, caller.UserId).SetProperty(c => c.IsCurrent, false), ct);

        db.RecordChange(caller, "USER", userId, "SOFT_DELETE", before: new { user.Email, user.FullName, user.Status });
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<UserResponse>, NotFound, ValidationProblem>> SetBranchesAsync(
        Guid userId, SetUserBranchesRequest request, IdentityDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return TypedResults.NotFound();

        var wanted = request.BranchIds.ToHashSet();
        var valid = await db.Branches.Where(b => wanted.Contains(b.BranchId) && b.IsActive).Select(b => b.BranchId).ToListAsync(ct);
        if (valid.Count != wanted.Count)
            return AdminSupport.InvalidReference("branchIds", $"Unknown or inactive branch(es): {string.Join(", ", wanted.Except(valid))}.");

        var current = await db.UserBranches.Where(ub => ub.UserId == userId).ToListAsync(ct);
        var removed = current.Where(ub => !wanted.Contains(ub.BranchId)).ToList();
        var added = wanted.Except(current.Select(ub => ub.BranchId)).ToList();
        var removedIds = removed.Select(r => r.BranchId).ToHashSet();

        db.UserBranches.RemoveRange(removed);
        foreach (var branchId in added)
            db.UserBranches.Add(new UserBranch { TenantId = user.TenantId, UserId = userId, BranchId = branchId });

        // A role held AT a branch the user can no longer access is meaningless — and
        // would silently come back if the branch were re-added. Remove it with the branch.
        var strandedRoles = await db.UserRoles.Where(ur => ur.UserId == userId && ur.BranchId != null && removedIds.Contains(ur.BranchId.Value)).ToListAsync(ct);
        db.UserRoles.RemoveRange(strandedRoles);
        if (user.DefaultBranchId is { } d && removedIds.Contains(d)) user.DefaultBranchId = null;

        if (removed.Count > 0) db.RecordChange(caller, "USER", userId, "REVOKE", before: new { branches = removedIds, roles = strandedRoles.Select(r => r.UserRoleId) });
        if (added.Count > 0) db.RecordChange(caller, "USER", userId, "GRANT", after: new { branches = added });
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok((await LoadAsync(db, userId, ct))!);
    }

    private static async Task<Results<Ok<UserResponse>, NotFound, ValidationProblem, ProblemHttpResult>> AssignRoleAsync(
        Guid userId, AssignRoleRequest request, IdentityDbContext db, ITenantContext caller, ClaimsPrincipal principal, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return TypedResults.NotFound();
        if (!await db.Roles.AnyAsync(r => r.RoleId == request.RoleId && r.IsActive, ct))
            return AdminSupport.InvalidReference("roleId", "Unknown or inactive role.");
        if (request.BranchId is { } branchId && !await db.UserBranches.AnyAsync(ub => ub.UserId == userId && ub.BranchId == branchId, ct))
            return AdminSupport.InvalidReference("branchId", "Give the user access to the branch before assigning a role there.");
        if (await db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == request.RoleId && ur.BranchId == request.BranchId, ct))
            return AdminSupport.Conflict("The user already holds this role at this scope.");
        if (await AdminSupport.EnsureCallerCanGrantRoleAsync(db, principal, request.RoleId, ct) is { } forbidden)
            return forbidden;

        var assignment = new UserRole { TenantId = user.TenantId, UserId = userId, RoleId = request.RoleId, BranchId = request.BranchId };
        db.UserRoles.Add(assignment);
        db.RecordChange(caller, "USER", userId, "GRANT", after: new { request.RoleId, request.BranchId });
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok((await LoadAsync(db, userId, ct))!);
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RemoveRoleAsync(
        Guid userId, Guid userRoleId, IdentityDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var assignment = await db.UserRoles.SingleOrDefaultAsync(ur => ur.UserRoleId == userRoleId && ur.UserId == userId, ct);
        if (assignment is null) return TypedResults.NotFound();

        var isAdminRole = assignment.BranchId is null && await db.Roles.AnyAsync(r => r.RoleId == assignment.RoleId && r.IsTenantAdmin, ct);
        if (isAdminRole && await AdminSupport.IsLastTenantAdminAsync(db, userId, ct))
            return AdminSupport.Conflict("This is the tenant's last active administrator. Grant the role to someone else first.");

        db.RecordChange(caller, "USER", userId, "REVOKE", before: new { assignment.RoleId, assignment.BranchId });
        db.UserRoles.Remove(assignment);
        await db.SaveChangesAsync(ct);

        return TypedResults.NoContent();
    }

    /// <summary>Disabling or deleting a user must end sessions they already hold, not just block the next login.</summary>
    private static async Task RevokeSessionsAsync(IdentityDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct) =>
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.RevokedReason, "ADMIN_REVOKE"), ct);

    private static Task<UserResponse?> LoadAsync(IdentityDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.AsNoTracking()
            .Where(u => u.UserId == userId)
            .Select(u => new UserResponse(
                u.UserId, u.Email, u.UserName, u.FullName, u.Phone, u.JobTitle, u.UserType, u.Status,
                u.DefaultBranchId, u.Locale, u.Timezone, u.EmailVerifiedAt, u.LastLoginAt, u.FailedLoginCount, u.LockedUntil,
                (from ub in db.UserBranches
                 join b in db.Branches on ub.BranchId equals b.BranchId
                 where ub.UserId == u.UserId
                 orderby b.BranchCode
                 select new UserBranchResponse(b.BranchId, b.BranchCode, b.DisplayName)).ToList(),
                (from ur in db.UserRoles
                 join r in db.Roles on ur.RoleId equals r.RoleId
                 where ur.UserId == u.UserId
                 orderby r.RoleCode
                 select new UserRoleResponse(ur.UserRoleId, r.RoleId, r.RoleCode, ur.BranchId,
                     db.Branches.Where(b => b.BranchId == ur.BranchId).Select(b => b.BranchCode).FirstOrDefault())).ToList(),
                u.CreatedAt, u.UpdatedAt))
            .SingleOrDefaultAsync(ct);
}
