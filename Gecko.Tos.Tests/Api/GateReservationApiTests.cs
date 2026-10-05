using System.Net;
using System.Net.Http.Json;
using Gecko.Data;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Owner 2026-10-04 (GATE_IN_BIG_SAVE.md §1): Record holds a box for the truck being keyed. Another
/// truck's clerk does not see it in the picker, cannot raise a blind order for it and cannot gate it;
/// the same draft holding it again is the same hold. A hold lapses by itself, is released when the row
/// is removed, and is used up when the box goes through. A retried gate move is the same EIR.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class GateReservationApiTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private const string Bookings = "/api/tos/bookings";
    private const string Prefix = "ZZR-";

    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    /// <summary>Free SCT registry boxes, not on any fixture booking (BoxA/BoxB are 20GP, BoxC a 40GP).</summary>
    private const string BoxA = "AKLU6018567", BoxB = "AKLU6019856", BoxC = "APZU4230891";

    private static string NewRef() => $"{Prefix}{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static async Task<BookingDetailResponse> BookAsync(HttpClient client, string carrierRef, CancellationToken ct, params string[] boxes)
    {
        var created = await client.PostAsJsonAsync(Bookings, new
        {
            branchId = SctLcb01, orderTypeCode = "IMP CY/CY", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,   // FULL_IN first
            requirements = new object[] { new { equipmentTypeCode = "20GP", qty = boxes.Length } },
            containers = boxes.Select(b => new { containerNo = b }).ToArray(),
        }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!;
    }

    private static Task<HttpResponseMessage> HoldAsync(HttpClient client, Guid draftId, Guid? bookingContainerId, CancellationToken ct, string? containerNo = null) =>
        client.PostAsJsonAsync($"{Gate}/reservations", new { branchId = SctLcb01, draftId, bookingContainerId, containerNo }, ct);

    private static object GateIn(string containerNo, Guid? draftId, string plate = "70-5511") => new
    {
        branchId = SctLcb01, containerNo, direction = "IN", draftId,
        tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 18000m, customsPermitNo = "ZZ-PERMIT-1",
        truck = new { plate, driverName = "Somchai P." },
        grossWeightKg = 20200m, weightSource = "WEIGHBRIDGE",
        seals = new object[] { new { sealNo = $"ZZ-{containerNo[^4..]}", sealType = "LINE", isIntact = true } },
    };

    [Fact]
    public async Task A_held_box_is_hidden_from_other_trucks_and_the_same_draft_gets_the_same_hold()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var (mine, theirs) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            var booking = await BookAsync(client, carrierRef, ct, BoxA, BoxB);
            var lineA = booking.Containers.Single(c => c.ContainerNo == BoxA).BookingContainerId;

            var first = await HoldAsync(client, mine, lineA, ct);
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            var hold = (await first.Content.ReadFromJsonAsync<BoxReservationResponse>(ct))!;
            Assert.Equal((BoxA, lineA), (hold.ContainerNo, hold.BookingContainerId!.Value));
            Assert.True(hold.ExpiresAt > hold.ReservedAt.AddMinutes(14));

            // Record pressed again (or the answer was lost): the same hold, extended.
            var again = await HoldAsync(client, mine, lineA, ct);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            var renewed = (await again.Content.ReadFromJsonAsync<BoxReservationResponse>(ct))!;
            Assert.Equal(hold.BoxReservationId, renewed.BoxReservationId);
            Assert.True(renewed.ExpiresAt >= hold.ExpiresAt);

            // Another truck: refused by number too (the number finds the booked line), and not offered in its picker.
            var other = await HoldAsync(client, theirs, null, ct, containerNo: BoxA);
            Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
            Assert.Contains("BOX_RESERVED", await other.Content.ReadAsStringAsync(ct));

            async Task<List<string?>> PickAsync(Guid draft) =>
                (await client.GetFromJsonAsync<PagedResult<BookableBoxResponse>>(
                    $"{Gate}/bookable-boxes?branchId={SctLcb01}&search={carrierRef}&draftId={draft}", ct))!.Items.Select(b => b.ContainerNo).ToList();
            Assert.Equal([BoxB], await PickAsync(theirs));
            Assert.Equal([BoxA, BoxB], (await PickAsync(mine)).Order());

            // The screen reloads: its holds come back.
            var reloaded = (await client.GetFromJsonAsync<List<BoxReservationResponse>>($"{Gate}/reservations?branchId={SctLcb01}&draftId={mine}", ct))!;
            Assert.Equal(hold.BoxReservationId, Assert.Single(reloaded).BoxReservationId);

            // The barrier says so to the other truck, and refuses its move.
            var preflight = (await client.GetFromJsonAsync<GatePreflightResponse>(
                $"{Gate}/preflight?branchId={SctLcb01}&containerNo={BoxA}&direction=IN&draftId={theirs}", ct))!;
            Assert.Contains(preflight.Findings, f => f.Code == "BOX_RESERVED");
            var refused = await client.PostAsJsonAsync($"{Gate}/transactions", GateIn(BoxA, theirs), ct);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("another truck", await refused.Content.ReadAsStringAsync(ct));

            // Removing the row frees it (twice is fine); then the other truck may take it.
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Gate}/reservations/{hold.BoxReservationId}?draftId={mine}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Gate}/reservations/{hold.BoxReservationId}?draftId={mine}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await HoldAsync(client, theirs, lineA, ct)).StatusCode);
        }
        finally
        {
            await TestDatabase.RemoveBoxReservationsAsync(BoxA, BoxB);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    [Fact]
    public async Task A_lapsed_hold_frees_the_box_and_a_gated_box_uses_its_hold_up()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var (mine, theirs) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            var booking = await BookAsync(client, carrierRef, ct, BoxA);
            var lineA = booking.Containers.Single().BookingContainerId;

            Assert.Equal(HttpStatusCode.Created, (await HoldAsync(client, theirs, lineA, ct)).StatusCode);
            await TestDatabase.LapseBoxReservationsAsync(BoxA);   // that screen was closed 15 minutes ago
            Assert.Equal(HttpStatusCode.Created, (await HoldAsync(client, mine, lineA, ct)).StatusCode);

            var eir = await client.PostAsJsonAsync($"{Gate}/transactions", GateIn(BoxA, mine), ct);
            Assert.True(eir.StatusCode == HttpStatusCode.Created, $"gate returned {(int)eir.StatusCode}: {await eir.Content.ReadAsStringAsync(ct)}");
            Assert.Empty((await client.GetFromJsonAsync<List<BoxReservationResponse>>($"{Gate}/reservations?branchId={SctLcb01}&draftId={mine}", ct))!);
        }
        finally
        {
            await TestDatabase.RemoveBoxReservationsAsync(BoxA);
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    [Fact]
    public async Task A_retried_gate_move_answers_with_the_same_EIR_and_a_held_box_gets_no_blind_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            await BookAsync(client, carrierRef, ct, BoxB);
            var key = Guid.NewGuid().ToString();

            async Task<HttpResponseMessage> PostAsync(object body)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{Gate}/transactions") { Content = JsonContent.Create(body) };
                request.Headers.Add(Idempotency.Header, key);
                return await client.SendAsync(request, ct);
            }

            var first = await PostAsync(GateIn(BoxB, null));
            Assert.True(first.StatusCode == HttpStatusCode.Created, $"gate returned {(int)first.StatusCode}: {await first.Content.ReadAsStringAsync(ct)}");
            var eir = (await first.Content.ReadFromJsonAsync<GateTransactionResponse>(ct))!;

            var retry = await PostAsync(GateIn(BoxB, null));
            Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
            var replayed = (await retry.Content.ReadFromJsonAsync<GateTransactionResponse>(ct))!;
            Assert.Equal((eir.GateTransactionId, eir.EirNo), (replayed.GateTransactionId, replayed.EirNo));

            // The same key with another body is a client bug.
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(GateIn(BoxB, null, plate: "70-0000"))).StatusCode);

            // A box with no order that another truck holds: no blind order for it.
            Assert.Equal(HttpStatusCode.Created, (await HoldAsync(client, Guid.NewGuid(), null, ct, containerNo: BoxC)).StatusCode);
            var blind = await client.PostAsJsonAsync($"{Gate}/blind-orders", new
            {
                branchId = SctLcb01, containerNo = BoxC, lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef = carrierRef + "B", draftId = Guid.NewGuid(),
            }, ct);
            Assert.Equal(HttpStatusCode.Conflict, blind.StatusCode);
            Assert.Contains("another truck", await blind.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            await TestDatabase.RemoveBoxReservationsAsync(BoxB, BoxC);
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }
}
