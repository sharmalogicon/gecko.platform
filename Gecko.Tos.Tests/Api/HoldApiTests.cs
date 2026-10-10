using System.Net;
using System.Net.Http.Json;
using Gecko.Data;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Holds;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Holds through the real host (PLAN §4.3, D-6, batch C). What is being proved is
/// that a hold is a ROW, not a bit: it has an author, a reason, a release, and a
/// release AUTHORITY that decides who may lift it.
///
/// Fixture holds (dev_02): CUSTOMS on AKLU6006714 (EDI), CREDIT on the import D/O
/// booking, CSC_EXP on AMCU9300423, DAMAGE on EGHU9122888 (AUTO, from a survey),
/// plus a released LINE_STOP and OPS_HOLD.
///
/// Test holds go on numbers starting ZZTU and are removed in finally.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class HoldApiTests(TosApiFactory api)
{
    private const string Holds = "/api/tos/holds";

    /// <summary>On fixture booking BK-SCT-LCB01-2609-00002, which carries the CREDIT hold.</summary>
    private const string BoxOnHeldBooking = "MRKU4122335";

    private const string CustomsHeldBox = "AKLU6006714";

    /// <summary>A well-formed number with a correct check digit that no fixture uses.</summary>
    private static string TestBox()
    {
        var ten = $"ZZTU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    private static async Task<HoldResponse> ApplyAsync(HttpClient client, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(Holds, body, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"apply returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<HoldResponse>(ct))!;
    }

    [Fact]
    public async Task Fixture_holds_read_back_with_the_master_data_that_governs_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);

        var active = (await client.GetFromJsonAsync<PagedResult<HoldResponse>>($"{Holds}?pageSize=200", ct))!;

        var customs = Assert.Single(active.Items, h => h.ContainerNo == CustomsHeldBox && h.HoldCode == "CUSTOMS");
        Assert.Equal(("ALL", "CUSTOMS", "EDI", true), (customs.BlockingScope, customs.ReleaseAuthority, customs.Source, customs.IsActive));
        Assert.Null(customs.BookingId);

        // A hold placed on a BOOKING names the booking and its depot.
        var credit = Assert.Single(active.Items, h => h.HoldCode == "CREDIT");
        Assert.Equal(("BK-SCT-LCB01-2609-00002", "SCT-LCB01", "DEPOT_FINANCE"), (credit.OrderNo, credit.BranchCode, credit.ReleaseAuthority));
        Assert.Null(credit.ContainerNo);

        // A released hold is history, not an absence: it keeps its reason and its releaser.
        var released = (await client.GetFromJsonAsync<PagedResult<HoldResponse>>($"{Holds}?status=RELEASED&pageSize=200", ct))!;
        var lineStop = Assert.Single(released.Items, h => h.HoldCode == "LINE_STOP");
        Assert.False(lineStop.IsActive);
        Assert.NotNull(lineStop.ReleasedAt);
        Assert.False(string.IsNullOrWhiteSpace(lineStop.ReleaseReason));
        Assert.DoesNotContain(active.Items, h => h.ContainerHoldId == lineStop.ContainerHoldId);
    }

    /// <summary>
    /// The barrier's question. A box is held by its own holds AND by the holds on the
    /// booking it is working — Vector's bit could express neither.
    /// </summary>
    [Fact]
    public async Task What_stops_this_box_answers_for_the_box_and_for_its_booking()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);

        var viaBooking = (await client.GetFromJsonAsync<ContainerHoldsResponse>($"/api/tos/containers/{BoxOnHeldBooking}/holds", ct))!;
        Assert.True(viaBooking.IsHeld);
        var credit = Assert.Single(viaBooking.Holds, h => h.HoldCode == "CREDIT");
        Assert.Equal(("BOOKING", "BK-SCT-LCB01-2609-00002", "GATE_OUT"), (credit.HeldVia, credit.OrderNo, credit.BlockingScope));

        var viaBox = (await client.GetFromJsonAsync<ContainerHoldsResponse>($"/api/tos/containers/{CustomsHeldBox}/holds", ct))!;
        Assert.Equal("CONTAINER", Assert.Single(viaBox.Holds).HeldVia);

        var free = (await client.GetFromJsonAsync<ContainerHoldsResponse>($"/api/tos/containers/{TestBox()}/holds", ct))!;
        Assert.False(free.IsHeld);
        Assert.Empty(free.Holds);

        var nonsense = await client.GetAsync("/api/tos/containers/NOT_A_BOX/holds", ct);
        Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
    }

    [Fact]
    public async Task A_hold_is_applied_and_released_with_a_person_and_a_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var box = TestBox();
        try
        {
            var hold = await ApplyAsync(client, new
            {
                containerNo = box,
                holdCode = "OPS_HOLD",
                reason = "Stacked under a DG row pending restow",
            }, ct);

            Assert.Equal((box, "MANUAL", true), (hold.ContainerNo, hold.Source, hold.IsActive));
            Assert.NotNull(hold.AppliedBy);

            // A box can be held before it ever arrives — that is why holds live here
            // and not on a yard row (DV-6).
            var seen = (await client.GetFromJsonAsync<ContainerHoldsResponse>($"/api/tos/containers/{box}/holds", ct))!;
            Assert.True(seen.IsHeld);

            // Same hold twice would hide the first one's reason behind a second row.
            var again = await client.PostAsJsonAsync(Holds, new { containerNo = box, holdCode = "OPS_HOLD", reason = "again" }, ct);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

            var released = await client.PostAsJsonAsync($"{Holds}/{hold.ContainerHoldId}/release",
                new { reason = "Restowed; box is accessible", rowVersion = hold.RowVersion }, ct);
            Assert.Equal(HttpStatusCode.OK, released.StatusCode);
            var after = (await released.Content.ReadFromJsonAsync<HoldResponse>(ct))!;
            Assert.False(after.IsActive);
            Assert.Equal("MANUAL", after.ReleaseSource);
            Assert.NotNull(after.ReleasedBy);

            // Releasing twice is a conflict, not a second release.
            var twice = await client.PostAsJsonAsync($"{Holds}/{hold.ContainerHoldId}/release",
                new { reason = "again", rowVersion = after.RowVersion }, ct);
            Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);

            // And the box is free: the release is what clears it, nothing else.
            var clear = (await client.GetFromJsonAsync<ContainerHoldsResponse>($"/api/tos/containers/{box}/holds", ct))!;
            Assert.False(clear.IsHeld);
        }
        finally { await TestDatabase.RemoveHoldsAsync(box); }
    }

    /// <summary>
    /// Deleting a hold TYPE in master data must not free the boxes that carry it.
    /// The type goes out of use — no new holds of it — but a hold already on a box
    /// still stops the moves its scope covers, and can still be released by the
    /// authority it was placed under. Before, the definition vanished with the
    /// soft delete: the barrier read no scope (so no block) and release answered
    /// 409 "restore the hold type first" — which master data cannot do.
    /// </summary>
    [Fact]
    public async Task A_deleted_hold_type_still_holds_the_boxes_that_carry_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var box = TestBox();
        var code = $"ZZ_DEL_{Random.Shared.Next(1000, 9999)}";
        var type = $"/api/master/holds/{code}";
        try
        {
            var created = await client.PostAsJsonAsync("/api/master/holds", new
            {
                holdCode = code, descriptionEn = "Deleted-type test", holdType = "OPERATIONS",
                blockingScope = "ALL", releaseAuthority = "SUPERVISOR", priority = 3,
            }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var hold = await ApplyAsync(client, new { containerNo = box, holdCode = code, reason = "Held before the type was retired" }, ct);

            var typeVersion = (await created.Content.ReadFromJsonAsync<HoldTypeRow>(ct))!.RowVersion;
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{type}?rowVersion={Uri.EscapeDataString(typeVersion)}", ct)).StatusCode);

            // Still held, and still with what it blocks — that is what the barrier reads.
            var seen = (await client.GetFromJsonAsync<ContainerHoldsResponse>($"/api/tos/containers/{box}/holds", ct))!;
            Assert.True(seen.IsHeld);
            var active = Assert.Single(seen.Holds, h => h.HoldCode == code);
            Assert.Equal(("ALL", "SUPERVISOR"), (active.BlockingScope, active.ReleaseAuthority));

            // Out of use: nobody places a new hold of a deleted type.
            var other = TestBox();
            var refused = await client.PostAsJsonAsync(Holds, new { containerNo = other, holdCode = code, reason = "new" }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

            // And the one on the box can still be lifted, by the authority it was placed under.
            var released = await client.PostAsJsonAsync($"{Holds}/{hold.ContainerHoldId}/release",
                new { reason = "Type retired; box cleared", rowVersion = hold.RowVersion }, ct);
            Assert.True(released.StatusCode == HttpStatusCode.OK,
                $"release returned {(int)released.StatusCode}: {await released.Content.ReadAsStringAsync(ct)}");
        }
        finally
        {
            await TestDatabase.RemoveHoldsAsync(box);
            // In case the test stopped before the delete.
            if (await client.GetAsync(type, ct) is { StatusCode: HttpStatusCode.OK } live)
            {
                var version = (await live.Content.ReadFromJsonAsync<HoldTypeRow>(ct))!.RowVersion;
                await client.DeleteAsync($"{type}?rowVersion={Uri.EscapeDataString(version)}", ct);
            }
        }
    }

    private sealed record HoldTypeRow(string HoldCode, string RowVersion);

    /// <summary>
    /// The rule that makes a hold worth having (PLAN §10.7): WHO may lift it comes
    /// from the MDM hold type, not from whoever has the screen open. OPS_MANAGER
    /// holds tos.hold.release.operations and .line — not .finance.
    /// </summary>
    [Fact]
    public async Task A_finance_hold_is_not_operations_to_lift()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(TosApiFactory.SctOwner);
        var ops = await api.ClientForAsync(TosApiFactory.SctOpsLcb);
        var box = TestBox();
        try
        {
            var hold = await ApplyAsync(admin, new { containerNo = box, holdCode = "CREDIT", reason = "Customer over credit limit" }, ct);

            var refused = await ops.PostAsJsonAsync($"{Holds}/{hold.ContainerHoldId}/release",
                new { reason = "customer says they paid", rowVersion = hold.RowVersion }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var body = await refused.Content.ReadAsStringAsync(ct);
            Assert.Contains("DEPOT_FINANCE", body, StringComparison.Ordinal);
            Assert.Contains("tos.hold.release.finance", body, StringComparison.Ordinal);

            // Still active — a refused release leaves nothing behind.
            var still = (await admin.GetFromJsonAsync<HoldResponse>($"{Holds}/{hold.ContainerHoldId}", ct))!;
            Assert.True(still.IsActive);

            var allowed = await admin.PostAsJsonAsync($"{Holds}/{hold.ContainerHoldId}/release",
                new { reason = "Payment received, receipt RC-2026-0918", rowVersion = still.RowVersion }, ct);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        finally { await TestDatabase.RemoveHoldsAsync(box); }
    }

    /// <summary>
    /// CUSTOMS and MNR are not the depot's to decide. Until the EDI and MnR modules
    /// own them, operations may RECORD the release — but only by quoting the document
    /// it is acting on, so the paper is named on the row (HoldRules, PLAN Q-C1).
    /// </summary>
    [Fact]
    public async Task A_customs_release_has_to_name_the_document_behind_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var box = TestBox();
        try
        {
            var hold = await ApplyAsync(client, new
            {
                containerNo = box,
                holdCode = "CUSTOMS",
                reason = "Red line: selected for physical inspection",
                externalRef = "A0112026091800345",
            }, ct);
            Assert.Equal("A0112026091800345", hold.ExternalRef);

            var bare = await client.PostAsJsonAsync($"{Holds}/{hold.ContainerHoldId}/release",
                new { reason = "customs say it is cleared", rowVersion = hold.RowVersion }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
            Assert.Contains("customs release reference", await bare.Content.ReadAsStringAsync(ct), StringComparison.OrdinalIgnoreCase);

            var withRef = await client.PostAsJsonAsync($"{Holds}/{hold.ContainerHoldId}/release", new
            {
                reason = "Customs release issued",
                releaseRef = "A0212026092100987",
                rowVersion = hold.RowVersion,
            }, ct);
            Assert.Equal(HttpStatusCode.OK, withRef.StatusCode);
            Assert.Equal("A0212026092100987", (await withRef.Content.ReadFromJsonAsync<HoldResponse>(ct))!.ReleaseRef);
        }
        finally { await TestDatabase.RemoveHoldsAsync(box); }
    }

    [Fact]
    public async Task A_hold_needs_exactly_one_target_a_real_hold_type_and_a_booking_worth_holding()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var box = TestBox();

        var neither = await client.PostAsJsonAsync(Holds, new { holdCode = "OPS_HOLD", reason = "nothing to hold" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);

        var both = await client.PostAsJsonAsync(Holds, new
        {
            containerNo = box,
            bookingId = Guid.NewGuid(),
            holdCode = "OPS_HOLD",
            reason = "a box and an order are different statements",
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);

        var unknownType = await client.PostAsJsonAsync(Holds, new { containerNo = box, holdCode = "NOPE", reason = "not a hold type" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknownType.StatusCode);
        Assert.Contains("master data", await unknownType.Content.ReadAsStringAsync(ct), StringComparison.OrdinalIgnoreCase);

        var mistyped = await client.PostAsJsonAsync(Holds, new { containerNo = "AKLU6006715", holdCode = "OPS_HOLD", reason = "wrong check digit" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, mistyped.StatusCode);
        Assert.Contains("check digit", await mistyped.Content.ReadAsStringAsync(ct), StringComparison.OrdinalIgnoreCase);

        // A cancelled booking stops nothing, so holding it says nothing.
        var cancelled = (await client.GetFromJsonAsync<PagedResult<BookingSummaryResponseLite>>(
            "/api/tos/bookings?status=CANCELLED&pageSize=1", ct))!.Items.Single();
        var onCancelled = await client.PostAsJsonAsync(Holds, new
        {
            bookingId = cancelled.BookingId,
            holdCode = "CREDIT",
            reason = "unpaid charges on a cancelled order",
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, onCancelled.StatusCode);
        Assert.Contains("CANCELLED", await onCancelled.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    /// <summary>Only the fields this file needs from a booking summary.</summary>
    private sealed record BookingSummaryResponseLite(Guid BookingId, string OrderNo, string Status);
}
