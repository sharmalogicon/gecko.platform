using System.Net;
using System.Net.Http.Json;
using Gecko.Data;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Owner 2026-10-04 (web.tos docs/GATE_IN_VECTOR_PARITY_FOR_API.md §1–§2).
///
/// A box with no paperwork comes in on BLIND GATE IN, as on Vector's Gate In — but the
/// gate raises the order, not the booking page. Raise, pay, then gate: the blind order
/// is its own call so the window can price it before the barrier; a retry for the same
/// box gets the same order. The picker is a grid of BOXES with their next step, filtered
/// as the desktop's booking search filters.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class GateBlindOrderApiTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private const string Bookings = "/api/tos/bookings";
    private const string Prefix = "ZZB-";

    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    /// <summary>Free SCT registry boxes, not on any fixture booking.</summary>
    private const string BoxA = "AKLU6018567", BoxB = "AKLU6019856", BoxC = "APZU4230891";

    private static string NewRef() => $"{Prefix}{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static object Blind(string containerNo, string? carrierRef, string? customerCode = "CUS-TAE", string? equipmentTypeCode = "20GP") => new
    {
        branchId = SctLcb01, containerNo, lineCode = "MAEU", customerCode, equipmentTypeCode, carrierRef,
    };

    [Fact]
    public async Task The_gate_raises_a_blind_order_a_retry_gets_the_same_one_and_the_box_then_gates_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            // No type sent: the registry's (BoxC is a 40GP).
            var first = await client.PostAsJsonAsync($"{Gate}/blind-orders", Blind(BoxC, carrierRef, equipmentTypeCode: null), ct);
            Assert.True(first.StatusCode == HttpStatusCode.Created, $"blind order returned {(int)first.StatusCode}: {await first.Content.ReadAsStringAsync(ct)}");
            var order = (await first.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!;
            Assert.Equal(("BLIND GATE IN", "WALK_IN", "CUS-TAE"), (order.Booking.OrderTypeCode, order.Booking.Source, order.Booking.CustomerCode));
            Assert.Equal("40GP", Assert.Single(order.Requirements).EquipmentTypeCode);
            var box = Assert.Single(order.Containers);
            Assert.Equal((BoxC, "GATE"), (box.ContainerNo, box.Source));
            Assert.Equal("MTY_IN", box.Steps.OrderBy(s => s.SequenceNo).First().MovementCode);

            // The clerk pressed Save twice, or the answer was lost: the same order, not a second.
            var again = await client.PostAsJsonAsync($"{Gate}/blind-orders", Blind(BoxC, carrierRef, equipmentTypeCode: null), ct);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(order.Booking.OrderNo, (await again.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!.Booking.OrderNo);

            // The barrier now knows the box, and the move is an ordinary gate-in on that order.
            var preflight = (await client.GetFromJsonAsync<GatePreflightResponse>(
                $"{Gate}/preflight?branchId={SctLcb01}&containerNo={BoxC}&direction=IN", ct))!;
            Assert.Equal((order.Booking.OrderNo, "MTY_IN"), (preflight.Booking!.OrderNo, preflight.NextStep!.MovementCode));
            Assert.DoesNotContain(preflight.Findings, f => f.Code == "NO_ASSIGNMENT");

            var eir = await client.PostAsJsonAsync($"{Gate}/transactions", new
            {
                branchId = SctLcb01, containerNo = BoxC, direction = "IN", tripType = "DROP_OFF_CONT",
                tareWeightKg = 2200m, maxGrossWeightKg = 30480m,
                truck = new { plate = "70-9911", driverName = "Somchai P." },
            }, ct);
            Assert.True(eir.StatusCode == HttpStatusCode.Created, $"gate returned {(int)eir.StatusCode}: {await eir.Content.ReadAsStringAsync(ct)}");
            Assert.Equal(order.Booking.OrderNo, (await eir.Content.ReadFromJsonAsync<GateTransactionResponse>(ct))!.OrderNo);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    [Fact]
    public async Task A_blind_order_needs_a_customer_and_a_box_that_is_on_no_other_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            // Owner 2026-10-04: the customer is required on every booking type; BLIND GATE IN is an IMPORT.
            var noCustomer = await client.PostAsJsonAsync($"{Gate}/blind-orders", Blind(BoxA, carrierRef, customerCode: null), ct);
            Assert.Equal(HttpStatusCode.BadRequest, noCustomer.StatusCode);
            Assert.Contains("\"customerCode\"", await noCustomer.Content.ReadAsStringAsync(ct));

            // A box already on an open booking is not "blind": the clerk gates it on that booking.
            var booked = await client.PostAsJsonAsync(Bookings, new
            {
                branchId = SctLcb01, orderTypeCode = "IMP CY/CY", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef = carrierRef + "D",
                requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
                containers = new object[] { new { containerNo = BoxA } },
            }, ct);
            Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
            var taken = await client.PostAsJsonAsync($"{Gate}/blind-orders", Blind(BoxA, carrierRef), ct);
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
            Assert.Contains((await booked.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!.Booking.OrderNo, await taken.Content.ReadAsStringAsync(ct));

            // The booking page still cannot raise one.
            var byHand = await client.PostAsJsonAsync(Bookings, new
            {
                branchId = SctLcb01, orderTypeCode = "BLIND GATE IN", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef = carrierRef + "H",
            }, ct);
            Assert.Contains("made by the gate", await byHand.Content.ReadAsStringAsync(ct));
        }
        finally { await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    [Fact]
    public async Task The_picker_lists_boxes_by_their_next_step_and_leaves_out_the_ones_on_this_truck()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            var created = await client.PostAsJsonAsync(Bookings, new
            {
                branchId = SctLcb01, orderTypeCode = "IMP CY/CY", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,   // FULL_IN > FULL_OUT > MTY_IN
                requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 3 } },
                containers = new object[] { new { containerNo = BoxA }, new { containerNo = BoxB } },
            }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var booking = (await created.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!;

            async Task<List<BookableBoxResponse>> PickAsync(string filters) =>
                (await client.GetFromJsonAsync<PagedResult<BookableBoxResponse>>(
                    $"{Gate}/bookable-boxes?branchId={SctLcb01}&search={carrierRef}{filters}", ct))!.Items.ToList();

            // Searched by the B/L the driver hands over: both boxes, each with its next step.
            var fullIn = await PickAsync("&direction=IN&fullEmpty=FULL");
            Assert.Equal([BoxA, BoxB], fullIn.Select(b => b.ContainerNo).Order());
            var row = fullIn.First(b => b.ContainerNo == BoxA);
            Assert.Equal((booking.Booking.OrderNo, carrierRef, "IMPORT", "IMP CY/CY", "MAEU", "CUS-TAE", "20GP"),
                (row.OrderNo, row.CarrierRef, row.BookingTypeCode, row.OrderTypeCode, row.LineCode, row.CustomerCode, row.EquipmentTypeCode));
            Assert.Equal(("FULL_IN", "IN", "FULL"), (row.NextStep.MovementCode, row.NextStep.Direction, row.NextStep.FullEmpty));

            // A box already on this truck drops out; a pick-up or an empty finds none of them.
            var rest = await PickAsync($"&direction=IN&excludeBookingContainerIds={row.BookingContainerId}");
            Assert.Equal([BoxB], rest.Select(b => b.ContainerNo));
            Assert.Empty(await PickAsync("&direction=OUT"));
            Assert.Empty(await PickAsync("&fullEmpty=EMPTY"));

            // No filter: every box on the booking, whatever its next step.
            Assert.Equal(2, (await PickAsync("")).Count);

            var bad = await client.GetAsync($"{Gate}/bookable-boxes?branchId={SctLcb01}&direction=SIDEWAYS", ct);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        finally { await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }
}
