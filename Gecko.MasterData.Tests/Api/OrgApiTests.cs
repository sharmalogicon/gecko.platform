using System.Net;
using System.Net.Http.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// The TOS header's two reads: the company a branch operates under, and the
/// branch's yards. Read-only against the fixture rows — nothing is written.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class OrgApiTests(MasterDataApiFactory api)
{
    private const string Company = "/api/master/company";
    private const string Yards = "/api/master/yards";

    // Fixture branches (gecko_identity dev_* scripts, mirrored into org.branch_profile).
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");   // -> SCT-HQ
    private static readonly Guid SctLkr01 = Guid.Parse("785AE785-33A5-F111-9B0D-00919E4766D5");   // -> SCT-LOG
    private static readonly Guid OtherLcb01 = Guid.Parse("7A5AE785-33A5-F111-9B0D-00919E4766D5"); // SIAM-COMMERCIAL SCC-LCB01 -> SCC

    // ── company ─────────────────────────────────────────────────────────────

    /// <summary>
    /// SCT operates two companies. "The tenant's company" does not exist — the
    /// header must show the one the selected branch invoices from.
    /// </summary>
    [Fact]
    public async Task The_company_is_the_one_the_branch_operates_under()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var lcb = await sct.GetFromJsonAsync<CompanyRow>($"{Company}?branchId={SctLcb01}", ct);
        var lkr = await sct.GetFromJsonAsync<CompanyRow>($"{Company}?branchId={SctLkr01}", ct);

        Assert.Equal("SCT-HQ", lcb!.CompanyCode);
        Assert.Equal("0105551234567", lcb.TaxId);
        Assert.Equal("00000", lcb.TaxBranchCode);
        Assert.Equal("TH", lcb.CountryCode);
        Assert.Equal("SCT-LOG", lkr!.CompanyCode);
    }

    /// <summary>
    /// It is the user's own company, not admin data: a branch-scoped user with no
    /// tenant-wide permission at all can read it.
    /// </summary>
    [Fact]
    public async Task Any_signed_in_user_can_read_their_company()
    {
        var ct = TestContext.Current.CancellationToken;
        var ops = await api.ClientForAsync(MasterDataApiFactory.SctOpsLcb);

        var response = await ops.GetAsync($"{Company}?branchId={SctLcb01}", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Another_tenants_branch_has_no_company()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);

        var own = await other.GetFromJsonAsync<CompanyRow>($"{Company}?branchId={OtherLcb01}", ct);
        var leaked = await sct.GetAsync($"{Company}?branchId={OtherLcb01}", ct);

        Assert.Equal("SCC", own!.CompanyCode);
        Assert.Equal(HttpStatusCode.NotFound, leaked.StatusCode);
    }

    [Fact]
    public async Task An_unknown_branch_is_404_and_a_missing_one_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        Assert.Equal(HttpStatusCode.NotFound, (await sct.GetAsync($"{Company}?branchId={Guid.NewGuid()}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sct.GetAsync(Company, ct)).StatusCode);
    }

    // ── yards ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Yards_are_the_branchs_own_in_code_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var lcb = await sct.GetFromJsonAsync<Paged<YardRow>>($"{Yards}?branchId={SctLcb01}", ct);
        var lkr = await sct.GetFromJsonAsync<Paged<YardRow>>($"{Yards}?branchId={SctLkr01}", ct);

        Assert.Equal(["Y-EMPTY", "Y-MNR", "Y-REEFER"], lcb!.Items.Select(y => y.YardCode));
        Assert.Equal(3, lcb.TotalCount);
        Assert.Equal(1, lcb.TotalPages);
        Assert.All(lcb.Items, y => Assert.True(y.IsActive));

        var export = Assert.Single(lkr!.Items, y => y.YardCode == "Y-EXPORT");
        Assert.Equal("EXPORT", export.YardType);
        Assert.Equal("FULL", export.FullEmpty);
        Assert.Equal("EXPORT", export.DirectionCode);
        Assert.Equal(900, export.CapacityTeu);
    }

    [Fact]
    public async Task Another_tenants_yards_are_invisible()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);

        var own = await other.GetFromJsonAsync<Paged<YardRow>>($"{Yards}?branchId={OtherLcb01}", ct);
        var leaked = await sct.GetFromJsonAsync<Paged<YardRow>>($"{Yards}?branchId={OtherLcb01}&activeOnly=false", ct);

        Assert.Equal("Y-MAIN", Assert.Single(own!.Items).YardCode);
        Assert.Empty(leaked!.Items);
        Assert.Equal(0, leaked.TotalCount);
    }

    /// <summary>
    /// The header needs the yards for every user — gate clerks work one branch and
    /// hold no mdm.* permission — so this is not gated on mdm.config.view.
    /// </summary>
    [Fact]
    public async Task Branch_staff_can_read_yards_and_strangers_cannot()
    {
        var ct = TestContext.Current.CancellationToken;
        var ops = await api.ClientForAsync(MasterDataApiFactory.SctOpsLcb);
        using var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await ops.GetAsync($"{Yards}?branchId={SctLcb01}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Yards}?branchId={SctLcb01}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Company}?branchId={SctLcb01}", ct)).StatusCode);
    }

    [Fact]
    public async Task Inactive_yards_appear_only_when_asked_for()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var active = await sct.GetFromJsonAsync<Paged<YardRow>>($"{Yards}?branchId={SctLcb01}&activeOnly=true", ct);
        var all = await sct.GetFromJsonAsync<Paged<YardRow>>($"{Yards}?branchId={SctLcb01}&activeOnly=false", ct);

        Assert.All(active!.Items, y => Assert.True(y.IsActive));
        Assert.True(all!.TotalCount >= active.TotalCount);
        Assert.Subset(all.Items.Select(y => y.YardId).ToHashSet(), active.Items.Select(y => y.YardId).ToHashSet());
    }

    private sealed record CompanyRow(
        Guid CompanyId, string CompanyCode, string NameEn, string? NameLocal, string? ShortName,
        string? TaxId, string? TaxBranchCode, string? DefaultCurrency, string CountryCode, bool IsActive);

    private sealed record YardRow(
        Guid YardId, string YardCode, string NameEn, string? NameLocal, string YardType, string FullEmpty,
        string? DirectionCode, int? CapacityTeu, bool IsActive);

    private sealed record Paged<T>(List<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);
}
