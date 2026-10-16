using System.Net;
using System.Net.Http.Json;
using Gecko.Data;
using Gecko.Revenue.Endpoints.Window;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Storage on the way out, end to end (PLAN_BILLING §4.4; owner 2026-10-16): an empty box dropped off by a walk-in truck
/// stands 10 days (its stay is moved back), then a walk-in truck picks it up. ZZSTORE (TestDatabase) is SCT's empty storage
/// for WALK MTY OUT, ฿100 a day after PUB-LCB's 5 free days — 11 days in the yard, both ends counted, = 6 days = ฿600 + 7%.
/// A cash customer pays it before the box goes out and tops up the extra days of a later "paid until"; a credit customer
/// (R2, CREDIT_FOR_CREDIT_CUSTOMERS) is not asked for cash, passes the barrier, and is billed for it on credit.
/// The coupon rule is ON at SCT-LCB01 for the pick-up and off again at the end.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class StorageFlowTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private const string Window = "/api/revenue/window";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewTag() => $"ZZSTOR-{Guid.NewGuid():N}"[..20];

    private static string NewBox()
    {
        var ten = $"ZZSU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    [Fact]
    public async Task A_cash_customer_pays_storage_before_the_box_goes_out_and_tops_up_the_extra_days()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var tag = NewTag();
        var box = NewBox();
        await TestDatabase.EmptyStorageOnWalkMtyOutAsync(add: true);
        await TestDatabase.SetSctCustomerCreditAsync("CUS-CEM", "CASH", null, null);
        try
        {
            var pickUp = await StoredTenDaysThenPlannedAsync(client, box, tag, "CUS-CEM", ct);
            var orderNo = pickUp.OrderNo;
            await TestDatabase.RequireCouponAtAsync(SctLcb01, true);

            // ── the window: 6 days of storage are due, from the day the box came in to today
            var quote = await QuoteAsync(client, orderNo, null, ct);
            var boxQuote = quote.Boxes.Single();
            Assert.Equal(11, boxQuote.StayDays);
            var storage = Assert.Single(boxQuote.Due, l => l.Kind == "STORAGE");
            Assert.Equal(("ZZSTORE", "CASH", 6m, 600m, 42m), (storage.ChargeCode, storage.PaymentTermCode, storage.Quantity, storage.Amount, storage.TaxAmount));
            Assert.Equal((quote.Today.AddDays(-10), quote.Today), (storage.ServiceFrom, storage.ServiceTo));

            // ── unpaid: the box may not go out
            var refused = await PreflightAsync(client, box, ct);
            Assert.Equal("BLOCKED", refused.Decision);
            Assert.Equal("BLOCK", Assert.Single(refused.Findings, f => f.Code == "NO_COUPON").Severity);

            var opened = await client.PostAsJsonAsync($"{Window}/shifts", new { branchId = SctLcb01, openingFloat = 0m }, ct);
            Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
            var paid = await client.PostAsJsonAsync($"{Window}/receipts", Pay(quote, null), ct);
            Assert.True(paid.StatusCode == HttpStatusCode.Created, await paid.Content.ReadAsStringAsync(ct));

            // ── the truck is late: paid until today, it comes in two days. Only the 2 extra days are due.
            var later = quote.Today.AddDays(2);
            var topUpQuote = await QuoteAsync(client, orderNo, later, ct);
            var topUp = Assert.Single(topUpQuote.Boxes.Single().Due);
            Assert.Equal(("STORAGE", "ZZSTORE", 2m, 200m), (topUp.Kind, topUp.ChargeCode, topUp.Quantity, topUp.Amount));
            Assert.Equal((quote.Today.AddDays(1), later), (topUp.ServiceFrom, topUp.ServiceTo));
            Assert.Equal(214m, topUpQuote.Total);
            var toppedUp = await client.PostAsJsonAsync($"{Window}/receipts", Pay(topUpQuote, later), ct);
            Assert.True(toppedUp.StatusCode == HttpStatusCode.Created, await toppedUp.Content.ReadAsStringAsync(ct));
            Assert.Empty((await QuoteAsync(client, orderNo, later, ct)).Boxes.Single().Due);

            // ── paid: the coupon reaches the barrier and the box goes out with nothing more to pay
            await ClearedAsync(client, box, "the paid coupon", ct);
            await GatedOutAsync(client, box, pickUp, ct);
        }
        finally
        {
            await TestDatabase.RequireCouponAtAsync(SctLcb01, null);
            await TestDatabase.SetSctCustomerCreditAsync("CUS-CEM", "CREDIT", 1000000m, "THB");
            await TestDatabase.RemoveWalkInAsync(tag);
            await TestDatabase.RemoveBoxReservationsAsync(box);
            await TestDatabase.EmptyStorageOnWalkMtyOutAsync(add: false);
        }
    }

    /// <summary>
    /// R2 on storage (owner 2026-10-16): a credit customer is billed for its storage, never asked for it in cash. Nothing is
    /// due at the window, the automatic coupon lets the box out, and the gate-out bills the 6 days on credit (no credit rate:
    /// the cash rate, billed on credit), on the stay they cover.
    /// </summary>
    [Fact]
    public async Task A_credit_customers_storage_is_billed_on_credit_and_never_holds_the_box()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var tag = NewTag();
        var box = NewBox();
        await TestDatabase.EmptyStorageOnWalkMtyOutAsync(add: true);
        try
        {
            await TestDatabase.RequireCouponAtAsync(SctLcb01, true);
            var pickUp = await StoredTenDaysThenPlannedAsync(client, box, tag, "CUS-TAE", ct, payment: true);
            var orderNo = pickUp.OrderNo;

            var quote = await QuoteAsync(client, orderNo, null, ct);
            var boxQuote = quote.Boxes.Single();
            Assert.DoesNotContain(boxQuote.Due, l => l.Kind == "STORAGE");
            Assert.Equal(0m, quote.Total);
            var billed = Assert.Single(boxQuote.BilledLater ?? [], l => l.Kind == "STORAGE");
            Assert.Equal(("ZZSTORE", "CREDIT", 6m, 600m), (billed.ChargeCode, billed.PaymentTermCode, billed.Quantity, billed.Amount));
            Assert.Contains(boxQuote.Tried, t => t is { ChargeCode: "ZZSTORE", Outcome: "CUSTOMER_ON_CREDIT" });

            // Nothing to pay in cash: the automatic coupon lets the box out.
            await ClearedAsync(client, box, "the automatic coupon", ct);
            await GatedOutAsync(client, box, pickUp, ct);

            // The gate-out bills it on credit, on the stay it covers.
            var charges = await EventuallyAsync(() => TestDatabase.WalkInChargesAsync(tag, "ZZSTORE"), c => c.Count > 0, "the credit storage", ct);
            var charge = Assert.Single(charges);
            Assert.Equal(("GATE", "UNBILLED", "CREDIT", 6m, 600m), (charge.Source, charge.Status, charge.PaymentTermCode, charge.Quantity, charge.Amount));
            Assert.NotNull(charge.ContainerStayId);
        }
        finally
        {
            await TestDatabase.RequireCouponAtAsync(SctLcb01, null);
            await TestDatabase.RemoveWalkInAsync(tag);
            await TestDatabase.RemoveBoxReservationsAsync(box);
            await TestDatabase.EmptyStorageOnWalkMtyOutAsync(add: false);
        }
    }

    /// <summary>
    /// A walk-in truck drops the empty box off (credit customer CUS-TAE: nothing to pay), Revenue opens its stay, the stay is
    /// moved 10 days back, and a walk-in truck of <paramref name="customer"/> comes for it: the pick-up is planned on its visit.
    /// </summary>
    private static async Task<PickUp> StoredTenDaysThenPlannedAsync(HttpClient client, string box, string tag, string customer,
        CancellationToken ct, bool payment = false)
    {
        var dropped = await SavedAsync(client, new
        {
            branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-7171" },
            rows = new[]
            {
                new
                {
                    walkIn = new { fullEmpty = "EMPTY", customerCode = "CUS-TAE", lineCode = "MAEU", equipmentTypeCode = "20GP", remarks = tag },
                    move = new { containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m },
                },
            },
            payment = payment ? new { payments = Array.Empty<object>(), expectedTotal = 0m } : null,
        }, ct);
        Assert.True(dropped.Rows.Single().Status == "GATED", dropped.Rows.Single().Reason);
        await EventuallyAsync(() => TestDatabase.BackdateStayAsync(box, 10), moved => moved == 1, "Revenue to open the stay", ct);

        var planned = await SavedAsync(client, new
        {
            branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-7272" },
            rows = new[] { new { walkIn = new { fullEmpty = "EMPTY", customerCode = customer, remarks = tag }, move = new { containerNo = box, direction = "OUT", tripType = "PICK_UP_CONT" } } },
        }, ct);
        var row = Assert.Single(planned.Rows);
        Assert.True(row.Status == "PLANNED", $"{row.Status}: {row.Reason}");
        return new PickUp(row.OrderNo, planned.TruckVisitId, row.BookingContainerId);
    }

    private sealed record PickUp(string OrderNo, Guid? TruckVisitId, Guid BookingContainerId);

    /// <summary>Gate out releases the planned pick-up, with no cash taken: everything due was settled before.</summary>
    private static async Task GatedOutAsync(HttpClient client, string box, PickUp pickUp, CancellationToken ct)
    {
        var released = await SavedAsync(client, new
        {
            branchId = SctLcb01, draftId = Guid.NewGuid(), truckVisitId = pickUp.TruckVisitId,
            rows = new[] { new { bookingContainerId = pickUp.BookingContainerId, move = new { containerNo = box, direction = "OUT", tripType = "PICK_UP_CONT" } } },
            payment = new { payments = Array.Empty<object>(), expectedTotal = 0m },
        }, ct);
        var row = Assert.Single(released.Rows);
        Assert.True(row.Status == "GATED", $"{row.Status}: {row.Reason}");
        Assert.True(released.Receipt is null || released.Receipt.Total == 0m);
    }

    private static async Task<WindowBookingResponse> QuoteAsync(HttpClient client, string orderNo, DateOnly? paidUntil, CancellationToken ct) =>
        await EventuallyAsync(
            async () =>
            {
                var url = $"{Window}/bookings?orderNo={Uri.EscapeDataString(orderNo)}" + (paidUntil is { } d ? $"&paidUntil={d:yyyy-MM-dd}" : "");
                var r = await client.GetAsync(url, ct);   // 404 until Revenue hears of the booking
                return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<WindowBookingResponse>(ct) : null;
            },
            q => q?.Boxes.Count == 1 && q.Boxes[0].NextMovementCode == "MTY_OUT", "Revenue to learn the pick-up", ct) ?? throw new InvalidOperationException();

    private static object Pay(WindowBookingResponse quote, DateOnly? paidUntil) => new
    {
        bookingId = quote.BookingId,
        bookingContainerIds = quote.Boxes.Select(b => b.BookingContainerId).ToArray(),
        paidUntil,
        payments = new object[] { new { channel = "CASH", amount = quote.Total } },
        expectedTotal = quote.Total,
    };

    private static async Task<TripSaveResponse> SavedAsync(HttpClient client, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Gate}/trips") { Content = JsonContent.Create(body) };
        request.Headers.Add(Idempotency.Header, Guid.NewGuid().ToString());
        var saved = await client.SendAsync(request, ct);
        Assert.True(saved.StatusCode == HttpStatusCode.Created, $"save returned {(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync(ct)}");
        return (await saved.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!;
    }

    private static async Task<GatePreflightResponse> PreflightAsync(HttpClient client, string box, CancellationToken ct) =>
        (await client.GetFromJsonAsync<GatePreflightResponse>($"{Gate}/preflight?branchId={SctLcb01}&containerNo={box}&direction=OUT", ct))!;

    /// <summary>
    /// The barrier stops asking for a coupon. (A preflight from outside the truck still sees the box reserved by the planned
    /// pick-up — BOX_RESERVED — so the gate-out Save that follows is what proves the box goes out.)
    /// </summary>
    private static Task ClearedAsync(HttpClient client, string box, string waitingFor, CancellationToken ct) =>
        EventuallyAsync(() => PreflightAsync(client, box, ct), v => v.Findings.All(f => f.Code != "NO_COUPON"), waitingFor, ct);

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
}
