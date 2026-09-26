using Gecko.Identity.Infrastructure.Auth;
using Gecko.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Application.Auth;

/// <summary>
/// Assembles the access-token claims. Used at login, at refresh (so a role change
/// takes effect within one access-token lifetime) and at invitation acceptance.
///
/// Reads through a TENANT-scoped context — rule 5 of 06_rls_security.sql: once the
/// user is authenticated, stop using system context.
/// </summary>
public sealed class ClaimsBuilder(TenantDbContextFactory tenantDb)
{
    private static readonly string[] UsableEntitlementStatuses = ["TRIAL", "ACTIVE", "PAST_DUE"];

    public async Task<AccessTokenClaims> BuildAsync(Guid tenantId, Guid userId, string userType, CancellationToken ct)
    {
        await using var db = tenantDb.Create(tenantId, userId);

        var branches = await (
            from ub in db.UserBranches
            join b in db.Branches on ub.BranchId equals b.BranchId
            where ub.UserId == userId && b.IsActive
            select b.BranchId).Distinct().ToListAsync(ct);

        var roles = await (
            from ur in db.UserRoles
            join r in db.Roles on ur.RoleId equals r.RoleId
            where ur.UserId == userId && r.IsActive
            select new { r.RoleId, r.RoleCode, ur.BranchId }).ToListAsync(ct);

        // Only tenant-wide roles grant tenant-wide permissions. OPS_MANAGER at one
        // branch must not unlock an admin endpoint that acts on the whole tenant.
        var tenantWideRoleIds = roles.Where(r => r.BranchId is null).Select(r => r.RoleId).ToList();
        var permissions = await db.RolePermissions
            .Where(rp => tenantWideRoleIds.Contains(rp.RoleId))
            .Select(rp => rp.PermissionCode)
            .Distinct()
            .ToListAsync(ct);

        // ...and a branch-scoped role grants the same permissions AT ITS BRANCH,
        // carried in `bpm` (PLAN Q11). Without this a gate clerk signs in and can
        // call nothing, because `prm` is empty for branch staff.
        var branchRoleIds = roles.Where(r => r.BranchId is not null).Select(r => r.RoleId).Distinct().ToList();
        var branchPermissions = new Dictionary<Guid, IReadOnlyCollection<string>>();
        if (branchRoleIds.Count > 0)
        {
            var grants = await db.RolePermissions
                .Where(rp => branchRoleIds.Contains(rp.RoleId))
                .Select(rp => new { rp.RoleId, rp.PermissionCode })
                .ToListAsync(ct);

            var tenantWide = permissions.ToHashSet(StringComparer.Ordinal);
            branchPermissions = roles
                .Where(r => r.BranchId is not null)
                .GroupBy(r => r.BranchId!.Value)
                .Select(branch => new
                {
                    BranchId = branch.Key,
                    // A permission already held tenant-wide is not repeated per branch:
                    // `prm` already answers for every branch, and the token stays small.
                    Permissions = (IReadOnlyCollection<string>)grants
                        .Where(g => branch.Any(r => r.RoleId == g.RoleId) && !tenantWide.Contains(g.PermissionCode))
                        .Select(g => g.PermissionCode)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToList(),
                })
                .Where(b => b.Permissions.Count > 0)
                .ToDictionary(b => b.BranchId, b => b.Permissions);
        }

        var modules = await db.Entitlements
            .Where(e => UsableEntitlementStatuses.Contains(e.Status))
            .Select(e => e.ModuleCode)
            .Distinct()
            .ToListAsync(ct);

        return new AccessTokenClaims(
            userId,
            tenantId,
            userType,
            branches,
            modules,
            roles.Select(r => r.BranchId is null ? r.RoleCode : $"{r.RoleCode}@{r.BranchId}").Distinct().ToList(),
            permissions,
            branchPermissions);
    }
}
