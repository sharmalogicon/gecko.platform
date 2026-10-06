using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Data;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// A box the booking did not name (owner 2026-10-06; Vector GateIn.cs:2887 + Operation.usp_BookingContainerMovement):
/// the clerk picks an empty place on the booking and keys the box that came. Record holds the place; Save writes the
/// number onto it. Two clerks on the same place: the second is moved to the next free place like it (owner: "a").
/// The yard rules: a drop-off is NOT in any yard, a pick-up IS in this yard with the step's FULL/EMPTY.
///
/// SCT's INT IN (one step, MTY_IN, empty drop-off) and LOUT (GOF, full pick-up).
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class GateAdhocApiTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewRef() => $"ZZN-{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static string NewBox()
    {
        var ten = $"ZZNU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    /// <summary>An INT IN booking with <paramref name="places"/> 20GP places and no container numbers yet.</summary>
    private static async Task<BookingDetailResponse> EmptyPlacesAsync(HttpClient client, string carrierRef, int places, CancellationToken ct)
    {
        var created = await client.PostAsJsonAsync("/api/tos/bookings", new
        {
            branchId = SctLcb01, orderTypeCode = "INT IN", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
            validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            requirements = new object[] { new { equipmentTypeCode = "20GP", qty = places } },
        }, ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"booking {(int)created.StatusCode}: {await created.Content.ReadAsStringAsync(ct)}");
        var booking = (await created.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!;
        var rows = Enumerable.Range(0, places).Select(_ => (object)new { clientLineId = Guid.NewGuid(), containerNo = (string?)null, lineNo = 1 }).ToArray();
        var batch = await client.PostAsJsonAsync($"/api/tos/bookings/{booking.Booking.BookingId}/containers/batch", new { containers = rows }, ct);
        Assert.True(batch.StatusCode == HttpStatusCode.OK, $"batch {(int)batch.StatusCode}: {await batch.Content.ReadAsStringAsync(ct)}");
        return await ReadBookingAsync(client, booking.Booking.BookingId, ct);
    }

    private static async Task<BookingDetailResponse> ReadBookingAsync(HttpClient client, Guid bookingId, CancellationToken ct) =>
        (await client.GetFromJsonAsync<BookingDetailResponse>($"/api/tos/bookings/{bookingId}", ct))!;

    private static Task<HttpResponseMessage> RecordAsync(HttpClient client, Guid draftId, Guid bookingContainerId, string box, CancellationToken ct) =>
        client.PostAsJsonAsync($"{Gate}/reservations", new { branchId = SctLcb01, draftId, bookingContainerId, containerNo = box }, ct);

    private static object DropOff(Guid bookingContainerId, string box) => new
    {
        bookingContainerId,
        move = new { containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m },
    };

    private static async Task<HttpResponseMessage> SaveAsync(HttpClient client, Guid draftId, string plate, object row, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Gate}/trips")
        {
            Content = JsonContent.Create(new { branchId = SctLcb01, draftId, truck = new { plate }, rows = new[] { row } }),
        };
        request.Headers.Add(Idempotency.Header, Guid.NewGuid().ToString());
        return await client.SendAsync(request, ct);
    }

    private static async Task<T> CreatedAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    private static async Task CleanAsync(string carrierRef, params string[] boxes)
    {
        await TestDatabase.RemoveGateAsync(carrierRef);
        await TestDatabase.RemoveBoxReservationsAsync(boxes);
        await TestDatabase.RemoveBookingsAsync(carrierRef);
    }

    [Fact]
    public async Task A_box_keyed_on_an_empty_place_is_written_onto_the_booking_when_it_gates()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        try
        {
            var booking = await EmptyPlacesAsync(client, carrierRef, 1, ct);
            var place = booking.Containers.Single();
            Assert.Null(place.ContainerNo);

            var draft = Guid.NewGuid();
            var hold = await CreatedAsync<BoxReservationResponse>(await RecordAsync(client, draft, place.BookingContainerId, box, ct), ct);
            Assert.Equal((place.BookingContainerId, box, (Guid?)null), (hold.BookingContainerId, hold.ContainerNo, hold.SwitchedFromBookingContainerId));

            var saved = await CreatedAsync<TripSaveResponse>(await SaveAsync(client, draft, "70-2887", DropOff(place.BookingContainerId, box), ct), ct);
            var row = Assert.Single(saved.Rows);
            Assert.True(row.Status == "GATED", $"{row.Status}: {row.Reason}");

            var after = (await ReadBookingAsync(client, booking.Booking.BookingId, ct)).Containers.Single();
            Assert.Equal((box, "GATE"), (after.ContainerNo, after.Source));
        }
        finally { await CleanAsync(carrierRef, box); }
    }

    [Fact]
    public async Task Two_clerks_on_the_same_empty_place_the_second_is_moved_to_the_next_free_place()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var (boxA, boxB) = (NewBox(), NewBox());
        try
        {
            var booking = await EmptyPlacesAsync(client, carrierRef, 2, ct);
            var first = booking.Containers.OrderBy(c => c.AssignedAt).ThenBy(c => c.BookingContainerId).First().BookingContainerId;
            var (draftA, draftB) = (Guid.NewGuid(), Guid.NewGuid());

            // Both clerks press Record on the first place: one keeps it, the other is moved, told why.
            var holds = await Task.WhenAll(RecordAsync(client, draftA, first, boxA, ct), RecordAsync(client, draftB, first, boxB, ct));
            var a = await CreatedAsync<BoxReservationResponse>(holds[0], ct);
            var b = await CreatedAsync<BoxReservationResponse>(holds[1], ct);
            Assert.NotEqual(a.BookingContainerId, b.BookingContainerId);
            var moved = a.SwitchedFromBookingContainerId is null ? b : a;
            Assert.Equal(first, moved.SwitchedFromBookingContainerId);
            Assert.Contains("next free place", moved.Message);

            // Both Save at the same instant, both still naming the FIRST place (a stale screen): both go through, on two places.
            var saves = await Task.WhenAll(
                SaveAsync(client, draftA, "70-0001", DropOff(first, boxA), ct),
                SaveAsync(client, draftB, "70-0002", DropOff(first, boxB), ct));
            var rows = new List<TripRowResponse>();
            foreach (var s in saves) rows.Add(Assert.Single((await CreatedAsync<TripSaveResponse>(s, ct)).Rows));
            Assert.All(rows, r => Assert.True(r.Status == "GATED", $"{r.ContainerNo} {r.Status}: {r.Reason}"));
            Assert.Equal(2, rows.Select(r => r.BookingContainerId).Distinct().Count());
            Assert.Contains(rows, r => r.Findings.Any(f => f.Code == "PLACE_SWITCHED"));

            var after = await ReadBookingAsync(client, booking.Booking.BookingId, ct);
            Assert.Equal(new[] { boxA, boxB }.Order(), after.Containers.Select(c => c.ContainerNo!).Order());
        }
        finally { await CleanAsync(carrierRef, boxA, boxB); }
    }

    [Fact]
    public async Task When_no_like_place_is_free_the_second_clerk_is_told_so()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var (boxA, boxB) = (NewBox(), NewBox());
        try
        {
            var place = (await EmptyPlacesAsync(client, carrierRef, 1, ct)).Containers.Single().BookingContainerId;
            await CreatedAsync<BoxReservationResponse>(await RecordAsync(client, Guid.NewGuid(), place, boxA, ct), ct);

            var second = await RecordAsync(client, Guid.NewGuid(), place, boxB, ct);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            var body = await second.Content.ReadAsStringAsync(ct);
            Assert.Contains("NO_FREE_PLACE", body);
            Assert.Contains("20GP", body);
        }
        finally { await CleanAsync(carrierRef, boxA, boxB); }
    }

    /// <summary>The four yard rules (owner 2026-10-06), on Record and at the barrier.</summary>
    [Fact]
    public async Task A_drop_off_must_not_be_in_the_yard_and_a_pick_up_must_be_here_with_the_steps_full_or_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        try
        {
            // The box comes in EMPTY on one INT IN booking (one step: the booking is then done with it).
            var firstPlace = (await EmptyPlacesAsync(client, carrierRef + "A", 1, ct)).Containers.Single().BookingContainerId;
            var draft = Guid.NewGuid();
            var gated = await CreatedAsync<TripSaveResponse>(await SaveAsync(client, draft, "70-1111", DropOff(firstPlace, box), ct), ct);
            Assert.Equal("GATED", Assert.Single(gated.Rows).Status);

            // Dropping it off again on another booking is refused on Record already.
            var again = (await EmptyPlacesAsync(client, carrierRef + "B", 1, ct)).Containers.Single().BookingContainerId;
            var refused = await RecordAsync(client, Guid.NewGuid(), again, box, ct);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var body = await refused.Content.ReadAsStringAsync(ct);
            Assert.Contains("BOX_REFUSED", body);
            Assert.Contains("ALREADY_IN_YARD", body);

            // A FULL pick-up (LOUT) of a box that is EMPTY in the yard is refused at the barrier.
            var lout = await client.PostAsJsonAsync("/api/tos/bookings", new
            {
                branchId = SctLcb01, orderTypeCode = "LOUT", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef = carrierRef + "C",
                validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
                containers = new object[] { new { containerNo = box } },
            }, ct);
            Assert.True(lout.StatusCode == HttpStatusCode.Created, $"LOUT {(int)lout.StatusCode}: {await lout.Content.ReadAsStringAsync(ct)}");
            var preflight = (await client.GetFromJsonAsync<GatePreflightResponse>($"{Gate}/preflight?branchId={SctLcb01}&containerNo={box}&direction=OUT", ct))!;
            var mismatch = Assert.Single(preflight.Findings, f => f.Code == "LOAD_MISMATCH");
            Assert.Equal("BLOCK", mismatch.Severity);
            Assert.Contains("EMPTY", mismatch.Message);
        }
        finally { await CleanAsync(carrierRef, box); }
    }

    /// <summary>Two gate moves of the same box at the same instant: exactly one EIR, the other a clean 409 (never a 500).</summary>
    [Fact]
    public async Task Two_gate_moves_of_the_same_box_at_the_same_instant_give_one_EIR_and_one_409()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        try
        {
            var booking = await EmptyPlacesAsync(client, carrierRef, 1, ct);
            var place = booking.Containers.Single();
            var named = await client.PutAsJsonAsync($"/api/tos/bookings/{booking.Booking.BookingId}/containers/{place.BookingContainerId}",
                new { rowVersion = place.RowVersion, containerNo = box }, ct);
            Assert.Equal(HttpStatusCode.OK, named.StatusCode);

            Task<HttpResponseMessage> Move(string plate) => client.PostAsJsonAsync($"{Gate}/transactions", new
            {
                branchId = SctLcb01, containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT",
                truck = new { plate }, tareWeightKg = 2200m, maxGrossWeightKg = 30480m,
            }, ct);
            var results = await Task.WhenAll(Move("70-0101"), Move("70-0102"), Move("70-0103"));
            var codes = results.Select(r => r.StatusCode).ToList();
            Assert.Equal(1, codes.Count(c => c == HttpStatusCode.Created));
            Assert.All(codes.Where(c => c != HttpStatusCode.Created), c => Assert.Equal(HttpStatusCode.Conflict, c));
        }
        finally { await CleanAsync(carrierRef, box); }
    }
}
