using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Data;
using Gecko.Identity.Endpoints.Admin;

namespace Gecko.Identity.Tests.Api;

/// <summary>
/// Branch CRUD through the real HTTP pipeline: auth, permission policy, validation,
/// RLS, soft delete and change_log. Branches created here are soft-deleted at the end.
/// </summary>
[Collection(ApiCollection.Name)]
public class BranchApiTests(ApiFactory api)
{
    private static string UniqueCode() => "T-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    public async Task Admin_can_create_read_update_and_soft_delete_a_branch()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);
        var code = UniqueCode();

        var created = await admin.PostAsJsonAsync("/api/branches", new { branchCode = code, displayName = "Test depot", branchType = "DEPOT", countryCode = "TH" }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var branch = (await created.Content.ReadFromJsonAsync<BranchResponse>(ct))!;
        Assert.Equal(code, branch.BranchCode);
        Assert.Equal($"/api/branches/{branch.BranchId}", created.Headers.Location?.AbsolutePath, ignoreCase: true);

        var updated = await admin.PutAsJsonAsync($"/api/branches/{branch.BranchId}", new { displayName = "Renamed", branchType = "CFS", countryCode = "TH", isActive = false }, ct);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.False((await updated.Content.ReadFromJsonAsync<BranchResponse>(ct))!.IsActive);

        var activeOnly = await admin.GetFromJsonAsync<PagedResult<BranchResponse>>($"/api/branches?search={code}", ct);
        var withInactive = await admin.GetFromJsonAsync<PagedResult<BranchResponse>>($"/api/branches?search={code}&includeInactive=true", ct);
        Assert.Empty(activeOnly!.Items);
        Assert.Single(withInactive!.Items);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/branches/{branch.BranchId}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/branches/{branch.BranchId}", ct)).StatusCode);

        // The code is reusable after a soft delete — the filtered unique index at work.
        var again = await admin.PostAsJsonAsync("/api/branches", new { branchCode = code, displayName = "Reused", branchType = "DEPOT", countryCode = "TH" }, ct);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        var reused = (await again.Content.ReadFromJsonAsync<BranchResponse>(ct))!;
        await admin.DeleteAsync($"/api/branches/{reused.BranchId}", ct);
    }

    [Fact]
    public async Task Every_write_is_recorded_in_the_change_log()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);

        var created = await admin.PostAsJsonAsync("/api/branches", new { branchCode = UniqueCode(), displayName = "Audited", branchType = "YARD", countryCode = "TH" }, ct);
        var branch = (await created.Content.ReadFromJsonAsync<BranchResponse>(ct))!;
        await admin.DeleteAsync($"/api/branches/{branch.BranchId}", ct);

        await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
        var actions = db.ChangeLogs.Where(c => c.EntityId == branch.BranchId).OrderBy(c => c.ChangeLogId).Select(c => c.Action).ToList();
        Assert.Equal(["CREATE", "SOFT_DELETE"], actions);
    }

    [Fact]
    public async Task User_without_the_permission_can_read_but_not_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var ops = await api.ClientForAsync(ApiFactory.SctOpsLcb);

        var list = await ops.GetFromJsonAsync<PagedResult<BranchResponse>>("/api/branches", ct);
        Assert.NotEmpty(list!.Items);

        var create = await ops.PostAsJsonAsync("/api/branches", new { branchCode = UniqueCode(), displayName = "Nope", branchType = "DEPOT", countryCode = "TH" }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task Another_tenant_gets_404_for_every_verb()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(ApiFactory.SctAdmin);
        var sss = await api.ClientForAsync(ApiFactory.SssAdmin);
        var sctBranch = (await sct.GetFromJsonAsync<PagedResult<BranchResponse>>("/api/branches", ct))!.Items[0];

        Assert.Equal(HttpStatusCode.NotFound, (await sss.GetAsync($"/api/branches/{sctBranch.BranchId}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sss.PutAsJsonAsync($"/api/branches/{sctBranch.BranchId}",
            new { displayName = "Hijacked", branchType = "DEPOT", countryCode = "TH", isActive = false }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sss.DeleteAsync($"/api/branches/{sctBranch.BranchId}", ct)).StatusCode);

        var sssList = await sss.GetFromJsonAsync<PagedResult<BranchResponse>>("/api/branches?includeInactive=true", ct);
        Assert.DoesNotContain(sssList!.Items, b => b.BranchId == sctBranch.BranchId);
    }

    [Theory]
    [InlineData("lower-case", "TH", HttpStatusCode.BadRequest)]     // regex
    [InlineData(null, "ZZ", HttpStatusCode.BadRequest)]             // soft reference to lookup.country
    public async Task Invalid_input_is_a_400_not_a_500(string? code, string country, HttpStatusCode expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);

        var response = await admin.PostAsJsonAsync("/api/branches", new { branchCode = code ?? UniqueCode(), displayName = "Bad", branchType = "DEPOT", countryCode = country }, ct);

        Assert.Equal(expected, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.True(problem.RootElement.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Duplicate_branch_code_is_a_409()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);
        var existing = (await admin.GetFromJsonAsync<PagedResult<BranchResponse>>("/api/branches", ct))!.Items[0];

        var response = await admin.PostAsJsonAsync("/api/branches", new { branchCode = existing.BranchCode, displayName = "Dup", branchType = "DEPOT", countryCode = "TH" }, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Branch_with_users_cannot_be_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);
        var inUse = (await admin.GetFromJsonAsync<PagedResult<BranchResponse>>("/api/branches", ct))!.Items[0];

        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/branches/{inUse.BranchId}", ct)).StatusCode);
    }

    [Fact]
    public async Task No_token_is_401()
    {
        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/branches", TestContext.Current.CancellationToken)).StatusCode);
    }
}
