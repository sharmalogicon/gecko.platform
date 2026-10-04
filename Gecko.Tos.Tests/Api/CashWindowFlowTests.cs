using System.Net;
using System.Net.Http.Json;
using Gecko.Revenue.Endpoints.Charges;
using Gecko.Revenue.Endpoints.Window;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// PLAN_BILLING §4.2 end to end, through the real host with its real dispatchers
/// (TOS → Revenue → TOS), on FIXTURE data — a receipt is a gap-free tax document,
/// so no test ever writes one into a real client's books.
///
/// The demo sentence: the box is refused at the barrier because nobody paid; the
/// cashier takes ฿160.50 at the window; the receipt number comes back; the coupon
/// reaches the barrier on its own; the box goes out; the charge is earned.
///
/// SCT's IMP CY/CY (FULL_IN → FULL_OUT) raises the gate fee on FULL_OUT, customer
/// cash (gecko_master dev_08), priced ฿150 by PUB-LCB. FULL_IN owes no cash, so
/// Revenue issues its coupon by itself. The test switches the coupon rule ON for
/// SCT-LCB01 (the fixture tenants run it as a warning) and back off at the end.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class CashWindowFlowTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private const string Window = "/api/revenue/window";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    /// <summary>A free SCT registry 40GP (shared with GateApiTests; the collection runs one test at a time).</summary>
    private const string Box = "APZU4230891";

    [Fact]
    public async Task An_unpaid_box_is_refused_and_goes_out_once_the_window_has_taken_the_money()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = $"ZZC-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        Guid? shiftId = null;

        await TestDatabase.RequireCouponAtAsync(SctLcb01, true);
        try
        {
            // ── the booking reaches Revenue, and FULL_IN (no cash) gets its coupon by itself
            var created = await client.PostAsJsonAsync("/api/tos/bookings", new
            {
                branchId = SctLcb01, orderTypeCode = "IMP CY/CY", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
                validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                requirements = new object[] { new { equipmentTypeCode = "40GP", qty = 1 } },
                containers = new object[] { new { containerNo = Box } },
            }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var booking = (await created.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!;

            var arriving = await EventuallyAsync(() => PreflightAsync(client, "IN", ct), v => v.Decision == "ALLOWED", "the automatic FULL_IN coupon", ct);
            Assert.DoesNotContain(arriving.Findings, f => f.Code == "NO_COUPON");

            var eirIn = await client.PostAsJsonAsync($"{Gate}/transactions", new
            {
                branchId = SctLcb01, containerNo = Box, direction = "IN",
                tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 18000m, customsPermitNo = "ZZ-PERMIT-1",
                truck = new { plate = "70-4321", driverName = "Somsak K." },
                grossWeightKg = 21000m, weightSource = "WEIGHBRIDGE",
                seals = new object[] { new { sealNo = "ZZ-CASH-01", sealType = "LINE", isIntact = true } },
            }, ct);
            Assert.Equal(HttpStatusCode.Created, eirIn.StatusCode);

            // ── unpaid: refused, with a sentence the clerk can say
            var refused = await PreflightAsync(client, "OUT", ct);
            Assert.Equal("BLOCKED", refused.Decision);
            var unpaid = Assert.Single(refused.Findings, f => f.Code == "NO_COUPON");
            Assert.Equal("BLOCK", unpaid.Severity);
            Assert.Contains("cash window", unpaid.Message, StringComparison.OrdinalIgnoreCase);

            // ── the window: open the drawer, see the quote
            var opened = await client.PostAsJsonAsync($"{Window}/shifts", new { branchId = SctLcb01, openingFloat = 1000m }, ct);
            Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
            shiftId = (await opened.Content.ReadFromJsonAsync<ShiftResponse>(ct))!.ShiftId;

            var quote = await EventuallyAsync(
                async () => (await client.GetFromJsonAsync<WindowBookingResponse>($"{Window}/bookings?orderNo={Uri.EscapeDataString(booking.Booking.OrderNo)}", ct))!,
                q => q.Boxes.Single().NextMovementCode == "FULL_OUT", "Revenue to see the gate-in", ct);
            var due = Assert.Single(quote.Boxes.Single().Due);
            Assert.Equal(("GATEFEE", "CUSTOMER", 150.00m, 10.50m), (due.ChargeCode, due.BillTo, due.Amount, due.TaxAmount));

            // gecko_revenue 23: the booking statement already shows what this box will be charged, per movement.
            var expected = (await client.GetFromJsonAsync<BookingStatementResponse>($"/api/revenue/charges/statement?orderNo={Uri.EscapeDataString(booking.Booking.OrderNo)}", ct))!;
            var quoted = Assert.Single(expected.Boxes.Single().Lines, l => l.Charge.Status == "QUOTED");
            Assert.Equal(("GATEFEE", "FULL_OUT", 150.00m), (quoted.Charge.ChargeCode, quoted.Charge.MovementCode, quoted.Charge.Amount));
            Assert.Equal(160.50m, expected.Totals.ExpectedCash);
            Assert.Equal(160.50m, quote.Total);

            // "Today" is the DEPOT's day (Laem Chabang, +07:00), not the server's or the browser's.
            var bangkok = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");
            var depotToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, bangkok).DateTime);
            Assert.Equal(depotToday, quote.Today);
            var yesterday = await client.GetAsync(
                $"{Window}/bookings?orderNo={Uri.EscapeDataString(booking.Booking.OrderNo)}&paidUntil={quote.Today.AddDays(-1):yyyy-MM-dd}", ct);
            Assert.Equal(HttpStatusCode.BadRequest, yesterday.StatusCode);

            // A total the cashier did not see is refused, never silently charged.
            var stale = await client.PostAsJsonAsync($"{Window}/receipts", Pay(booking, quote, expected: 150m, cash: 150m), ct);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

            // ── take the money
            var paid = await client.PostAsJsonAsync($"{Window}/receipts", Pay(booking, quote, expected: 160.50m, cash: 160.50m, tendered: 200m), ct);
            Assert.True(paid.StatusCode == HttpStatusCode.Created, await paid.Content.ReadAsStringAsync(ct));
            var receipt = (await paid.Content.ReadFromJsonAsync<ReceiptResponse>(ct))!;
            Assert.Matches(@"^RCT-SCT-LCB01-\d{4}-\d{5}$", receipt.ReceiptNo);
            Assert.Equal((160.50m, 39.50m), (receipt.Total, receipt.Change));
            var coupon = Assert.Single(receipt.Coupons);
            Assert.Equal(("FULL_OUT", Box), (coupon.MovementCode, coupon.ContainerNo));

            // ── the seller block: SCT-LCB01 invoices as SCT-HQ, head office, straight from MDM
            var seller = receipt.Seller!;
            Assert.NotNull(receipt.Seller);
            Assert.Equal("SCT-HQ", seller.CompanyCode);
            Assert.False(string.IsNullOrWhiteSpace(seller.NameEn));
            Assert.Matches(@"^\d{13}$", seller.TaxId!);
            Assert.Equal(("00000", (bool?)true), (seller.TaxBranchNo, seller.IsHeadOffice));
            Assert.False(string.IsNullOrWhiteSpace(seller.Address));
            Assert.Equal(("SCT-LCB01", shiftId), (receipt.BranchCode, (Guid?)receipt.ShiftId));
            Assert.Equal(TimeSpan.FromHours(7), receipt.ReceiptAt.Offset);

            // ── the receipt as an A4 tax invoice
            var pdf = await client.GetAsync($"{Window}/receipts/{receipt.ReceiptId}/receipt.pdf", ct);
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
            var bytes = await pdf.Content.ReadAsByteArrayAsync(ct);
            Assert.Equal("%PDF"u8.ToArray(), bytes[..4]);
            Assert.Contains(receipt.ReceiptNo, pdf.Content.Headers.ContentDisposition?.FileNameStar ?? pdf.Content.Headers.ContentDisposition?.FileName ?? "");

            // ── the drawer's own list, for a reprint
            var list = await client.GetFromJsonAsync<List<ShiftReceiptResponse>>($"{Window}/shifts/{shiftId}/receipts", ct);
            var listed = Assert.Single(list!);
            Assert.Equal((receipt.ReceiptId, receipt.ReceiptNo, 160.50m, "ISSUED", booking.Booking.OrderNo),
                (listed.ReceiptId, listed.ReceiptNo, listed.Total, listed.Status, listed.OrderNo));
            // Someone else's drawer is not a list another user can read.
            var gateClerk = await api.ClientForAsync(TosApiFactory.SctGateLcb);
            Assert.Equal(HttpStatusCode.Forbidden, (await gateClerk.GetAsync($"{Window}/shifts/{shiftId}/receipts", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Window}/shifts/{Guid.NewGuid()}/receipts", ct)).StatusCode);

            // Paying twice is impossible: nothing is left to pay.
            var again = await client.PostAsJsonAsync($"{Window}/receipts", Pay(booking, quote, expected: 160.50m, cash: 160.50m), ct);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

            // ── the coupon reaches the barrier by itself (Revenue outbox → TOS)
            var cleared = await EventuallyAsync(() => PreflightAsync(client, "OUT", ct), v => v.Decision == "ALLOWED", "the paid coupon", ct);
            Assert.Equal(coupon.CouponRef, cleared.Coupon!.CouponRef);

            var eirOut = await client.PostAsJsonAsync($"{Gate}/transactions", new
            {
                branchId = SctLcb01, containerNo = Box, direction = "OUT", tripType = "PICK_UP_CONT",
                truck = new { plate = "70-8765", driverName = "Prasert W." },
                seals = new object[] { new { sealNo = "ZZ-CASH-01", sealType = "LINE", isIntact = true } },
            }, ct);
            Assert.Equal(HttpStatusCode.Created, eirOut.StatusCode);
            var gateOut = (await eirOut.Content.ReadFromJsonAsync<GateTransactionResponse>(ct))!;

            // ── Revenue hears the gate-out: the cash is earned, and the box's stay is closed
            var settled = await EventuallyAsync(
                async () => (await client.GetFromJsonAsync<WindowBookingResponse>($"{Window}/bookings?orderNo={Uri.EscapeDataString(booking.Booking.OrderNo)}", ct))!,
                q => q.Boxes.Single().Settled.Any(c => c.Status == "EARNED"), "the charge to be earned", ct);
            var earned = Assert.Single(settled.Boxes.Single().Settled);
            // Paid and earned: the quote is retired, the statement shows the real line.
            var after = (await client.GetFromJsonAsync<BookingStatementResponse>($"/api/revenue/charges/statement?orderNo={Uri.EscapeDataString(booking.Booking.OrderNo)}", ct))!;
            Assert.DoesNotContain(after.Boxes.SelectMany(b => b.Lines), l => l.Charge.Status == "QUOTED");
            Assert.Contains(after.Boxes.SelectMany(b => b.Lines), l => l.Charge.Status == "EARNED" && l.Charge.ChargeCode == "GATEFEE");
            Assert.Equal(("GATEFEE", 160.50m, coupon.CouponRef), (earned.ChargeCode, earned.Total, earned.CouponRef));
            Assert.Equal(gateOut.GateTransactionId, await TestDatabase.StayClosedByAsync(Box));

            // ── close the drawer: 1,000 float + 160.50 taken
            var closed = await client.PostAsJsonAsync($"{Window}/shifts/{shiftId}/close",
                new { counts = new object[] { new { channel = "CASH", countedAmount = 1160.50m } } }, ct);
            Assert.True(closed.StatusCode == HttpStatusCode.OK, await closed.Content.ReadAsStringAsync(ct));
            var count = Assert.Single((await closed.Content.ReadFromJsonAsync<ShiftResponse>(ct))!.Counts);
            Assert.Equal(("CASH", 1160.50m, 0m), (count.Channel, count.Expected, count.Variance));
            shiftId = null;
        }
        finally
        {
            await TestDatabase.RequireCouponAtAsync(SctLcb01, null);
            await TestDatabase.RemoveCashWindowAsync(carrierRef, shiftId);
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    /// <summary>
    /// Owner 2026-10-03: a cash charge no tariff prices is never a free pass. Before,
    /// FULL_IN with an unpriced cash charge got an automatic coupon and went through at 0.
    /// Now: no coupon, the barrier holds the box, the window lists the charge under noPrice
    /// and refuses a receipt, and a supervisor's waiver releases the box.
    /// </summary>
    [Fact]
    public async Task A_cash_charge_no_tariff_prices_holds_the_box_until_a_supervisor_waives_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = $"ZZC-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        Guid? shiftId = null;

        await TestDatabase.RequireCouponAtAsync(SctLcb01, true);
        await TestDatabase.UnpricedChargeOnImpCyCyAsync("FULL_IN", add: true);
        try
        {
            var created = await client.PostAsJsonAsync("/api/tos/bookings", new
            {
                branchId = SctLcb01, orderTypeCode = "IMP CY/CY", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
                validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                requirements = new object[] { new { equipmentTypeCode = "40GP", qty = 1 } },
                containers = new object[] { new { containerNo = Box } },
            }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var booking = (await created.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!;
            var orderNo = Uri.EscapeDataString(booking.Booking.OrderNo);

            // The window sees the hole in the tariff.
            var quote = (await EventuallyAsync(
                async () =>
                {
                    var r = await client.GetAsync($"{Window}/bookings?orderNo={orderNo}", ct);   // 404 until Revenue hears of it
                    return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<WindowBookingResponse>(ct) : null;
                },
                q => q?.Boxes.Count == 1, "Revenue to learn the booking", ct))!;
            var box = quote.Boxes.Single();
            Assert.Equal("FULL_IN", box.NextMovementCode);
            var hole = Assert.Single(box.NoPrice!);
            Assert.Equal(("VASSEAL", "CUSTOMER", 0m), (hole.ChargeCode, hole.BillTo, hole.Amount));
            Assert.Contains("VASSEAL", box.Note);

            // No automatic coupon: the barrier holds the box.
            await Task.Delay(1500, ct);
            var held = await PreflightAsync(client, "IN", ct);
            Assert.Equal("BLOCKED", held.Decision);
            Assert.Contains(held.Findings, f => f.Code == "NO_COUPON");

            // The window will not take money around the hole.
            var opened = await client.PostAsJsonAsync($"{Window}/shifts", new { branchId = SctLcb01, openingFloat = 0m }, ct);
            Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
            shiftId = (await opened.Content.ReadFromJsonAsync<ShiftResponse>(ct))!.ShiftId;
            var refused = await client.PostAsJsonAsync($"{Window}/receipts", Pay(booking, quote, expected: 0m, cash: 1m), ct);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("No price", await refused.Content.ReadAsStringAsync(ct));

            // A supervisor waives it, with a reason: nothing else is due, so the waiver is the coupon.
            var waived = await client.PostAsJsonAsync($"{Window}/waive", new
            {
                bookingContainerId = box.BookingContainerId, chargeCode = "VASSEAL", billTo = "CUSTOMER",
                reason = "Seal fee not in the contract; waived for this release",
            }, ct);
            Assert.True(waived.StatusCode == HttpStatusCode.OK, await waived.Content.ReadAsStringAsync(ct));
            Assert.NotNull((await waived.Content.ReadFromJsonAsync<WaiveResponse>(ct))!.Coupon);

            var cleared = await EventuallyAsync(() => PreflightAsync(client, "IN", ct), v => v.Decision == "ALLOWED", "the waiver coupon", ct);
            Assert.DoesNotContain(cleared.Findings, f => f.Code == "NO_COUPON");
        }
        finally
        {
            await TestDatabase.UnpricedChargeOnImpCyCyAsync("FULL_IN", add: false);
            await TestDatabase.RequireCouponAtAsync(SctLcb01, null);
            await TestDatabase.RemoveCashWindowAsync(carrierRef, shiftId);
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    private static object Pay(BookingDetailResponse booking, WindowBookingResponse quote, decimal expected, decimal cash, decimal? tendered = null) => new
    {
        bookingId = booking.Booking.BookingId,
        bookingContainerIds = quote.Boxes.Select(b => b.BookingContainerId).ToArray(),
        payments = new object[] { new { channel = "CASH", amount = cash, tenderedAmount = tendered } },
        expectedTotal = expected,
    };

    private static async Task<GatePreflightResponse> PreflightAsync(HttpClient client, string direction, CancellationToken ct) =>
        (await client.GetFromJsonAsync<GatePreflightResponse>($"{Gate}/preflight?branchId={SctLcb01}&containerNo={Box}&direction={direction}", ct))!;

    /// <summary>The dispatchers poll every second in this host; a hop is done well inside the limit, or it is broken.</summary>
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
