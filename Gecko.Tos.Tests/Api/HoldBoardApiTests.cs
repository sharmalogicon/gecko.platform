using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Data;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Holds;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// The holds board (TIER3 §7): a read-only view over the hold rows that exist. What
/// is proved here is the one thing it adds to <c>GET /holds</c> — every hold has a
/// DEPOT (the booking's, or the yard the box stands in) — and that the board's
/// counts and its "may release" flag agree with the rows and the release endpoint.
///
/// Fixture (dev_02, SCT): CUSTOMS on MRKU4025989 (in the SCT-LCB01 yard), CUSTOMS on
/// AKLU6006714 (in no yard), CREDIT on an LCB01 booking, a released LINE_STOP.
/// Test holds go on ZZTU numbers and are removed in finally.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class HoldBoardApiTests(TosApiFactory api)
{
    private const string Board = "/api/tos/holds/board";
    private const string Summary = "/api/tos/holds/summary";
    private const string InLcbYard = "MRKU4025989";
    private const string InNoYard = "AKLU6006714";
    private const string GateLkr = "gate1.lkr@sct.co.th";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string TestBox()
    {
        var ten = $"ZZTU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    private static async Task<PagedResult<HoldBoardRow>> BoardAsync(HttpClient client, string query, CancellationToken ct) =>
        (await client.GetFromJsonAsync<PagedResult<HoldBoardRow>>($"{Board}?pageSize=200{query}", ct))!;

    private static async Task AssertFieldAsync(HttpResponseMessage response, string field, CancellationToken ct)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Contains(body.RootElement.GetProperty("errors").EnumerateObject(),
            p => string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Board_rows_carry_their_depot_age_and_who_may_release_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(TosApiFactory.SctOwner);

        var active = await BoardAsync(admin, "", ct);
        Assert.All(active.Items, r => Assert.True(r.Hold.IsActive));

        var inYard = Assert.Single(active.Items, r => r.Hold.ContainerNo == InLcbYard && r.Hold.HoldCode == "CUSTOMS");
        Assert.Equal((SctLcb01, "SCT-LCB01", "CONTAINER"), (inYard.DepotBranchId, inYard.DepotCode, inYard.HeldOn));
        Assert.Equal((true, true, "tos.hold.release.operations"), (inYard.CanRelease, inYard.ReleaseRefRequired, inYard.ReleasePermission));

        var travelling = Assert.Single(active.Items, r => r.Hold.ContainerNo == InNoYard);
        Assert.Null(travelling.DepotBranchId);

        var credit = Assert.Single(active.Items, r => r.Hold.HoldCode == "CREDIT");
        Assert.Equal(("BOOKING", "SCT-LCB01"), (credit.HeldOn, credit.DepotCode));
        Assert.True(credit.BoxesOnBooking >= 1);

        // Oldest first: the board is about what has been stuck longest.
        Assert.Equal(active.Items.OrderBy(r => r.Hold.AppliedAt).Select(r => r.Hold.ContainerHoldId),
            active.Items.Select(r => r.Hold.ContainerHoldId));

        // Filters that need master data (type, scope) and ones that do not (source, depot).
        var blocksAll = await BoardAsync(admin, "&blockingScope=ALL", ct);
        Assert.NotEmpty(blocksAll.Items);
        Assert.All(blocksAll.Items, r => Assert.Equal("ALL", r.Hold.BlockingScope));
        var customsType = inYard.Hold.HoldType!;
        Assert.All((await BoardAsync(admin, $"&holdType={customsType}", ct)).Items, r => Assert.Equal(customsType, r.Hold.HoldType));
        var edi = await BoardAsync(admin, "&source=EDI", ct);
        Assert.All(edi.Items, r => Assert.Equal("EDI", r.Hold.Source));
        Assert.Contains(edi.Items, r => r.Hold.ContainerNo == InLcbYard);
        var lcb = await BoardAsync(admin, $"&branchId={SctLcb01}", ct);
        Assert.All(lcb.Items, r => Assert.Equal(SctLcb01, r.DepotBranchId));
        Assert.DoesNotContain(lcb.Items, r => r.Hold.ContainerNo == InNoYard);

        // History: released rows, most recent first, never releasable again.
        var released = await BoardAsync(admin, "&status=RELEASED", ct);
        Assert.Contains(released.Items, r => r.Hold.HoldCode == "LINE_STOP");
        Assert.All(released.Items, r => Assert.False(r.Hold.IsActive || r.CanRelease));
    }

    [Fact]
    public async Task A_hold_placed_now_is_on_the_board_and_released_from_it_with_its_row_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(TosApiFactory.SctOwner);
        var box = TestBox();
        try
        {
            var before = (await admin.GetFromJsonAsync<HoldSummaryResponse>(Summary, ct))!;
            var applied = await admin.PostAsJsonAsync("/api/tos/holds", new { containerNo = box, holdCode = "OPS_HOLD", reason = "Board test" }, ct);
            Assert.Equal(HttpStatusCode.Created, applied.StatusCode);

            var row = Assert.Single((await BoardAsync(admin, $"&search={box}", ct)).Items);
            Assert.Equal((0, true, (Guid?)null, "MANUAL"), (row.AgeDays, row.CanRelease, row.DepotBranchId, row.Hold.Source));

            var after = (await admin.GetFromJsonAsync<HoldSummaryResponse>(Summary, ct))!;
            Assert.Equal(before.Active + 1, after.Active);
            Assert.Equal(before.Ageing.Single(a => a.Key == "UNDER_1_DAY").Count + 1, after.Ageing.Single(a => a.Key == "UNDER_1_DAY").Count);

            var release = await admin.PostAsJsonAsync($"/api/tos/holds/{row.Hold.ContainerHoldId}/release",
                new { reason = "Board test release", rowVersion = row.Hold.RowVersion }, ct);
            Assert.Equal(HttpStatusCode.OK, release.StatusCode);

            Assert.Empty((await BoardAsync(admin, $"&search={box}", ct)).Items);
            var history = Assert.Single((await BoardAsync(admin, $"&status=RELEASED&search={box}", ct)).Items);
            Assert.False(history.CanRelease);
        }
        finally { await TestDatabase.RemoveHoldsAsync(box); }
    }

    [Fact]
    public async Task Summary_counts_add_up_to_the_active_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(TosApiFactory.SctOwner);

        var summary = (await admin.GetFromJsonAsync<HoldSummaryResponse>(Summary, ct))!;
        var board = await BoardAsync(admin, "", ct);

        Assert.Equal(board.TotalCount, summary.Active);
        Assert.Equal(summary.Active, summary.ByHold.Sum(c => c.Count));
        Assert.Equal(summary.Active, summary.ByHoldType.Sum(c => c.Count));
        Assert.Equal(summary.Active, summary.ByBlockingScope.Sum(c => c.Count));
        Assert.Equal(summary.Active, summary.BySource.Sum(c => c.Count));
        Assert.Equal(summary.Active, summary.ByDepot.Sum(c => c.Count));
        Assert.Equal(summary.Active, summary.Ageing.Sum(c => c.Count));
        Assert.Contains(summary.ByDepot, d => d.BranchCode == "SCT-LCB01");
        Assert.Contains(summary.ByDepot, d => d.BranchId == null);
        Assert.Equal(14, summary.ReleasedPerDay.Count);
        Assert.Equal(30, (await admin.GetFromJsonAsync<HoldSummaryResponse>($"{Summary}?days=30", ct))!.ReleasedPerDay.Count);

        var lcb = (await admin.GetFromJsonAsync<HoldSummaryResponse>($"{Summary}?branchId={SctLcb01}", ct))!;
        Assert.Equal((await BoardAsync(admin, $"&branchId={SctLcb01}", ct)).TotalCount, lcb.Active);
    }

    [Fact]
    public async Task Bad_filters_are_refused_on_the_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(TosApiFactory.SctOwner);

        await AssertFieldAsync(await admin.GetAsync($"{Board}?status=ALL", ct), "status", ct);
        await AssertFieldAsync(await admin.GetAsync($"{Board}?source=FAX", ct), "source", ct);
        await AssertFieldAsync(await admin.GetAsync($"{Summary}?days=0", ct), "days", ct);
        await AssertFieldAsync(await admin.GetAsync($"{Summary}?days=91", ct), "days", ct);
    }

    /// <summary>
    /// A gate clerk at LKR01 sees the holds that can reach their gate — a box in no
    /// yard — and not the holds at LCB01, whether on a box standing there or on an
    /// LCB01 booking. Naming LCB01 is a 403.
    /// </summary>
    [Fact]
    public async Task A_branch_scoped_clerk_sees_their_depot_and_the_boxes_in_no_yard()
    {
        var ct = TestContext.Current.CancellationToken;
        var lkr = await api.ClientForAsync(GateLkr);
        var lcb = await api.ClientForAsync(TosApiFactory.SctGateLcb);

        var theirs = await BoardAsync(lkr, "", ct);
        Assert.Contains(theirs.Items, r => r.Hold.ContainerNo == InNoYard);
        Assert.DoesNotContain(theirs.Items, r => r.Hold.ContainerNo == InLcbYard);
        Assert.DoesNotContain(theirs.Items, r => r.Hold.HoldCode == "CREDIT");
        Assert.All(theirs.Items, r => Assert.True(r.DepotBranchId is null));
        var theirSummary = (await lkr.GetFromJsonAsync<HoldSummaryResponse>(Summary, ct))!;
        Assert.Equal(theirs.TotalCount, theirSummary.Active);
        Assert.DoesNotContain(theirSummary.ByDepot, d => d.BranchId == SctLcb01);

        Assert.Equal(HttpStatusCode.Forbidden, (await lkr.GetAsync($"{Board}?branchId={SctLcb01}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lkr.GetAsync($"{Summary}?branchId={SctLcb01}", ct)).StatusCode);

        // The LCB01 clerk sees it, but may not lift it — and the release endpoint agrees.
        var mine = Assert.Single((await BoardAsync(lcb, "", ct)).Items, r => r.Hold.ContainerNo == InLcbYard);
        Assert.False(mine.CanRelease);
        var refused = await lcb.PostAsJsonAsync($"/api/tos/holds/{mine.Hold.ContainerHoldId}/release",
            new { reason = "Clerk should not", releaseRef = "X-1", rowVersion = mine.Hold.RowVersion }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Another_tenant_sees_none_of_it_and_anonymous_callers_nothing_at_all()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(TosApiFactory.SctOwner);
        var sss = await api.ClientForAsync(TosApiFactory.SssOwner);

        var sct = (await BoardAsync(admin, "", ct)).Items.Concat((await BoardAsync(admin, "&status=RELEASED", ct)).Items)
            .Select(r => r.Hold.ContainerHoldId).ToHashSet();
        var theirs = (await BoardAsync(sss, "", ct)).Items.Concat((await BoardAsync(sss, "&status=RELEASED", ct)).Items);
        Assert.DoesNotContain(theirs, r => sct.Contains(r.Hold.ContainerHoldId));
        Assert.Empty((await BoardAsync(sss, $"&search={InLcbYard}", ct)).Items);

        var summary = (await sss.GetFromJsonAsync<HoldSummaryResponse>(Summary, ct))!;
        Assert.DoesNotContain(summary.ByDepot, d => d.BranchId == SctLcb01);

        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Board, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Summary, ct)).StatusCode);
    }
}
