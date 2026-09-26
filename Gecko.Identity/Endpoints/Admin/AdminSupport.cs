using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Gecko.Identity.Infrastructure.Persistence;
using Gecko.Identity.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gecko.Identity.Endpoints.Admin;

/// <summary>Permission codes as seeded in iam.permission (07_seed_data.sql).</summary>
internal static class Permissions
{
    public const string BranchManage = "admin.branch.manage";
    public const string UserManage = "admin.user.manage";
    public const string RoleManage = "admin.role.manage";
    public const string ApiTokenManage = "admin.apitoken.manage";
    public const string BillingView = "admin.billing.view";
    public const string AuditView = "admin.audit.view";
}

internal static class AdminSupport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Guid TenantId(this ITenantContext caller) =>
        caller.TenantId ?? throw new InvalidOperationException("Admin endpoints require an authenticated tenant.");

    /// <summary>
    /// Appends to audit.change_log in the SAME SaveChanges as the change itself, so
    /// an admin action and its audit row commit or roll back together.
    /// </summary>
    public static void RecordChange(this IdentityDbContext db, ITenantContext caller,
        string entityType, Guid entityId, string action, object? before = null, object? after = null, string? reason = null)
    {
        db.ChangeLogs.Add(new ChangeLog
        {
            TenantId = caller.TenantId(),
            ActorUserId = caller.UserId,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before, Json),
            AfterJson = after is null ? null : JsonSerializer.Serialize(after, Json),
            Reason = reason,
        });
    }

    /// <summary>
    /// There are no foreign keys in gecko_identity (house convention), so a soft
    /// reference that points nowhere is caught here or not at all.
    /// </summary>
    public static ValidationProblem InvalidReference(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    public static ProblemHttpResult Conflict(string title) =>
        TypedResults.Problem(title: title, statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// Privilege-escalation guard: permissions in the list that the caller does not
    /// hold themselves. Applied when creating/changing a role AND when assigning or
    /// inviting to one — otherwise admin.user.manage alone could hand out TENANT_OWNER.
    /// </summary>
    public static List<string> NotHeldBy(ClaimsPrincipal caller, IEnumerable<string> permissionCodes)
    {
        var held = caller.FindAll(GeckoClaimTypes.Permission).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        return permissionCodes.Distinct().Where(code => !held.Contains(code)).ToList();
    }

    /// <summary><see cref="NotHeldBy"/> for every permission of a role, as a 403 problem or null.</summary>
    public static async Task<ProblemHttpResult?> EnsureCallerCanGrantRoleAsync(
        IdentityDbContext db, ClaimsPrincipal caller, Guid roleId, CancellationToken ct)
    {
        var codes = await db.RolePermissions.Where(rp => rp.RoleId == roleId).Select(rp => rp.PermissionCode).ToListAsync(ct);
        var missing = NotHeldBy(caller, codes);
        return missing.Count == 0
            ? null
            : TypedResults.Problem(
                title: "You cannot grant a role with permissions you do not hold.",
                detail: string.Join(", ", missing),
                statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Lock-out guard: true when the user is the only ACTIVE holder of a tenant-wide
    /// tenant-admin role. Disabling, deleting or demoting them would leave nobody able
    /// to manage the tenant, with no self-service way back.
    /// </summary>
    public static async Task<bool> IsLastTenantAdminAsync(IdentityDbContext db, Guid userId, CancellationToken ct)
    {
        var adminHolders =
            from ur in db.UserRoles
            join r in db.Roles on ur.RoleId equals r.RoleId
            join u in db.Users on ur.UserId equals u.UserId
            where ur.BranchId == null && r.IsTenantAdmin && r.IsActive && u.Status == "ACTIVE"
            select ur.UserId;

        var holders = await adminHolders.Distinct().ToListAsync(ct);
        return holders.Count == 1 && holders[0] == userId;
    }
}
