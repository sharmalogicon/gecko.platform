using System.Net;
using System.Net.Http.Json;
using Gecko.Data;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Vessels;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Branch scoping (PLAN Q11). The `bpm` claim lets depot staff in at all; these
/// tests are the other half — what the door does NOT decide.
///
/// The fixture users this leans on:
///   ops.lcb@sct.co.th   OPS_MANAGER at SCT-LCB01 (view + manage + cancel)
///                       VIEWER      at SCT-LKR01 (view only)
///   gate1.lcb@sct.co.th GATE_CLERK  at SCT-LCB01 (view only, no manage anywhere)
///   admin@sct.co.th     TENANT_OWNER, tenant-wide
/// so SCT's third branch, SCT-BKK01, is outside ops.lcb's access entirely — and
/// LKR01 is readable but not writable, which is the case a single "my branches"
/// list would get wrong.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class BranchScopeApiTests(TosApiFactory api)
{
    private const string Bookings = "/api/tos/bookings";
    private const string Calls = "/api/tos/vessel-calls";

    private const string BkkBooking = "BK-SCT-BKK01-2609-00001";
    private const string LkrBooking = "BK-SCT-LKR01-2609-00009";

    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly Guid SctBkk01 = Guid.Parse("775AE785-33A5-F111-9B0D-00919E4766D5");

    private static async Task<BookingSummaryResponse> FindAsync(HttpClient client, string orderNo, CancellationToken ct) =>
        (await client.GetFromJsonAsync<PagedResult<BookingSummaryResponse>>($"{Bookings}?search={orderNo}", ct))!.Items.Single();

    [Fact]
    public async Task A_branch_scoped_user_lists_only_the_branches_their_permission_covers()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(TosApiFactory.SctOwner);
        var ops = await api.ClientForAsync(TosApiFactory.SctOpsLcb);

        // Searched, not paged: the gate-day fixture put hundreds of bookings in
        // front of these, and a test that depends on page 1 is a test that breaks
        // the day the depot gets busy.
        var all = (await admin.GetFromJsonAsync<PagedResult<BookingSummaryResponse>>($"{Bookings}?pageSize=200", ct))!;
        var mine = (await ops.GetFromJsonAsync<PagedResult<BookingSummaryResponse>>($"{Bookings}?pageSize=200", ct))!;
        var minesBkk = (await ops.GetFromJsonAsync<PagedResult<BookingSummaryResponse>>($"{Bookings}?search={BkkBooking}", ct))!;
        var minesLkr = (await ops.GetFromJsonAsync<PagedResult<BookingSummaryResponse>>($"{Bookings}?search={LkrBooking}", ct))!;

        Assert.Contains(all.Items, b => b.BranchCode == "SCT-LCB01");
        Assert.Single(minesLkr.Items);                                    // VIEWER at LKR01 still sees it
        Assert.Empty(minesBkk.Items);                                     // no grant at BKK01 at all
        Assert.DoesNotContain(mine.Items, b => b.BranchCode == "SCT-BKK01");
        Assert.True(mine.TotalCount < all.TotalCount);
    }

    [Fact]
    public async Task Another_branch_reads_as_not_found_and_a_branch_you_only_view_cannot_be_changed()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(TosApiFactory.SctOwner);
        var ops = await api.ClientForAsync(TosApiFactory.SctOpsLcb);

        // No grant at BKK01: the row reads as absent, the same answer another tenant gets.
        var bkk = await FindAsync(admin, BkkBooking, ct);
        Assert.Equal(HttpStatusCode.NotFound, (await ops.GetAsync($"{Bookings}/{bkk.BookingId}", ct)).StatusCode);

        // VIEWER at LKR01: readable...
        var lkr = await FindAsync(ops, LkrBooking, ct);
        var detail = (await ops.GetFromJsonAsync<BookingDetailResponse>($"{Bookings}/{lkr.BookingId}", ct))!;

        // ...and not writable. The scope check runs BEFORE the rowVersion check, so a
        // caller who may not touch the row never learns whether their copy was current.
        var update = await ops.PutAsJsonAsync($"{Bookings}/{lkr.BookingId}", new
        {
            branchId = detail.Booking.BranchId,
            orderTypeCode = detail.Booking.OrderTypeCode,
            lineCode = detail.Booking.LineCode,
            remarks = "should never be written",
            rowVersion = detail.Booking.RowVersion,
        }, ct);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
    }

    [Fact]
    public async Task A_booking_cannot_be_raised_at_a_branch_outside_your_access()
    {
        var ct = TestContext.Current.CancellationToken;
        var ops = await api.ClientForAsync(TosApiFactory.SctOpsLcb);

        var refused = await ops.PostAsJsonAsync(Bookings, new
        {
            branchId = SctBkk01,
            orderTypeCode = "IMP CY/CY",
            lineCode = "MAEU",
            customerCode = "CUS-TAE",
            carrierRef = "ZZ-SCOPE-BKK",
            requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
        }, ct);

        // 403 with the branch named, not 404: the caller typed the branch themselves.
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("SCT-BKK01", await refused.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_gate_clerk_reads_bookings_but_holds_no_manage_anywhere()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = await api.ClientForAsync(TosApiFactory.SctGateLcb);

        var list = await gate.GetAsync(Bookings, ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var refused = await gate.PostAsJsonAsync(Bookings, new
        {
            branchId = SctLcb01,
            orderTypeCode = "IMP CY/CY",
            lineCode = "MAEU",
            requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
        }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>
    /// The schedule is TENANT data — the ship calling Laem Chabang is the same ship for
    /// every depot — so a branch-scoped grant reads all of it. Before the bpm claim this
    /// same call was a 403 for ops.lcb.
    /// </summary>
    [Fact]
    public async Task The_schedule_is_tenant_wide_for_a_branch_scoped_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(TosApiFactory.SctOwner);
        var ops = await api.ClientForAsync(TosApiFactory.SctOpsLcb);

        var all = (await admin.GetFromJsonAsync<PagedResult<VesselCallSummaryResponse>>($"{Calls}?pageSize=200", ct))!;
        var mine = (await ops.GetFromJsonAsync<PagedResult<VesselCallSummaryResponse>>($"{Calls}?pageSize=200", ct))!;

        Assert.NotEmpty(mine.Items);
        Assert.Equal(all.TotalCount, mine.TotalCount);
    }

    /// <summary>
    /// The one thing a branch-scoped manager may not do to the shared schedule: write
    /// another depot's cut-off row — or silently delete it by leaving it out of a
    /// "replace the set" payload.
    /// </summary>
    [Fact]
    public async Task A_branch_scoped_manager_cannot_set_another_branch_s_cutoff()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(TosApiFactory.SctOwner);
        var ops = await api.ClientForAsync(TosApiFactory.SctOpsLcb);

        var call = (await admin.GetFromJsonAsync<PagedResult<VesselCallSummaryResponse>>($"{Calls}?pageSize=1", ct))!.Items.Single();
        var before = (await admin.GetFromJsonAsync<VesselCallDetailResponse>($"{Calls}/{call.VesselCallId}", ct))!;

        var refused = await ops.PutAsJsonAsync($"{Calls}/{call.VesselCallId}/cutoffs", new
        {
            cutoffs = new object[]
            {
                new { kind = "PORT_DRY", at = before.Call.Etd.AddHours(-36) },
                new { kind = "YARD_DRY", branchId = SctBkk01, at = before.Call.Etd.AddHours(-60) },
            },
        }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // Refused whole: the PORT_DRY row in the same payload was not written either.
        var after = (await admin.GetFromJsonAsync<VesselCallDetailResponse>($"{Calls}/{call.VesselCallId}", ct))!;
        Assert.Equal(
            before.Cutoffs.Select(c => (c.Kind, c.BranchId, c.At)).OrderBy(c => c.Kind).ToList(),
            after.Cutoffs.Select(c => (c.Kind, c.BranchId, c.At)).OrderBy(c => c.Kind).ToList());
    }
}
