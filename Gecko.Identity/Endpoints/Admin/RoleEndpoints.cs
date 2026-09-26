using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Gecko.Data;
using Gecko.Identity.Infrastructure.Persistence;
using Gecko.Identity.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Endpoints.Admin;

public sealed record RoleSummary(
    Guid RoleId, string RoleCode, string DisplayName, string? Description,
    bool IsSystem, bool IsTenantAdmin, bool IsActive, int PermissionCount, int UserCount);

public sealed record RoleResponse(
    Guid RoleId, string RoleCode, string DisplayName, string? Description,
    bool IsSystem, bool IsTenantAdmin, bool IsActive, IReadOnlyList<string> Permissions,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CreateRoleRequest(
    [property: Required, RegularExpression("^[A-Z][A-Z0-9_]{1,39}$", ErrorMessage = "Upper-case letters, digits and '_', 2-40 chars, e.g. NIGHT_SUPERVISOR.")] string RoleCode,
    [property: Required, MaxLength(100)] string DisplayName,
    [property: Required, MinLength(1)] IReadOnlyList<string> Permissions,
    [property: MaxLength(400)] string? Description = null);

public sealed record UpdateRoleRequest(
    [property: Required, MaxLength(100)] string DisplayName,
    bool IsActive,
    [property: MaxLength(400)] string? Description = null);

public sealed record SetRolePermissionsRequest([property: Required] IReadOnlyList<string> Permissions);

public sealed record PermissionResponse(string PermissionCode, string ModuleCode, string DisplayName, string? Description);

/// <summary>
/// Tenant roles. System roles (seeded by usp_provision_tenant) can be renamed but
/// not deactivated, re-permissioned or deleted — editing TENANT_OWNER's grants is
/// the fastest way for a tenant to lock itself out of its own admin screens.
/// </summary>
internal static class RoleEndpoints
{
    public static RouteGroupBuilder MapRoleEndpoints(this RouteGroupBuilder api)
    {
        var roles = api.MapGroup("/roles").WithTags("Roles & permissions");

        roles.MapGet("/", ListAsync).RequireAuthorization().WithSummary("List roles with permission and user counts");
        roles.MapGet("/{roleId:guid}", GetAsync).RequireAuthorization().WithName("GetRole").WithSummary("Get a role and its permissions");
        roles.MapPost("/", CreateAsync).RequirePermission(Permissions.RoleManage).Validate<CreateRoleRequest>().WithSummary("Create a custom role");
        roles.MapPut("/{roleId:guid}", UpdateAsync).RequirePermission(Permissions.RoleManage).Validate<UpdateRoleRequest>().WithSummary("Rename / (de)activate a role");
        roles.MapPut("/{roleId:guid}/permissions", SetPermissionsAsync).RequirePermission(Permissions.RoleManage).Validate<SetRolePermissionsRequest>().WithSummary("Replace a custom role's permissions");
        roles.MapDelete("/{roleId:guid}", DeleteAsync).RequirePermission(Permissions.RoleManage).WithSummary("Soft-delete a custom role that nobody holds");

        api.MapGet("/permissions", ListPermissionsAsync).RequireAuthorization().WithTags("Roles & permissions").WithSummary("Every grantable permission (platform reference data)");

        return api;
    }

    private static async Task<Ok<List<RoleSummary>>> ListAsync(IdentityDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Roles.AsNoTracking()
            .OrderBy(r => r.RoleCode)
            .Select(r => new RoleSummary(
                r.RoleId, r.RoleCode, r.DisplayName, r.Description, r.IsSystem, r.IsTenantAdmin, r.IsActive,
                db.RolePermissions.Count(rp => rp.RoleId == r.RoleId),
                db.UserRoles.Where(ur => ur.RoleId == r.RoleId).Select(ur => ur.UserId).Distinct().Count()))
            .ToListAsync(ct));

    private static async Task<Results<Ok<RoleResponse>, NotFound>> GetAsync(Guid roleId, IdentityDbContext db, CancellationToken ct) =>
        await LoadAsync(db, roleId, ct) is { } role ? TypedResults.Ok(role) : TypedResults.NotFound();

    private static async Task<Results<CreatedAtRoute<RoleResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateRoleRequest request, IdentityDbContext db, ITenantContext caller, ClaimsPrincipal user, CancellationToken ct)
    {
        if (await db.Roles.AnyAsync(r => r.RoleCode == request.RoleCode, ct))
            return AdminSupport.Conflict($"Role code '{request.RoleCode}' already exists.");
        if (await CheckGrantableAsync(db, user, request.Permissions, ct) is { } problem)
            return problem;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var role = new Role
        {
            TenantId = caller.TenantId(),
            RoleCode = request.RoleCode,
            DisplayName = request.DisplayName,
            Description = request.Description,
            IsSystem = false,
            IsTenantAdmin = false,   // only provisioning creates tenant-admin roles
            IsActive = true,
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);

        foreach (var code in request.Permissions.Distinct())
            db.RolePermissions.Add(new RolePermission { TenantId = role.TenantId, RoleId = role.RoleId, PermissionCode = code });

        db.RecordChange(caller, "ROLE", role.RoleId, "CREATE", after: new { role.RoleCode, role.DisplayName, request.Permissions });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return TypedResults.CreatedAtRoute((await LoadAsync(db, role.RoleId, ct))!, "GetRole", new { roleId = role.RoleId });
    }

    private static async Task<Results<Ok<RoleResponse>, NotFound, ProblemHttpResult>> UpdateAsync(
        Guid roleId, UpdateRoleRequest request, IdentityDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var role = await db.Roles.SingleOrDefaultAsync(r => r.RoleId == roleId, ct);
        if (role is null) return TypedResults.NotFound();
        if (role.IsSystem && !request.IsActive)
            return AdminSupport.Conflict("System roles cannot be deactivated.");

        var before = new { role.DisplayName, role.Description, role.IsActive };
        role.DisplayName = request.DisplayName;
        role.Description = request.Description;
        role.IsActive = request.IsActive;

        db.RecordChange(caller, "ROLE", role.RoleId, "UPDATE", before, new { role.DisplayName, role.Description, role.IsActive });
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok((await LoadAsync(db, roleId, ct))!);
    }

    private static async Task<Results<Ok<RoleResponse>, NotFound, ValidationProblem, ProblemHttpResult>> SetPermissionsAsync(
        Guid roleId, SetRolePermissionsRequest request, IdentityDbContext db, ITenantContext caller, ClaimsPrincipal user, CancellationToken ct)
    {
        var role = await db.Roles.SingleOrDefaultAsync(r => r.RoleId == roleId, ct);
        if (role is null) return TypedResults.NotFound();
        if (role.IsSystem)
            return AdminSupport.Conflict("System role permissions are fixed. Create a custom role instead.");
        if (await CheckGrantableAsync(db, user, request.Permissions, ct) is { } problem)
            return problem;

        var wanted = request.Permissions.ToHashSet(StringComparer.Ordinal);
        var current = await db.RolePermissions.Where(rp => rp.RoleId == roleId).ToListAsync(ct);

        var revoked = current.Where(rp => !wanted.Contains(rp.PermissionCode)).ToList();
        var granted = wanted.Except(current.Select(rp => rp.PermissionCode)).ToList();

        db.RolePermissions.RemoveRange(revoked);   // soft delete — revoking a grant keeps its history
        foreach (var code in granted)
            db.RolePermissions.Add(new RolePermission { TenantId = role.TenantId, RoleId = roleId, PermissionCode = code });

        if (revoked.Count > 0)
            db.RecordChange(caller, "ROLE", roleId, "REVOKE", before: revoked.Select(r => r.PermissionCode));
        if (granted.Count > 0)
            db.RecordChange(caller, "ROLE", roleId, "GRANT", after: granted);

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok((await LoadAsync(db, roleId, ct))!);
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        Guid roleId, IdentityDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var role = await db.Roles.SingleOrDefaultAsync(r => r.RoleId == roleId, ct);
        if (role is null) return TypedResults.NotFound();
        if (role.IsSystem)
            return AdminSupport.Conflict("System roles cannot be deleted.");
        if (await db.UserRoles.AnyAsync(ur => ur.RoleId == roleId, ct))
            return AdminSupport.Conflict("Role is still assigned to users. Remove it from them first.");
        if (await db.Invitations.AnyAsync(i => i.RoleId == roleId && i.AcceptedAt == null && i.RevokedAt == null, ct))
            return AdminSupport.Conflict("Role is used by a pending invitation. Cancel it first.");

        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(rp => rp.RoleId == roleId).ToListAsync(ct));
        db.RecordChange(caller, "ROLE", roleId, "SOFT_DELETE", before: new { role.RoleCode, role.DisplayName });
        db.Roles.Remove(role);
        await db.SaveChangesAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<PermissionResponse>>> ListPermissionsAsync(IdentityDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Permissions.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.ModuleCode).ThenBy(p => p.PermissionCode)
            .Select(p => new PermissionResponse(p.PermissionCode, p.ModuleCode, p.DisplayName, p.Description))
            .ToListAsync(ct));

    /// <summary>
    /// Two checks: every code exists (no FKs — this is the only guard), and the caller
    /// already holds every permission they are granting. Without the second, anyone
    /// with admin.role.manage could mint a role holding admin.billing.manage and
    /// assign it to themselves.
    /// </summary>
    private static async Task<ValidationProblem?> CheckGrantableAsync(
        IdentityDbContext db, ClaimsPrincipal user, IReadOnlyList<string> codes, CancellationToken ct)
    {
        var distinct = codes.Distinct().ToList();
        var known = await db.Permissions.Where(p => distinct.Contains(p.PermissionCode) && p.IsActive).Select(p => p.PermissionCode).ToListAsync(ct);
        var unknown = distinct.Except(known).ToList();
        if (unknown.Count > 0)
            return AdminSupport.InvalidReference("permissions", $"Unknown permission(s): {string.Join(", ", unknown)}.");

        var escalating = AdminSupport.NotHeldBy(user, distinct);
        return escalating.Count > 0
            ? AdminSupport.InvalidReference("permissions", $"You cannot grant permission(s) you do not hold: {string.Join(", ", escalating)}.")
            : null;
    }

    private static Task<RoleResponse?> LoadAsync(IdentityDbContext db, Guid roleId, CancellationToken ct) =>
        db.Roles.AsNoTracking()
            .Where(r => r.RoleId == roleId)
            .Select(r => new RoleResponse(
                r.RoleId, r.RoleCode, r.DisplayName, r.Description, r.IsSystem, r.IsTenantAdmin, r.IsActive,
                db.RolePermissions.Where(rp => rp.RoleId == r.RoleId).OrderBy(rp => rp.PermissionCode).Select(rp => rp.PermissionCode).ToList(),
                r.CreatedAt, r.UpdatedAt))
            .SingleOrDefaultAsync(ct);
}
