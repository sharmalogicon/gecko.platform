using System.Net;
using System.Net.Http.Json;
using Gecko.Data;
using Gecko.Identity.Endpoints.Admin;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Tests.Api;

[Collection(ApiCollection.Name)]
public class ReadOnlyApiTests(ApiFactory api)
{
    /// <summary>The one table with no RLS behind it: the explicit filter is the whole defence.</summary>
    [Fact]
    public async Task Auth_events_are_filtered_to_the_callers_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(ApiFactory.SctAdmin);
        var sss = await api.ClientForAsync(ApiFactory.SssAdmin);

        var sctEvents = await sct.GetFromJsonAsync<PagedResult<AuthEventResponse>>("/api/audit/auth-events?pageSize=200", ct);
        var sssEvents = await sss.GetFromJsonAsync<PagedResult<AuthEventResponse>>("/api/audit/auth-events?pageSize=200", ct);

        Assert.NotEmpty(sctEvents!.Items);
        Assert.NotEmpty(sssEvents!.Items);
        Assert.Empty(sctEvents.Items.Select(e => e.AuthEventId).Intersect(sssEvents.Items.Select(e => e.AuthEventId)));

        // Ground truth from the database, not a guess from e-mail domains: LOGOUT rows
        // carry no e-mail, and an invitation can name an address from any domain.
        await using var system = TestDatabase.ForSystem();
        async Task<List<Guid?>> OwnersAsync(IEnumerable<long> ids)
        {
            var list = ids.ToList();
            return await system.AuthEvents.Where(e => list.Contains(e.AuthEventId)).Select(e => e.TenantId).Distinct().ToListAsync(ct);
        }
        Assert.Equal([TestDatabase.Sct], await OwnersAsync(sctEvents.Items.Select(e => e.AuthEventId)));
        Assert.Equal([TestDatabase.Sss], await OwnersAsync(sssEvents.Items.Select(e => e.AuthEventId)));
    }

    [Fact]
    public async Task Change_log_shows_the_actor()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);
        var created = await admin.PostAsJsonAsync("/api/branches", new { branchCode = "T-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), displayName = "Log me", branchType = "OFFICE", countryCode = "TH" }, ct);
        var branch = (await created.Content.ReadFromJsonAsync<BranchResponse>(ct))!;
        await admin.DeleteAsync($"/api/branches/{branch.BranchId}", ct);

        var log = await admin.GetFromJsonAsync<PagedResult<ChangeLogResponse>>($"/api/audit/change-log?entityId={branch.BranchId}", ct);

        Assert.Equal(2, log!.Items.Count);
        Assert.All(log.Items, c => Assert.Equal(ApiFactory.SctAdmin, c.ActorEmail));
    }

    [Fact]
    public async Task Subscription_views_and_placeholder_prices()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);

        var entitlements = await admin.GetFromJsonAsync<List<EntitlementResponse>>("/api/subscription/entitlements", ct);
        Assert.Contains(entitlements!, e => e.ModuleCode == "TOS");

        // Non-public plans only appear when the tenant is on them.
        var plans = await admin.GetFromJsonAsync<List<PlanResponse>>("/api/subscription/plans", ct);
        var onPlans = entitlements!.Select(e => e.PlanId).ToHashSet();
        Assert.DoesNotContain(plans!, p => p.ModuleCode == "MNR" && !onPlans.Contains(p.PlanId));

        var invoices = await admin.GetFromJsonAsync<PagedResult<InvoiceSummary>>("/api/subscription/invoices", ct);
        if (invoices!.Items.Count > 0)
        {
            var invoice = await admin.GetFromJsonAsync<InvoiceResponse>($"/api/subscription/invoices/{invoices.Items[0].InvoiceId}", ct);
            Assert.NotEmpty(invoice!.Lines);
        }
    }

    [Theory]
    [InlineData("/api/audit/auth-events")]
    [InlineData("/api/audit/change-log")]
    [InlineData("/api/subscription/invoices")]
    public async Task Billing_and_audit_need_their_permission(string url)
    {
        var ops = await api.ClientForAsync(ApiFactory.SctOpsLcb);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync(url, TestContext.Current.CancellationToken)).StatusCode);
    }
}
