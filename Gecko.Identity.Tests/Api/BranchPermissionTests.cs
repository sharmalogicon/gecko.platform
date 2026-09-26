using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Gecko.Identity.Endpoints;
using Gecko.SharedKernel;

namespace Gecko.Identity.Tests.Api;

/// <summary>
/// The <c>bpm</c> claim (gecko_tos PLAN Q11). Before it, a branch-scoped user signed
/// in successfully and could then call nothing: <c>prm</c> only ever carried
/// tenant-wide grants, and depot staff hold their roles AT a depot.
///
/// Fixture shape this leans on (dev_02): <c>ops.lcb@sct.co.th</c> is OPS_MANAGER at
/// SCT-LCB01 and VIEWER at SCT-LKR01 — two branches with DIFFERENT permission sets,
/// which is what proves the grouping is per set and not per user.
/// </summary>
[Collection(ApiCollection.Name)]
public class BranchPermissionTests(ApiFactory api)
{
    [Fact]
    public async Task A_branch_scoped_user_carries_their_permissions_per_branch_and_still_none_tenant_wide()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(ApiFactory.SctOpsLcb);

        var me = (await client.GetFromJsonAsync<MeResponse>("/auth/me", ct))!;

        Assert.Empty(me.Permissions);                       // unchanged: `prm` is tenant-wide only
        Assert.Equal(2, me.BranchPermissions.Count);        // OPS_MANAGER's set, VIEWER's set

        var grants = me.BranchPermissions
            .Select(claim => BranchPermissionClaim.TryParse(claim, out var permissions, out var branches)
                ? (Permissions: permissions, Branches: branches)
                : throw new InvalidOperationException($"Unparseable bpm claim: {claim}"))
            .ToList();

        var manager = Assert.Single(grants, g => g.Permissions.Contains("tos.booking.manage"));
        var viewer = Assert.Single(grants, g => !g.Permissions.Contains("tos.booking.manage"));

        Assert.Contains("tos.hold.release.operations", manager.Permissions);
        Assert.Contains("tos.booking.view", viewer.Permissions);
        Assert.DoesNotContain("tos.booking.cancel", viewer.Permissions);

        // Each set names exactly its own branch, and the two are different branches.
        Assert.Single(manager.Branches);
        Assert.Single(viewer.Branches);
        Assert.NotEqual(manager.Branches[0], viewer.Branches[0]);
        Assert.All(grants, g => Assert.All(g.Branches, b => Assert.Contains(b.ToString(), me.Branches)));
    }

    /// <summary>
    /// The escalation this claim could have opened. OPS_MANAGER holds admin.audit.view,
    /// but only AT a branch; /api/audit is a tenant-wide endpoint and must stay shut.
    /// Tenant-wide endpoints keep RequirePermission (prm only) for exactly this reason.
    /// </summary>
    [Fact]
    public async Task A_branch_grant_does_not_open_a_tenant_wide_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var ops = await api.ClientForAsync(ApiFactory.SctOpsLcb);
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);

        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync("/api/audit/auth-events", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/audit/auth-events", ct)).StatusCode);
    }

    [Fact]
    public async Task A_tenant_wide_user_carries_no_branch_claims_at_all()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(ApiFactory.SctAdmin);

        var me = (await client.GetFromJsonAsync<MeResponse>("/auth/me", ct))!;

        Assert.NotEmpty(me.Permissions);
        Assert.Empty(me.BranchPermissions);
    }

    /// <summary>
    /// Why the claim is grouped. One claim per (permission, branch) pair would put
    /// SCT's 40-permission OPS_MANAGER at ~2.2 KB per branch, and a manager over three
    /// depots past the 8 KB header limit — a token that fails in production only for
    /// the tenants big enough to matter.
    /// </summary>
    [Fact]
    public async Task The_token_stays_small_enough_to_send_in_a_header()
    {
        var ct = TestContext.Current.CancellationToken;
        using var anonymous = api.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            "/auth/login", new { email = ApiFactory.SctOpsLcb, password = ApiFactory.Password }, ct);
        var token = (await response.Content.ReadFromJsonAsync<LoginResponse>(ct))!.AccessToken;

        Assert.True(token.Length < 4096, $"Access token is {token.Length} bytes — too close to the 8 KB header limit.");

        var payload = token.Split('.')[1];
        using var claims = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=').Replace('-', '+').Replace('_', '/')));
        var bpm = claims.RootElement.GetProperty(GeckoClaimTypes.BranchPermission);

        // Two sets => two claim values, each one string, not 40.
        Assert.Equal(2, bpm.GetArrayLength());
        Assert.All(bpm.EnumerateArray(), value => Assert.Contains("@", value.GetString()!, StringComparison.Ordinal));
    }
}
