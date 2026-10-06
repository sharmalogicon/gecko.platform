using System.Net;
using System.Net.Http.Json;
using Gecko.Data;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Gate out on the one-call Save (GATE_OPEN_ITEMS_FOR_API #1; Vector GateOut.cs loads the truck's own movement header,
/// TransactionNo, instead of opening a new one): a truck already in the yard joins its visit, by
/// <c>truckVisitId</c> or by the draft that opened it; a retried Save is the same answer; two clerks never take the
/// same yard box. SCT's INT IN (MTY_IN) brings an empty box in; INT OUT (MTY_OUT) takes one out.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class GateOutTripApiTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewRef() => $"ZZO-{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static string NewBox()
    {
        var ten = $"ZZOU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    private static async Task<BookingDetailResponse> EmptyPlacesAsync(HttpClient client, string orderType, string carrierRef, int places, CancellationToken ct)
    {
        var created = await client.PostAsJsonAsync("/api/tos/bookings", new
        {
            branchId = SctLcb01, orderTypeCode = orderType, lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
            validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            requirements = new object[] { new { equipmentTypeCode = "20GP", qty = places } },
        }, ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"booking {(int)created.StatusCode}: {await created.Content.ReadAsStringAsync(ct)}");
        var booking = (await created.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!;
        var rows = Enumerable.Range(0, places).Select(_ => (object)new { clientLineId = Guid.NewGuid(), containerNo = (string?)null, lineNo = 1 }).ToArray();
        var batch = await client.PostAsJsonAsync($"/api/tos/bookings/{booking.Booking.BookingId}/containers/batch", new { containers = rows }, ct);
        Assert.True(batch.StatusCode == HttpStatusCode.OK, $"batch {(int)batch.StatusCode}: {await batch.Content.ReadAsStringAsync(ct)}");
        return (await client.GetFromJsonAsync<BookingDetailResponse>($"/api/tos/bookings/{booking.Booking.BookingId}", ct))!;
    }

    private static object DropOff(Guid place, string box) => new
    {
        bookingContainerId = place,
        move = new { containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m },
    };

    private static object PickUp(Guid place, string box) => new
    {
        bookingContainerId = place,
        move = new { containerNo = box, direction = "OUT", tripType = "PICK_UP_CONT" },
    };

    private static async Task<HttpResponseMessage> SaveAsync(HttpClient client, string key, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Gate}/trips") { Content = JsonContent.Create(body) };
        request.Headers.Add(Idempotency.Header, key);
        return await client.SendAsync(request, ct);
    }

    private static async Task<TripSaveResponse> SavedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"save {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        var answer = (await response.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!;
        Assert.All(answer.Rows, r => Assert.True(r.Status == "GATED", $"{r.ContainerNo} {r.Status}: {r.Reason}"));
        return answer;
    }

    private static async Task CleanAsync(string carrierRef, params string[] boxes)
    {
        await TestDatabase.RemoveGateAsync(carrierRef);
        await TestDatabase.RemoveBoxReservationsAsync(boxes);
        await TestDatabase.RemoveBookingsAsync(carrierRef);
    }

    [Fact]
    public async Task A_truck_in_the_yard_takes_a_box_out_on_its_own_visit_and_a_retry_is_the_same_answer()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var (dropped, taken) = (NewBox(), NewBox());
        try
        {
            // An empty box already in the yard (another truck brought it).
            var stock = (await EmptyPlacesAsync(client, "INT IN", carrierRef + "S", 1, ct)).Containers.Single().BookingContainerId;
            await SavedAsync(await SaveAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-0000" }, rows = new[] { DropOff(stock, taken) },
            }, ct), ct);

            // Truck 70-5555 comes in with a box and to collect an empty one the yard will choose (no number yet).
            var draft = Guid.NewGuid();
            var inPlace = (await EmptyPlacesAsync(client, "INT IN", carrierRef + "I", 1, ct)).Containers.Single().BookingContainerId;
            var outBooking = await EmptyPlacesAsync(client, "INT OUT", carrierRef + "O", 1, ct);
            var outPlace = outBooking.Containers.Single().BookingContainerId;
            var arrivedAt = await SaveAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = draft, truck = new { plate = "70-5555" }, rows = new[] { DropOff(inPlace, dropped), PickUp(outPlace, "") },
            }, ct);
            Assert.True(arrivedAt.StatusCode == HttpStatusCode.Created, $"gate in {(int)arrivedAt.StatusCode}: {await arrivedAt.Content.ReadAsStringAsync(ct)}");
            var arrived = (await arrivedAt.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!;
            Assert.Equal(new[] { "GATED", "PLANNED" }, arrived.Rows.Select(r => r.Status));
            var pending = (await client.GetFromJsonAsync<TruckVisitResponse>($"{Gate}/visits/{arrived.TruckVisitId}", ct))!.Pickups!;
            Assert.Equal((outPlace, (string?)null, "PLANNED"), (pending.Single().BookingContainerId, pending.Single().ContainerNo, pending.Single().Status));

            // The truck cannot just leave: it came for a box.
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{Gate}/visits/{arrived.TruckVisitId}/depart", new { }, ct)).StatusCode);

            // At the gate-out lane: the truck in front of the clerk, the box the yard loaded; the same visit.
            var key = Guid.NewGuid().ToString();
            object outBody = new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truckVisitId = arrived.TruckVisitId, rows = new[] { PickUp(outPlace, taken) },
            };
            var left = await SavedAsync(await SaveAsync(client, key, outBody, ct), ct);
            Assert.Equal((arrived.TruckVisitId, arrived.VisitNo), (left.TruckVisitId, left.VisitNo));   // the same visit, not a second one
            Assert.NotNull(left.TruckLeftAt);
            var booked = (await client.GetFromJsonAsync<BookingDetailResponse>($"/api/tos/bookings/{outBooking.Booking.BookingId}", ct))!;
            Assert.Equal(taken, booked.Containers.Single().ContainerNo);   // the box keyed at gate out is written onto the booking

            // The answer was lost: the same key again is the same EIR, nothing new.
            var again = await SavedAsync(await SaveAsync(client, key, outBody, ct), ct);
            Assert.Equal(left.Rows.Single().EirNo, again.Rows.Single().EirNo);

            var visit = (await client.GetFromJsonAsync<TruckVisitResponse>($"{Gate}/visits/{arrived.TruckVisitId}", ct))!;
            Assert.Equal(2, visit.Transactions.Count(m => m.Status == "COMPLETED"));
        }
        finally { await CleanAsync(carrierRef, dropped, taken); }
    }

    [Fact]
    public async Task A_second_Save_from_the_same_screen_joins_the_visit_that_screen_opened()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var (first, second) = (NewBox(), NewBox());
        try
        {
            var places = (await EmptyPlacesAsync(client, "INT IN", carrierRef, 2, ct)).Containers.Select(c => c.BookingContainerId).ToList();
            var draft = Guid.NewGuid();
            object Body(Guid place, string box) => new { branchId = SctLcb01, draftId = draft, truck = new { plate = "70-2957" }, rows = new[] { DropOff(place, box) } };

            var a = await SavedAsync(await SaveAsync(client, Guid.NewGuid().ToString(), Body(places[0], first), ct), ct);
            var b = await SavedAsync(await SaveAsync(client, Guid.NewGuid().ToString(), Body(places[1], second), ct), ct);
            Assert.Equal(a.TruckVisitId, b.TruckVisitId);   // one truck arrival, one visit (GATE_OPEN_ITEMS #1)
        }
        finally { await CleanAsync(carrierRef, first, second); }
    }

    [Fact]
    public async Task A_visit_that_has_left_or_belongs_elsewhere_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var saved = await SaveAsync(client, Guid.NewGuid().ToString(), new
        {
            branchId = SctLcb01, draftId = Guid.NewGuid(), truckVisitId = Guid.NewGuid(), rows = new[] { PickUp(Guid.NewGuid(), NewBox()) },
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        Assert.Contains("truckVisitId", await saved.Content.ReadAsStringAsync(ct));

        var noTruck = await SaveAsync(client, Guid.NewGuid().ToString(), new
        {
            branchId = SctLcb01, draftId = Guid.NewGuid(), rows = new[] { PickUp(Guid.NewGuid(), NewBox()) },
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, noTruck.StatusCode);
        Assert.Contains("truck", await noTruck.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Two gate-out clerks key the same yard box for two trucks: the first holds it, the second is told who has it.</summary>
    [Fact]
    public async Task Two_clerks_cannot_take_the_same_yard_box_out()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        try
        {
            var stock = (await EmptyPlacesAsync(client, "INT IN", carrierRef + "S", 1, ct)).Containers.Single().BookingContainerId;
            await SavedAsync(await SaveAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-0000" }, rows = new[] { DropOff(stock, box) },
            }, ct), ct);

            var places = (await EmptyPlacesAsync(client, "INT OUT", carrierRef + "O", 2, ct)).Containers.Select(c => c.BookingContainerId).ToList();
            Task<HttpResponseMessage> Record(Guid draft, Guid place) =>
                client.PostAsJsonAsync($"{Gate}/reservations", new { branchId = SctLcb01, draftId = draft, bookingContainerId = place, containerNo = box }, ct);
            var answers = await Task.WhenAll(Record(Guid.NewGuid(), places[0]), Record(Guid.NewGuid(), places[1]));
            Assert.Equal(1, answers.Count(r => r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK));
            var refused = Assert.Single(answers, r => r.StatusCode == HttpStatusCode.Conflict);
            var body = await refused.Content.ReadAsStringAsync(ct);
            Assert.True(body.Contains("BOX_RESERVED") || body.Contains("BOX_REFUSED"), body);
        }
        finally { await CleanAsync(carrierRef, box); }
    }
}
