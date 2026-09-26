using System.Net;
using System.Net.Http.Json;
using Gecko.Identity.Endpoints;
using Gecko.Identity.Endpoints.Admin;

namespace Gecko.Identity.Tests.Api;

[Collection(ApiCollection.Name)]
public class RoleApiTests(ApiFactory api)
{
    private static string UniqueCode() => "TEST_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    /// <summary>
    /// 15_revenue_permissions.sql: the person who types a price is not the one who
    /// makes it live. ACCOUNTS drafts and imports; only TENANT_OWNER approves; the
    /// gate clerk can read a price (cash at the window) but not change it.
    /// </summary>
    [Fact]
    public async Task Revenue_permissions_separate_drafting_from_approval()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);
        var roles = (await admin.GetFromJsonAsync<List<RoleSummary>>("/api/roles", ct))!;

        async Task<IReadOnlyList<string>> RevenuePermissionsOf(string roleCode)
        {
            var id = roles.Single(r => r.RoleCode == roleCode).RoleId;
            var role = await admin.GetFromJsonAsync<RoleResponse>($"/api/roles/{id}", ct);
            return role!.Permissions.Where(p => p.StartsWith("revenue.", StringComparison.Ordinal)).Order().ToList();
        }

        Assert.Equal(
            ["revenue.cash.collect", "revenue.charge.waive", "revenue.import.manage", "revenue.tariff.approve", "revenue.tariff.manage", "revenue.tariff.view"],
            await RevenuePermissionsOf("TENANT_OWNER"));
        Assert.Equal(
            ["revenue.cash.collect", "revenue.charge.waive", "revenue.import.manage", "revenue.tariff.manage", "revenue.tariff.view"],
            await RevenuePermissionsOf("ACCOUNTS"));
        // 18_cashier_permissions.sql: the gate clerk takes the money at the window
        // but may not forgive it — waiving is a supervisor's act.
        Assert.Equal(["revenue.cash.collect", "revenue.tariff.view"], await RevenuePermissionsOf("GATE_CLERK"));
        Assert.Equal(["revenue.charge.waive", "revenue.tariff.view"], await RevenuePermissionsOf("OPS_MANAGER"));
        Assert.Empty(await RevenuePermissionsOf("EDI_COORDINATOR"));

        // Decision (a), 2026-09-17: accounts staff hold ACCOUNTS tenant-wide, because
        // `prm` only carries tenant-wide grants. A branch-scoped accounts user would
        // get no revenue.* at all and every tariff endpoint would 403.
        var accounts = await api.ClientForAsync("accounts@sct.co.th");
        var me = (await accounts.GetFromJsonAsync<MeResponse>("/auth/me", ct))!;
        Assert.Contains("revenue.tariff.manage", me.Permissions);
        Assert.DoesNotContain("revenue.tariff.approve", me.Permissions);
    }

    [Fact]
    public async Task Custom_role_lifecycle_with_permission_replacement()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);

        var created = await admin.PostAsJsonAsync("/api/roles",
            new { roleCode = UniqueCode(), displayName = "Night supervisor", permissions = new[] { "admin.audit.view", "admin.billing.view" } }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var role = (await created.Content.ReadFromJsonAsync<RoleResponse>(ct))!;
        Assert.False(role.IsSystem);
        Assert.Equal(["admin.audit.view", "admin.billing.view"], role.Permissions);

        var replaced = await admin.PutAsJsonAsync($"/api/roles/{role.RoleId}/permissions", new { permissions = new[] { "admin.audit.view", "admin.branch.manage" } }, ct);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(["admin.audit.view", "admin.branch.manage"], (await replaced.Content.ReadFromJsonAsync<RoleResponse>(ct))!.Permissions);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/roles/{role.RoleId}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/roles/{role.RoleId}", ct)).StatusCode);
    }

    [Fact]
    public async Task Unknown_permission_code_is_rejected()
    {
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);

        var response = await admin.PostAsJsonAsync("/api/roles",
            new { roleCode = UniqueCode(), displayName = "Bad", permissions = new[] { "admin.everything" } }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task System_roles_are_protected()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);
        var owner = (await admin.GetFromJsonAsync<List<RoleSummary>>("/api/roles", ct))!.Single(r => r.RoleCode == "TENANT_OWNER");

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/roles/{owner.RoleId}/permissions", new { permissions = new[] { "admin.audit.view" } }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/roles/{owner.RoleId}", new { displayName = "Owner", isActive = false }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/roles/{owner.RoleId}", ct)).StatusCode);
    }

    [Fact]
    public async Task Roles_are_tenant_isolated()
    {
        var ct = TestContext.Current.CancellationToken;
        var sctRoles = await (await api.ClientForAsync(ApiFactory.SctAdmin)).GetFromJsonAsync<List<RoleSummary>>("/api/roles", ct);
        var sssRoles = await (await api.ClientForAsync(ApiFactory.SssAdmin)).GetFromJsonAsync<List<RoleSummary>>("/api/roles", ct);

        Assert.Empty(sctRoles!.Select(r => r.RoleId).Intersect(sssRoles!.Select(r => r.RoleId)));
    }

    [Fact]
    public async Task Permission_catalogue_is_readable()
    {
        var ops = await api.ClientForAsync(ApiFactory.SctOpsLcb);

        var permissions = await ops.GetFromJsonAsync<List<PermissionResponse>>("/api/permissions", TestContext.Current.CancellationToken);

        Assert.Contains(permissions!, p => p.PermissionCode == "admin.user.manage");
    }
}
