using System.Net;
using System.Net.Http.Json;
using Gecko.Data;
using Gecko.Identity.Endpoints.Admin;

namespace Gecko.Identity.Tests.Api;

/// <summary>
/// Users, role assignment and the two guards that matter most: privilege escalation
/// and tenant lock-out. Fixture users are modified and restored within each test.
/// </summary>
[Collection(ApiCollection.Name)]
public class UserApiTests(ApiFactory api)
{
    private static async Task<UserResponse> FindUserAsync(HttpClient client, string email, CancellationToken ct)
    {
        var page = await client.GetFromJsonAsync<PagedResult<UserSummary>>($"/api/users?search={email}", ct);
        var summary = page!.Items.Single(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
        return (await client.GetFromJsonAsync<UserResponse>($"/api/users/{summary.UserId}", ct))!;
    }

    [Fact]
    public async Task Admin_lists_and_reads_users_of_own_tenant_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(ApiFactory.SctAdmin);
        var sss = await api.ClientForAsync(ApiFactory.SssAdmin);

        var ops = await FindUserAsync(sct, ApiFactory.SctOpsLcb, ct);
        Assert.Equal(2, ops.Branches.Count);
        Assert.Contains(ops.Roles, r => r.RoleCode == "OPS_MANAGER" && r.BranchCode is not null);

        Assert.Equal(HttpStatusCode.NotFound, (await sss.GetAsync($"/api/users/{ops.UserId}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sss.PostAsync($"/api/users/{ops.UserId}/disable", null, ct)).StatusCode);
    }

    [Fact]
    public async Task Users_api_requires_admin_user_manage()
    {
        var ops = await api.ClientForAsync(ApiFactory.SctOpsLcb);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync("/api/users", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Disabled_user_cannot_log_in_until_enabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);
        var target = await FindUserAsync(admin, "gate2.lcb@sct.co.th", ct);
        using var anonymous = api.CreateClient();
        object Login() => new { email = "gate2.lcb@sct.co.th", password = ApiFactory.Password };

        try
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/users/{target.UserId}/disable", null, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/auth/login", Login(), ct)).StatusCode);
        }
        finally
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/users/{target.UserId}/enable", null, ct)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/auth/login", Login(), ct)).StatusCode);
    }

    [Fact]
    public async Task Admin_cannot_lock_the_tenant_out()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);
        var self = await FindUserAsync(admin, ApiFactory.SctAdmin, ct);
        var ownerAssignment = self.Roles.Single(r => r.RoleCode == "TENANT_OWNER" && r.BranchId is null);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/users/{self.UserId}/disable", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/users/{self.UserId}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/users/{self.UserId}/roles/{ownerAssignment.UserRoleId}", ct)).StatusCode);
    }

    [Fact]
    public async Task Holder_of_user_manage_alone_cannot_hand_out_tenant_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await api.ClientForAsync(ApiFactory.SctAdmin);
        const string delegateEmail = "edi@sct.co.th";   // ACTIVE, EDI_COORDINATOR tenant-wide (audit@ is still INVITED)

        // A narrow custom role: can manage users, nothing else.
        var roleResponse = await owner.PostAsJsonAsync("/api/roles",
            new { roleCode = "TEST_HR_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), displayName = "HR clerk", permissions = new[] { "admin.user.manage" } }, ct);
        var hrRole = (await roleResponse.Content.ReadFromJsonAsync<RoleResponse>(ct))!;
        var delegateUser = await FindUserAsync(owner, delegateEmail, ct);
        var assigned = await (await owner.PostAsJsonAsync($"/api/users/{delegateUser.UserId}/roles", new { roleId = hrRole.RoleId }, ct))
            .Content.ReadFromJsonAsync<UserResponse>(ct);
        var assignment = assigned!.Roles.Single(r => r.RoleId == hrRole.RoleId);

        try
        {
            var hr = await api.ClientForAsync(delegateEmail);   // logs in now, so the token carries admin.user.manage
            var ownerRole = (await owner.GetFromJsonAsync<List<RoleSummary>>("/api/roles", ct))!.Single(r => r.RoleCode == "TENANT_OWNER");
            var victim = await FindUserAsync(owner, "gate1.lcb@sct.co.th", ct);

            var grant = await hr.PostAsJsonAsync($"/api/users/{victim.UserId}/roles", new { roleId = ownerRole.RoleId }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, grant.StatusCode);

            var invite = await hr.PostAsJsonAsync("/api/invitations", new { email = $"escalate-{Guid.NewGuid():N}@example.com", roleId = ownerRole.RoleId }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, invite.StatusCode);

            // ...but it can grant a role within its own permissions.
            var allowed = await hr.PostAsJsonAsync($"/api/users/{victim.UserId}/roles", new { roleId = hrRole.RoleId }, ct);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            var victimAssignment = (await allowed.Content.ReadFromJsonAsync<UserResponse>(ct))!.Roles.Single(r => r.RoleId == hrRole.RoleId);
            await owner.DeleteAsync($"/api/users/{victim.UserId}/roles/{victimAssignment.UserRoleId}", ct);
        }
        finally
        {
            await owner.DeleteAsync($"/api/users/{delegateUser.UserId}/roles/{assignment.UserRoleId}", ct);
            await owner.DeleteAsync($"/api/roles/{hrRole.RoleId}", ct);
        }
    }

    [Fact]
    public async Task Branch_access_replace_and_restore()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);
        var user = await FindUserAsync(admin, "ops.lkr@sct.co.th", ct);
        var original = user.Branches.Select(b => b.BranchId).ToList();
        var allBranches = (await admin.GetFromJsonAsync<PagedResult<BranchResponse>>("/api/branches", ct))!.Items.Select(b => b.BranchId).ToList();
        var extra = allBranches.Except(original).First();

        try
        {
            var widened = await admin.PutAsJsonAsync($"/api/users/{user.UserId}/branches", new { branchIds = original.Append(extra) }, ct);
            Assert.Equal(original.Count + 1, (await widened.Content.ReadFromJsonAsync<UserResponse>(ct))!.Branches.Count);

            var bogus = await admin.PutAsJsonAsync($"/api/users/{user.UserId}/branches", new { branchIds = new[] { Guid.NewGuid() } }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
        }
        finally
        {
            await admin.PutAsJsonAsync($"/api/users/{user.UserId}/branches", new { branchIds = original }, ct);
        }

        Assert.Equal(original.Order(), (await FindUserAsync(admin, "ops.lkr@sct.co.th", ct)).Branches.Select(b => b.BranchId).Order());
    }
}
