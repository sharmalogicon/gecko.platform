using System.Net;
using System.Net.Http.Json;
using Gecko.Data;
using Gecko.Revenue.Endpoints.Window;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// The gate's big Save (owner 2026-10-04, GATE_IN_BIG_SAVE.md §2), through the real host and its real
/// dispatchers, on FIXTURE data. One call for the truck: ONE receipt across its bookings (the gate charge
/// once), an EIR per box, everything the clerk prints in the answer; a retry gets the same answer; a box
/// the barrier refuses after payment stays paid and comes back NOT_GATED.
///
/// SCT's GATE TEST (gecko_master dev_09, rates gecko_revenue dev_02): FULL_IN owes nothing; FULL_OUT owes
/// GATETRIP (PER_TRIP, ฿100 any truck) and GATEFEE (฿150) in cash. The coupon rule is switched ON for
/// SCT-LCB01 for these tests, so a box truly cannot move unpaid.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class GateTripSaveApiTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private const string Window = "/api/revenue/window";
    private const string OrderType = "GATE TEST";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewRef() => $"ZZT-{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static string NewBox()
    {
        var ten = $"ZZTU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    private static async Task<BookingDetailResponse> BookAsync(HttpClient client, string carrierRef, string box, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync("/api/tos/bookings", new
        {
            branchId = SctLcb01, orderTypeCode = OrderType, lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
            validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
            containers = new object[] { new { containerNo = box } },
        }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"booking returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!;
    }

    private static async Task DropOffAsync(HttpClient client, string box, CancellationToken ct)
    {
        await EventuallyAsync(() => PreflightAsync(client, box, "IN", ct), v => v.Decision == "ALLOWED", $"{box} allowed in", ct);
        var response = await client.PostAsJsonAsync($"{Gate}/transactions", new
        {
            branchId = SctLcb01, containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT",
            truck = new { plate = "70-1111" },
            grossWeightKg = 22000m, tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 19800m,
            seals = new object[] { new { sealNo = $"ZZT-{box[^4..]}", sealType = "LINE", isIntact = true } },
        }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"gate-in returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }

    private static async Task<GatePreflightResponse> PreflightAsync(HttpClient client, string box, string direction, CancellationToken ct) =>
        (await client.GetFromJsonAsync<GatePreflightResponse>($"{Gate}/preflight?branchId={SctLcb01}&containerNo={box}&direction={direction}", ct))!;

    /// <summary>The pick-up quote once Revenue has seen the gate-in (FULL_OUT is then next).</summary>
    private static Task<WindowBookingResponse> PickUpQuoteAsync(HttpClient client, string orderNo, CancellationToken ct) =>
        EventuallyAsync(async () =>
        {
            var response = await client.GetAsync($"{Window}/bookings?orderNo={Uri.EscapeDataString(orderNo)}", ct);
            return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<WindowBookingResponse>(ct) : null;
        }, q => q is not null && q.Boxes.Single().NextMovementCode == "FULL_OUT", $"{orderNo} quoted for FULL_OUT", ct)!;

    private static object PickUp(Guid bookingContainerId, string box) => new
    {
        bookingContainerId,
        move = new
        {
            containerNo = box, direction = "OUT", tripType = "PICK_UP_CONT",
            seals = new object[] { new { sealNo = $"ZZT-{box[^4..]}", sealType = "LINE", isIntact = true } },
        },
    };

    private static async Task<HttpResponseMessage> SaveAsync(HttpClient client, string key, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Gate}/trips") { Content = JsonContent.Create(body) };
        request.Headers.Add(Idempotency.Header, key);
        return await client.SendAsync(request, ct);
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done, string waitingFor, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var value = await read();
            if (done(value)) return value;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Waited 30 s for {waitingFor}.");
            await Task.Delay(250, ct);
        }
    }

    private static async Task<Guid> OpenDrawerAsync(HttpClient client, CancellationToken ct)
    {
        var opened = await client.PostAsJsonAsync($"{Window}/shifts", new { branchId = SctLcb01, openingFloat = 0m }, ct);
        Assert.True(opened.StatusCode == HttpStatusCode.Created, await opened.Content.ReadAsStringAsync(ct));
        return (await opened.Content.ReadFromJsonAsync<ShiftResponse>(ct))!.ShiftId;
    }

    private static async Task CleanAsync(string carrierRef, Guid? shiftId, params string[] boxes)
    {
        await TestDatabase.RemoveCashWindowAsync(carrierRef, shiftId);
        await TestDatabase.RemoveGateAsync(carrierRef);
        await TestDatabase.RemoveBoxReservationsAsync(boxes);
        await TestDatabase.RemoveHoldsAsync(boxes);
        await TestDatabase.RemoveBookingsAsync(carrierRef);
        await TestDatabase.RequireCouponAtAsync(SctLcb01, null);
    }

    [Fact]
    public async Task One_Save_takes_the_trucks_cash_on_one_receipt_and_gates_every_box_and_a_retry_gets_the_same_answer()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var (boxA, boxB) = (NewBox(), NewBox());
        Guid? shiftId = null;
        await TestDatabase.RequireCouponAtAsync(SctLcb01, true);
        try
        {
            // Two bookings, each box already in the yard: the truck comes to pick both up.
            var a = await BookAsync(client, carrierRef + "A", boxA, ct);
            var b = await BookAsync(client, carrierRef + "B", boxB, ct);
            await DropOffAsync(client, boxA, ct);
            await DropOffAsync(client, boxB, ct);
            shiftId = await OpenDrawerAsync(client, ct);

            // What the card showed: each booking's quote, the gate charge once for the truck.
            var qa = await PickUpQuoteAsync(client, a.Booking.OrderNo, ct);
            var qb = await PickUpQuoteAsync(client, b.Booking.OrderNo, ct);
            var trip = qb.Boxes.Single().Due.Single(l => l.BillingUnitCode == "PER_TRIP");
            var expected = qa.Total + qb.Total - trip.Total;

            var body = new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(),
                truck = new { plate = "70-7777", driverName = "Somchai P." },
                rows = new[]
                {
                    PickUp(a.Containers.Single().BookingContainerId, boxA),
                    PickUp(b.Containers.Single().BookingContainerId, boxB),
                },
                payment = new { payments = new[] { new { channel = "CASH", amount = expected } }, expectedTotal = expected },
            };
            var key = Guid.NewGuid().ToString();
            var saved = await SaveAsync(client, key, body, ct);
            Assert.True(saved.StatusCode == HttpStatusCode.Created, $"save returned {(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync(ct)}");
            var answer = (await saved.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!;

            // One receipt for the truck, both bookings on it.
            Assert.NotNull(answer.Receipt);
            Assert.Equal((expected, expected), (answer.Receipt.Total, answer.Receipt.Nett));
            var receipt = (await client.GetFromJsonAsync<ReceiptResponse>($"{Window}/receipts/{answer.Receipt.ReceiptId}", ct))!;
            Assert.Equal(new[] { boxA, boxB }.Order(), receipt.Lines.Select(l => l.ContainerNo).Distinct().Order());
            Assert.Equal(1, receipt.Lines.Count(l => l.BillingUnitCode == "PER_TRIP"));

            // An EIR per box, on one truck visit.
            Assert.All(answer.Rows, r => Assert.Equal("GATED", r.Status));
            Assert.Equal(2, answer.Rows.Select(r => r.EirNo).Distinct().Count());
            Assert.All(answer.Rows, r => Assert.NotNull(r.CouponRef));
            Assert.NotNull(answer.VisitNo);

            // The answer was lost: the same Save again is the same receipt and the same EIRs, nothing new.
            var again = await SaveAsync(client, key, body, ct);
            Assert.Equal(HttpStatusCode.Created, again.StatusCode);
            var replay = (await again.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!;
            Assert.Equal(answer.Receipt.ReceiptNo, replay.Receipt!.ReceiptNo);
            Assert.Equal(answer.Rows.Select(r => r.EirNo), replay.Rows.Select(r => r.EirNo));

            // The same key with another body is a client bug.
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SaveAsync(client, key, body with { draftId = Guid.NewGuid() }, ct)).StatusCode);
        }
        finally { await CleanAsync(carrierRef, shiftId, boxA, boxB); }
    }

    /// <summary>A box with no paperwork: the Save raises its BLIND GATE IN order, waits for the window to know it, takes the cash and gates it in.</summary>
    [Fact]
    public async Task A_blind_box_is_ordered_paid_and_gated_in_one_Save()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        Guid? shiftId = null;
        await TestDatabase.RequireCouponAtAsync(SctLcb01, true);
        await TestDatabase.GateChargeOnBlindGateInAsync(add: true);
        try
        {
            shiftId = await OpenDrawerAsync(client, ct);
            var preview = (await client.GetFromJsonAsync<WindowBookingResponse>(
                $"{Window}/preview?branchId={SctLcb01}&orderTypeCode=BLIND%20GATE%20IN&lineCode=MAEU&customerCode=CUS-TAE&equipmentTypeCode=20GP", ct))!;
            Assert.True(preview.Total > 0);

            var saved = await SaveAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(),
                truck = new { plate = "70-9999" },
                rows = new[]
                {
                    new
                    {
                        blind = new { containerNo = box, lineCode = "MAEU", customerCode = "CUS-TAE", equipmentTypeCode = "20GP", carrierRef },
                        move = new { containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m },
                    },
                },
                payment = new { payments = new[] { new { channel = "CASH", amount = preview.Total } }, expectedTotal = preview.Total },
            }, ct);
            Assert.True(saved.StatusCode == HttpStatusCode.Created, $"save returned {(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync(ct)}");
            var answer = (await saved.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!;

            Assert.Equal(preview.Total, answer.Receipt!.Total);
            var row = Assert.Single(answer.Rows);
            Assert.True(row.Status == "GATED", $"{row.Status}: {row.Reason}");
            Assert.NotNull(row.EirNo);
            Assert.StartsWith("BK-", row.OrderNo);
        }
        finally
        {
            await TestDatabase.GateChargeOnBlindGateInAsync(add: false);
            await CleanAsync(carrierRef, shiftId, box);
        }
    }

    [Fact]
    public async Task A_box_the_barrier_refuses_after_payment_stays_paid_and_comes_back_not_gated()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        Guid? shiftId = null;
        await TestDatabase.RequireCouponAtAsync(SctLcb01, true);
        try
        {
            var booking = await BookAsync(client, carrierRef, box, ct);
            await DropOffAsync(client, box, ct);
            shiftId = await OpenDrawerAsync(client, ct);
            var quote = await PickUpQuoteAsync(client, booking.Booking.OrderNo, ct);

            // Customs puts a hold on the box after the clerk priced it.
            var hold = await client.PostAsJsonAsync("/api/tos/holds", new { containerNo = box, holdCode = "CUSTOMS", reason = "Red line" }, ct);
            Assert.Equal(HttpStatusCode.Created, hold.StatusCode);

            var saved = await SaveAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(),
                truck = new { plate = "70-8888" },
                rows = new[] { PickUp(booking.Containers.Single().BookingContainerId, box) },
                payment = new { payments = new[] { new { channel = "CASH", amount = quote.Total } }, expectedTotal = quote.Total },
            }, ct);
            Assert.True(saved.StatusCode == HttpStatusCode.Created, $"save returned {(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync(ct)}");
            var answer = (await saved.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!;

            Assert.NotNull(answer.Receipt);                        // the money was taken …
            var row = Assert.Single(answer.Rows);
            Assert.Equal("NOT_GATED", row.Status);                 // … the box did not move …
            Assert.Null(row.EirNo);
            Assert.Contains(row.Findings, f => f.Code == "HOLD");  // … and the clerk is told why.
            Assert.NotNull(row.CouponRef);

            // Its coupon is still good: once customs releases it, it goes out without paying again.
            var after = await PreflightAsync(client, box, "OUT", ct);
            Assert.DoesNotContain(after.Findings, f => f.Code == "NO_COUPON");
        }
        finally { await CleanAsync(carrierRef, shiftId, box); }
    }
}
