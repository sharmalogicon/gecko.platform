using System.Net;
using System.Net.Http.Json;
using Gecko.Data;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Vessels;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Late gates approved before the truck arrives (PLAN §4.2, D-2, batch C).
///
/// Vector's `AllowLateGate` bit let 686 boxes through the yard cut-off in 2025 with
/// no name and no reason attached. These tests pin the three things that replace it:
/// an approval must be WORTH something (later than the cut-off it excepts), it must
/// not outlive the SHIP, and it can be withdrawn.
///
/// Test bookings carry a ZZ- carrier ref and are removed in finally, which takes
/// their approvals with them.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class CutoffExceptionApiTests(TosApiFactory api)
{
    private const string Bookings = "/api/tos/bookings";

    /// <summary>The fixture's pre-approved late gate (dev_02), now spent.</summary>
    private const string BookingWithFixtureException = "BK-SCT-LCB01-2609-00013";

    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewRef() => $"ZZ-{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static string Url(Guid bookingId) => $"{Bookings}/{bookingId}/cutoff-exceptions";

    [Fact]
    public async Task The_fixture_approval_reads_back_and_has_expired_on_its_own()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);

        var booking = (await client.GetFromJsonAsync<PagedResult<BookingSummaryResponse>>(
            $"{Bookings}?search={BookingWithFixtureException}", ct))!.Items.Single();

        var exceptions = (await client.GetFromJsonAsync<IReadOnlyList<CutoffExceptionResponse>>(Url(booking.BookingId), ct))!;

        var approval = Assert.Single(exceptions);
        Assert.Equal(("YARD_DG", BookingWithFixtureException), (approval.CutoffKind, approval.OrderNo));
        Assert.False(string.IsNullOrWhiteSpace(approval.Reason));

        // Nobody had to close it: an approval is a window, and the window has passed.
        Assert.False(approval.IsLive);
        Assert.Null(approval.RevokedAt);
    }

    [Fact]
    public async Task An_approval_has_to_beat_the_cut_off_and_must_not_outlive_the_ship()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        // Its own call, sailing in twelve days: a fixture call sails on a fixed date and the test would expire with it.
        var callRef = $"ZZ-{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var sails = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(12), TimeSpan.Zero).AddHours(15);
        var voyage = $"T{Random.Shared.Next(100000, 999999)}";
        try
        {
            var made = await client.PostAsJsonAsync("/api/tos/vessel-calls", new
            {
                callRef, vesselCode = "CHAOPHRAYA", portCode = "THLCH", terminalCode = "LCB-A0", operatorVoyageOut = voyage,
                eta = sails.AddHours(-30), etd = sails,
                lines = new object[] { new { lineCode = "ONEY", voyageOut = voyage + "O" } },
                cutoffs = new object[] { new { kind = "PORT_DRY", at = sails.AddHours(-36) }, new { kind = "YARD_DRY", at = sails.AddHours(-60) } },
            }, ct);
            Assert.True(made.StatusCode == HttpStatusCode.Created, await made.Content.ReadAsStringAsync(ct));
            var call = (await made.Content.ReadFromJsonAsync<VesselCallDetailResponse>(ct))!.Call;

            var created = await client.PostAsJsonAsync(Bookings, new
            {
                branchId = SctLcb01, orderTypeCode = "EXP CY/CY", lineCode = "ONEY", customerCode = "CUS-BKF", carrierRef,
                vesselCallId = call.VesselCallId, polPortCode = "THLCH", podPortCode = "SGSIN",
                requirements = new object[] { new { equipmentTypeCode = "40HC", qty = 2 } },
            }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var booking = (await created.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!.Booking;

            // The same function the gate will judge by — not a copied cut-off (D-2).
            var effective = (await client.GetFromJsonAsync<IReadOnlyList<EffectiveCutoffResponse>>(
                $"/api/tos/vessel-calls/{call.VesselCallId}/effective-cutoffs?lineCode=ONEY&branchId={SctLcb01}", ct))!;
            var yardDry = effective.Single(c => c.Kind == "YARD_DRY");
            var etd = booking.Etd!.Value;

            async Task<HttpResponseMessage> ApproveAsync(DateTimeOffset allowedUntil, string reason = "Line agreed a late gate") =>
                await client.PostAsJsonAsync(Url(booking.BookingId), new
                {
                    cutoffKind = "YARD_DRY",
                    allowedUntil,
                    reason,
                    carrierApprovalRef = "ONEY/LG/2026-0921",
                }, ct);

            // Earlier than the cut-off it excepts: it would let nothing through.
            var pointless = await ApproveAsync(yardDry.At.AddHours(-1));
            Assert.Equal(HttpStatusCode.BadRequest, pointless.StatusCode);
            Assert.Contains("grants nothing", await pointless.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

            // After the ship sails: Vector has 495 of these, signed by nobody.
            var afterSailing = await ApproveAsync(etd.AddHours(1));
            Assert.Equal(HttpStatusCode.BadRequest, afterSailing.StatusCode);
            Assert.Contains("sails at", await afterSailing.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

            var granted = await ApproveAsync(etd.AddHours(-12));
            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
            var approval = (await granted.Content.ReadFromJsonAsync<CutoffExceptionResponse>(ct))!;
            Assert.True(approval.IsLive);
            Assert.Equal(yardDry.At, approval.CutoffAt);            // what it was judged against, on the record
            Assert.Equal("ONEY/LG/2026-0921", approval.CarrierApprovalRef);

            // Two live approvals for the same cut-off leave it unclear which one applied.
            var second = await ApproveAsync(etd.AddHours(-6));
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

            var revoked = await client.PostAsJsonAsync($"{Url(booking.BookingId)}/{approval.CutoffExceptionId}/revoke",
                new { reason = "Line withdrew the late gate", rowVersion = approval.RowVersion }, ct);
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
            var after = (await revoked.Content.ReadFromJsonAsync<CutoffExceptionResponse>(ct))!;
            Assert.False(after.IsLive);
            Assert.NotNull(after.RevokedBy);

            var twice = await client.PostAsJsonAsync($"{Url(booking.BookingId)}/{approval.CutoffExceptionId}/revoke",
                new { reason = "again", rowVersion = after.RowVersion }, ct);
            Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);

            // Withdrawn, so the same approval can be given again if the line relents.
            Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(etd.AddHours(-6))).StatusCode);
        }
        finally { await TestDatabase.RemoveBookingsAsync(carrierRef); await TestDatabase.RemoveCallAsync(callRef); }
    }

    [Fact]
    public async Task A_booking_with_no_vessel_call_has_no_cut_off_to_be_late_for()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            var created = await client.PostAsJsonAsync(Bookings, new
            {
                branchId = SctLcb01, orderTypeCode = "IMP CY/CY", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
                requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
            }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var booking = (await created.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!.Booking;

            var refused = await client.PostAsJsonAsync(Url(booking.BookingId), new
            {
                cutoffKind = "YARD_DRY",
                allowedUntil = DateTimeOffset.UtcNow.AddDays(3),
                reason = "no call, no cut-off",
            }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("not on a vessel call", await refused.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }
        finally { await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    /// <summary>
    /// A late gate is a supervisor's decision, not a clerk's: tos.cutoff.override is
    /// granted to TENANT_OWNER and OPS_MANAGER only (16_tos_permissions.sql), and a
    /// gate clerk who could approve their own late gate is the bit all over again.
    /// </summary>
    [Fact]
    public async Task A_gate_clerk_cannot_approve_their_own_late_gate()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = await api.ClientForAsync(TosApiFactory.SctGateLcb);
        var owner = await api.ClientForAsync(TosApiFactory.SctOwner);

        var booking = (await owner.GetFromJsonAsync<PagedResult<BookingSummaryResponse>>(
            $"{Bookings}?search={BookingWithFixtureException}", ct))!.Items.Single();

        var refused = await gate.PostAsJsonAsync(Url(booking.BookingId), new
        {
            cutoffKind = "YARD_DRY",
            allowedUntil = DateTimeOffset.UtcNow.AddDays(1),
            reason = "the truck is already here",
        }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // ...and they can still READ the approvals that govern their gate.
        Assert.Equal(HttpStatusCode.OK, (await gate.GetAsync(Url(booking.BookingId), ct)).StatusCode);
    }
}
