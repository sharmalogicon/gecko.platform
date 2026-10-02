using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Revenue.Endpoints.Window;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Gate charging, spec step 3 (GATE_CHARGING_DESIGN.md §2-§5, §7) end to end
/// through the real host: TOS → Revenue → TOS, on FIXTURE data only (SCT, other
/// tenant SSS — never KORAKIT).
///
/// SCT's order type GATE TEST (gecko_master dev_09, rates gecko_revenue dev_02)
/// is a copy of IMP CY/CY: FULL_IN owes nothing; FULL_OUT owes GATETRIP (the
/// PER_TRIP gate charge, ฿100 any truck / ฿140 18_WHEEL, credit ฿90) and GATEFEE
/// (฿150 per box) in cash, and LIFTCR (฿300) on credit; MTY_IN owes GATETRIP.
/// VASWASH (฿200) and VASSEAL (no rate) are its gate VAS.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class GateChargingFlowTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private const string Window = "/api/revenue/window";
    private const string Terms = "/api/master/haulier-charge-terms";
    private const string OrderType = "GATE TEST";
    private const string Prefix = "ZZK-";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewRef() => $"{Prefix}{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static string NewBox()
    {
        var ten = $"ZZKU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    private static async Task<BookingDetailResponse> BookAsync(HttpClient client, string carrierRef, string[] boxes, CancellationToken ct,
        string? haulierCode = null)
    {
        var response = await client.PostAsJsonAsync("/api/tos/bookings", new
        {
            branchId = SctLcb01, orderTypeCode = OrderType, lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef, haulierCode,
            validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            requirements = new object[] { new { equipmentTypeCode = "20GP", qty = boxes.Length } },
            containers = boxes.Select(b => new { containerNo = b }).ToArray(),
        }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"booking returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!;
    }

    /// <summary>A full drop-off (FULL_IN) with every §2.1 field; a new truck unless <paramref name="visitId"/>.</summary>
    private static async Task<GateTransactionResponse> DropOffAsync(HttpClient client, string box, CancellationToken ct, Guid? visitId = null)
    {
        await EventuallyAsync(() => PreflightAsync(client, box, "IN", ct), v => v.Decision == "ALLOWED", $"{box} allowed in", ct);
        var response = await client.PostAsJsonAsync($"{Gate}/transactions", new
        {
            branchId = SctLcb01, containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT",
            truckVisitId = visitId,
            truck = visitId is null ? new { plate = "70-1111", haulierCode = "HAU-LCH" } : null,
            grossWeightKg = 22000m, tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 19800m,
            seals = new object[] { new { sealNo = $"ZZK-{box[^4..]}", sealType = "LINE", isIntact = true } },
        }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"gate-in returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<GateTransactionResponse>(ct))!;
    }

    private static async Task<GatePreflightResponse> PreflightAsync(HttpClient client, string box, string direction, CancellationToken ct) =>
        (await client.GetFromJsonAsync<GatePreflightResponse>($"{Gate}/preflight?branchId={SctLcb01}&containerNo={box}&direction={direction}", ct))!;

    private static string QuoteUrl(string orderNo, string query = "") => $"{Window}/bookings?orderNo={Uri.EscapeDataString(orderNo)}{query}";

    private static async Task<WindowBookingResponse> QuoteAsync(HttpClient client, string orderNo, CancellationToken ct, string query = "")
    {
        var response = await client.GetAsync(QuoteUrl(orderNo, query), ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"quote returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<WindowBookingResponse>(ct))!;
    }

    /// <summary>
    /// The quote once Revenue has caught up. Revenue learns a booking from TOS's
    /// outbox a moment after it is made, so until then the window answers 404 —
    /// "not yet", not a failure. Any other status fails at once.
    /// </summary>
    private static async Task<WindowBookingResponse> QuoteWhenAsync(HttpClient client, string orderNo, Func<WindowBookingResponse, bool> done,
        string waitingFor, CancellationToken ct, string query = "") =>
        (await EventuallyAsync(async () =>
        {
            var response = await client.GetAsync(QuoteUrl(orderNo, query), ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"quote returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
            return await response.Content.ReadFromJsonAsync<WindowBookingResponse>(ct);
        }, q => q is not null && done(q), waitingFor, ct))!;

    private static async Task ExpectFieldAsync(HttpResponseMessage response, string field, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400 on {field}, got {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"expected an error on '{field}': {body}");
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

    private static async Task CleanAsync(string carrierRef, Guid? shiftId)
    {
        await TestDatabase.RemoveCashWindowAsync(carrierRef, shiftId);
        await TestDatabase.RemoveGateAsync(carrierRef);
        await TestDatabase.RemoveBookingsAsync(carrierRef);
        await TestDatabase.RemoveHaulierTermsAsync(OrderType);
        await TestDatabase.SetSctSettingAsync("gate.default_truck_category", null, null);
        await TestDatabase.SetSctSettingAsync("gate.gate_charge_only_order_types", SctLcb01, null);
    }

    // ── S1: the cash window ──────────────────────────────────────────────────

    [Fact]
    public async Task The_truck_category_the_haulier_term_the_VAS_and_the_gate_charge_only_rule_shape_the_window_quote()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        Guid? shiftId = null;
        try
        {
            var booking = await BookAsync(client, carrierRef, [box], ct);
            var orderNo = booking.Booking.OrderNo;

            // FULL_IN is a full drop-off: no VAS offered, so ticking one is a 400 (§3 f).
            var arriving = await QuoteWhenAsync(client, orderNo, q => q.Boxes.Single().NextMovementCode == "FULL_IN", "Revenue to see the booking", ct);
            Assert.False(arriving.Boxes.Single().VasOffered);
            await ExpectFieldAsync(await client.GetAsync(QuoteUrl(orderNo, "&vas=VASWASH"), ct), "vas", ct);
            await ExpectFieldAsync(await client.GetAsync(QuoteUrl(orderNo, "&truckCategoryCode=3_WHEEL"), ct), "truckCategoryCode", ct);
            await ExpectFieldAsync(await client.GetAsync(QuoteUrl(orderNo, "&haulierCode=NOBODY"), ct), "haulierCode", ct);

            await DropOffAsync(client, box, ct);
            var plain = await QuoteWhenAsync(client, orderNo, q => q.Boxes.Single().NextMovementCode == "FULL_OUT", "Revenue to see the gate-in", ct);

            // ── no category, no default: the any-truck rate. Credit is shown, not charged.
            var due = plain.Boxes.Single();
            Assert.True(due.VasOffered);
            Assert.Equal([("GATEFEE", 150.00m), ("GATETRIP", 100.00m)], due.Due.Select(d => (d.ChargeCode, d.Amount)).OrderBy(d => d.ChargeCode));
            Assert.Equal("PER_TRIP", due.Due.Single(d => d.ChargeCode == "GATETRIP").BillingUnitCode);
            Assert.Equal(267.50m, plain.Total);
            Assert.Null(plain.TruckCategoryCode);
            var credit = Assert.Single(due.BilledLater!);
            Assert.Equal(("LIFTCR", "CREDIT", "CUSTOMER", 300.00m, 21.00m, false), (credit.ChargeCode, credit.PaymentTermCode, credit.BillTo, credit.Amount, credit.TaxAmount, credit.ByHaulierTerm));
            Assert.Equal((300.00m, 21.00m, 321.00m), (plain.BilledLater!.Subtotal, plain.BilledLater.Tax, plain.BilledLater.Total));

            // ── the truck axis (§3 b): 18_WHEEL has its own rate
            var big = await QuoteAsync(client, orderNo, ct, "&truckCategoryCode=18_WHEEL");
            Assert.Equal(140.00m, big.Boxes.Single().Due.Single(d => d.ChargeCode == "GATETRIP").Amount);
            Assert.Equal(("18_WHEEL", 310.30m), (big.TruckCategoryCode, big.Total));

            // ── the tenant default (§7.4) applies when the clerk names none
            await TestDatabase.SetSctSettingAsync("gate.default_truck_category", null, "18_WHEEL");
            var defaulted = await QuoteAsync(client, orderNo, ct);
            Assert.Equal(("18_WHEEL", 310.30m), (defaulted.TruckCategoryCode, defaulted.Total));
            await TestDatabase.SetSctSettingAsync("gate.default_truck_category", null, null);

            // ── VAS (§3 f): a pick-up offers it; the priced one is a cash line, the unpriced one is in the trail
            var vas = await QuoteAsync(client, orderNo, ct, "&vas=VASWASH&vas=VASSEAL");
            var wash = Assert.Single(vas.Boxes.Single().Due, d => d.ChargeCode == "VASWASH");
            Assert.Equal(("VAS", 200.00m), (wash.Kind, wash.Amount));
            Assert.Contains(vas.Boxes.Single().Tried, t => t.ChargeCode == "VASSEAL" && t.Outcome == "UNPRICED");
            Assert.DoesNotContain(vas.Boxes.Single().Due, d => d.ChargeCode == "VASSEAL");
            await ExpectFieldAsync(await client.GetAsync(QuoteUrl(orderNo, "&vas=LIFTCR"), ct), "vas", ct);

            // ── the haulier's own term (§3 c): GATEFEE on credit for HAU-SHT only
            var created = await client.PostAsJsonAsync(Terms, new
            {
                haulierCode = "HAU-SHT", orderTypeCode = OrderType, movementCode = "FULL_OUT", chargeCode = "GATEFEE", paymentTermCode = "CREDIT",
            }, ct);
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));

            var sht = await QuoteAsync(client, orderNo, ct, "&haulierCode=HAU-SHT");
            Assert.Equal("HAU-SHT", sht.HaulierCode);
            Assert.Equal(["GATETRIP"], sht.Boxes.Single().Due.Select(d => d.ChargeCode));
            var moved = Assert.Single(sht.Boxes.Single().BilledLater!, l => l.ChargeCode == "GATEFEE");
            Assert.Equal(("CREDIT", "CUSTOMER", 150.00m, true), (moved.PaymentTermCode, moved.BillTo, moved.Amount, moved.ByHaulierTerm));
            Assert.Contains(sht.Boxes.Single().Tried, t => t.ChargeCode == "GATEFEE" && t.Outcome == "HAULIER_CREDIT" && t.Note!.Contains("HAU-SHT"));
            Assert.Equal(107.00m, sht.Total);

            var lch = await QuoteAsync(client, orderNo, ct, "&haulierCode=HAU-LCH");
            Assert.Contains(lch.Boxes.Single().Due, d => d.ChargeCode == "GATEFEE");

            // Another tenant sees neither the term nor the booking.
            var sss = await api.ClientForAsync(TosApiFactory.SssOwner);
            Assert.Empty((await sss.GetFromJsonAsync<List<JsonElement>>($"{Terms}?orderTypeCode={Uri.EscapeDataString(OrderType)}", ct))!);
            Assert.Equal(HttpStatusCode.NotFound, (await sss.GetAsync(QuoteUrl(orderNo), ct)).StatusCode);

            // ── "load only gate charge for" (§3 g): the gate charge stays, other cash goes, credit is kept
            await TestDatabase.SetSctSettingAsync("gate.gate_charge_only_order_types", SctLcb01, $"[\"{OrderType}\"]");
            var only = await QuoteAsync(client, orderNo, ct, "&vas=VASWASH");
            Assert.Equal(["GATETRIP"], only.Boxes.Single().Due.Select(d => d.ChargeCode));
            Assert.Contains(only.Boxes.Single().Tried, t => t.ChargeCode == "GATEFEE" && t.Outcome == "GATE_CHARGE_ONLY");
            Assert.Contains(only.Boxes.Single().Tried, t => t.ChargeCode == "VASWASH" && t.Outcome == "GATE_CHARGE_ONLY");
            Assert.Equal(["LIFTCR"], only.Boxes.Single().BilledLater!.Select(l => l.ChargeCode));
            await TestDatabase.SetSctSettingAsync("gate.gate_charge_only_order_types", SctLcb01, null);

            // ── the receipt is re-quoted with the same truck: a stale total is a 409, the right one pays
            shiftId = await OpenDrawerAsync(client, ct);
            object Pay(decimal expected) => new
            {
                bookingId = booking.Booking.BookingId, bookingContainerIds = new[] { due.BookingContainerId },
                payments = new object[] { new { channel = "CASH", amount = expected } }, expectedTotal = expected,
                truckCategoryCode = "18_WHEEL", haulierCode = "HAU-SHT",
            };
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{Window}/receipts", Pay(107.00m), ct)).StatusCode);
            var paid = await client.PostAsJsonAsync($"{Window}/receipts", Pay(149.80m), ct);
            Assert.True(paid.StatusCode == HttpStatusCode.Created, await paid.Content.ReadAsStringAsync(ct));
            var receipt = (await paid.Content.ReadFromJsonAsync<ReceiptResponse>(ct))!;
            var line = Assert.Single(receipt.Lines);
            Assert.Equal(("GATETRIP", 140.00m, 9.80m), (line.ChargeCode, line.Amount, line.TaxAmount));
        }
        finally { await CleanAsync(carrierRef, shiftId); }
    }

    // ── S3 = PLAN_BILLING 6.3: credit accrual from the gate event ────────────

    private static async Task<GateTransactionResponse> PickUpAsync(HttpClient client, string box, CancellationToken ct, Guid? visitId = null)
    {
        await EventuallyAsync(() => PreflightAsync(client, box, "OUT", ct), v => v.Decision == "ALLOWED", $"{box} allowed out", ct);
        var response = await client.PostAsJsonAsync($"{Gate}/transactions", new
        {
            branchId = SctLcb01, containerNo = box, direction = "OUT", tripType = "PICK_UP_CONT",
            truckVisitId = visitId,
            truck = visitId is null ? new { plate = "70-2222", haulierCode = "HAU-SHT", truckCategoryCode = "18_WHEEL" } : null,
            seals = new object[] { new { sealNo = $"ZZK-{box[^4..]}", sealType = "LINE", isIntact = true } },
        }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"gate-out returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<GateTransactionResponse>(ct))!;
    }

    private static async Task<List<TestDatabase.ChargeRow>> ChargesWhenAsync(string carrierRef, Func<List<TestDatabase.ChargeRow>, bool> done,
        string waitingFor, CancellationToken ct) =>
        await EventuallyAsync(() => TestDatabase.ChargesAsync(carrierRef), done, waitingFor, ct);

    [Fact]
    public async Task Credit_accrues_at_the_gate_with_the_gate_charge_once_per_truck_visit_a_replay_changes_nothing_and_a_void_moves_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var boxes = new[] { NewBox(), NewBox() }.Order().ToArray();
        Guid? shiftId = null;
        await TestDatabase.RequireCouponAtAsync(SctLcb01, true);
        try
        {
            // HAU-SHT's trucks put the gate charge on credit (§3 c).
            var term = await client.PostAsJsonAsync(Terms, new
            {
                haulierCode = "HAU-SHT", orderTypeCode = OrderType, movementCode = "FULL_OUT", chargeCode = "GATETRIP", paymentTermCode = "CREDIT",
            }, ct);
            Assert.True(term.StatusCode == HttpStatusCode.Created, await term.Content.ReadAsStringAsync(ct));

            var booking = await BookAsync(client, carrierRef, boxes, ct);
            var inA = await DropOffAsync(client, boxes[0], ct);
            await DropOffAsync(client, boxes[1], ct, inA.TruckVisitId);
            Assert.Empty(await TestDatabase.ChargesAsync(carrierRef));   // FULL_IN owes nothing

            // The window: the gate charge is billed later (once), the per-box fee is paid now.
            var quote = await QuoteWhenAsync(client, booking.Booking.OrderNo, q => q.Boxes.All(b => b.NextMovementCode == "FULL_OUT"),
                "Revenue to see both gate-ins", ct, "&haulierCode=HAU-SHT&truckCategoryCode=18_WHEEL");
            Assert.Equal(321.00m, quote.Total);
            Assert.Single(quote.Boxes.SelectMany(b => b.BilledLater!), l => l.ChargeCode == "GATETRIP");
            shiftId = await OpenDrawerAsync(client, ct);
            var paid = await client.PostAsJsonAsync($"{Window}/receipts", new
            {
                bookingId = booking.Booking.BookingId, bookingContainerIds = quote.Boxes.Select(b => b.BookingContainerId).ToArray(),
                haulierCode = "HAU-SHT", truckCategoryCode = "18_WHEEL",
                payments = new object[] { new { channel = "CASH", amount = 321.00m } }, expectedTotal = 321.00m,
            }, ct);
            Assert.True(paid.StatusCode == HttpStatusCode.Created, await paid.Content.ReadAsStringAsync(ct));

            // ── both boxes leave on ONE truck: the cash is earned on the visit, the credit accrues, the gate charge once
            var outA = await PickUpAsync(client, boxes[0], ct);
            var outB = await PickUpAsync(client, boxes[1], ct, outA.TruckVisitId);
            var visit = outA.TruckVisitId;
            bool Settled(List<TestDatabase.ChargeRow> rows) =>
                rows.Count(c => c.Status == "EARNED") == 2 && rows.Count(c => c.Source == "GATE" && c.ChargeCode == "LIFTCR") == 2;
            var charges = await ChargesWhenAsync(carrierRef, Settled, "both gate-outs to be billed", ct);

            Assert.All(charges.Where(c => c.Status == "EARNED"), c => Assert.Equal(("GATEFEE", visit), (c.ChargeCode, c.TruckVisitId)));
            var lifts = charges.Where(c => c.ChargeCode == "LIFTCR").ToList();
            Assert.All(lifts, c => Assert.Equal(("UNBILLED", "CREDIT", 300.00m, 21.00m, visit), (c.Status, c.PaymentTermCode, c.Amount, c.TaxAmount, c.TruckVisitId)));
            var trip = Assert.Single(charges, c => c.ChargeCode == "GATETRIP");
            Assert.Equal(("GATE", "UNBILLED", "CREDIT", 90.00m, true, outA.GateTransactionId, (Guid?)visit),
                (trip.Source, trip.Status, trip.PaymentTermCode, trip.Amount, trip.IsTripCharge, trip.GateTransactionId, trip.TruckVisitId));

            // ── the visit, priced (§6.2 quote-visit): cash paid now against credit billed later, to the baht
            var priced = (await client.GetFromJsonAsync<VisitQuoteResponse>($"{Window}/quote-visit?truckVisitId={visit}", ct))!;
            Assert.Equal([boxes[0], boxes[1]], priced.Boxes.Select(b => b.ContainerNo));
            Assert.Equal(new VisitMoneyResponse(300.00m, 21.00m, 321.00m), priced.PaidNow);        // 2 × GATEFEE 150
            Assert.Equal(new VisitMoneyResponse(690.00m, 48.30m, 738.30m), priced.BilledLater);    // 2 × LIFTCR 300 + GATETRIP 90
            var gateCharge = Assert.Single(priced.Boxes.SelectMany(b => b.Lines), l => l.IsGateCharge);
            Assert.Equal(("GATETRIP", "CREDIT", "CUSTOMER", 90.00m, 6.30m, 96.30m),
                (gateCharge.ChargeCode, gateCharge.PaymentTerm, gateCharge.PaymentTo, gateCharge.Amount, gateCharge.TaxAmount, gateCharge.SellingAmount));
            Assert.Contains(gateCharge, priced.Boxes[0].Lines);
            Assert.All(priced.Boxes.SelectMany(b => b.Lines).Where(l => l.PaymentTerm == "CASH"),
                l => Assert.Equal(("GATEFEE", "EARNED", true), (l.ChargeCode, l.Status, l.ReceiptNo is not null)));
            await ExpectFieldAsync(await client.GetAsync($"{Window}/quote-visit", ct), "truckVisitId", ct);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Window}/quote-visit?truckVisitId={Guid.NewGuid()}", ct)).StatusCode);
            var otherTenant = await api.ClientForAsync(TosApiFactory.SssOwner);
            Assert.Equal(HttpStatusCode.NotFound, (await otherTenant.GetAsync($"{Window}/quote-visit?truckVisitId={visit}", ct)).StatusCode);

            // ── a redelivered gate event (lost lease) changes nothing
            var replayed = await TestDatabase.ReplayAsync(outB.GateTransactionId, "ContainerGatedOut");
            await EventuallyAsync(() => TestDatabase.RevenueHandledAsync(replayed), done => done, "the replay to be handled", ct);
            var again = await TestDatabase.ChargesAsync(carrierRef);
            Assert.Equal(charges.Select(c => (c.ChargeCode, c.Status, c.GateTransactionId)).Order(), again.Select(c => (c.ChargeCode, c.Status, c.GateTransactionId)).Order());

            // ── void the box that carried the gate charge: its credit is cancelled, the gate charge moves to the box still on the truck
            var voided = await client.PostAsJsonAsync($"{Gate}/transactions/{outA.GateTransactionId}/void",
                new { reason = "keyed the wrong box", rowVersion = outA.RowVersion }, ct);
            Assert.True(voided.IsSuccessStatusCode, await voided.Content.ReadAsStringAsync(ct));
            var after = await ChargesWhenAsync(carrierRef,
                rows => rows.Any(c => c.ChargeCode == "GATETRIP" && c.Status == "UNBILLED" && c.GateTransactionId == outB.GateTransactionId),
                "the gate charge to move to the other box", ct);
            Assert.All(after.Where(c => c.GateTransactionId == outA.GateTransactionId && c.Source == "GATE"),
                c => Assert.Equal("CANCELLED", c.Status));
            Assert.Contains("keyed the wrong box", after.Single(c => c.ChargeCode == "GATETRIP" && c.Status == "CANCELLED").CancelReason);
            Assert.Single(after, c => c.ChargeCode == "GATETRIP" && c.Status != "CANCELLED");
            Assert.Equal(visit, after.Single(c => c.ChargeCode == "GATETRIP" && c.Status == "UNBILLED").TruckVisitId);
            // The visit now prices the box still on the truck only, gate charge included.
            var left = (await client.GetFromJsonAsync<VisitQuoteResponse>($"{Window}/quote-visit?truckVisitId={visit}", ct))!;
            Assert.Equal([boxes[1]], left.Boxes.Select(b => b.ContainerNo));
            Assert.Equal((160.50m, 417.30m), (left.PaidNow.Total, left.BilledLater.Total));   // GATEFEE; LIFTCR 300 + GATETRIP 90
            // The cash the voided move had earned is good again for the re-gate.
            Assert.Single(after, c => c.ChargeCode == "GATEFEE" && c.Status == "PAID" && c.TruckVisitId is null);
        }
        finally
        {
            await TestDatabase.RequireCouponAtAsync(SctLcb01, null);
            await CleanAsync(carrierRef, shiftId);
        }
    }

    // ── two bookings on one truck: the gate charge is collected once, at the window (owner 2026-10-01) ──

    [Fact]
    public async Task Two_bookings_on_one_truck_pay_the_gate_charge_once_at_the_window()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var both = NewRef()[..14];   // one prefix: the two bookings share a drawer, so they are cleaned up together
        var (refA, refB) = ($"{both}-A", $"{both}-B");
        var (boxA, boxB) = (NewBox(), NewBox());
        Guid? shiftId = null;
        try
        {
            var a = await BookAsync(client, refA, [boxA], ct);
            var b = await BookAsync(client, refB, [boxB], ct);
            await DropOffAsync(client, boxA, ct);
            await DropOffAsync(client, boxB, ct);
            await QuoteWhenAsync(client, a.Booking.OrderNo, q => q.Boxes.Single().NextMovementCode == "FULL_OUT", "Revenue to see A's gate-in", ct);
            var alone = await QuoteWhenAsync(client, b.Booking.OrderNo, q => q.Boxes.Single().NextMovementCode == "FULL_OUT", "Revenue to see B's gate-in", ct);
            Assert.Equal(267.50m, alone.Total);   // its own truck: (100 + 150) × 1.07
            Assert.Null(alone.GateChargeCarriedBy);

            var together = $"&sameTruckAs={Uri.EscapeDataString(a.Booking.OrderNo)}";
            await ExpectFieldAsync(await client.GetAsync(QuoteUrl(b.Booking.OrderNo, $"&sameTruckAs={Uri.EscapeDataString(b.Booking.OrderNo)}"), ct), "sameTruckAs", ct);
            await ExpectFieldAsync(await client.GetAsync(QuoteUrl(b.Booking.OrderNo, "&sameTruckAs=ZZ-NO-SUCH-ORDER"), ct), "sameTruckAs", ct);
            // A's gate charge is still to be paid: B cannot lean on it yet.
            Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(QuoteUrl(b.Booking.OrderNo, together), ct)).StatusCode);

            shiftId = await OpenDrawerAsync(client, ct);
            object Pay(BookingDetailResponse booking, WindowBookingResponse quote, decimal expected, string[]? sameTruckAs = null) => new
            {
                bookingId = booking.Booking.BookingId, bookingContainerIds = quote.Boxes.Select(x => x.BookingContainerId).ToArray(),
                payments = new object[] { new { channel = "CASH", amount = expected } }, expectedTotal = expected, sameTruckAs,
            };
            var paidA = await client.PostAsJsonAsync($"{Window}/receipts", Pay(a, await QuoteAsync(client, a.Booking.OrderNo, ct), 267.50m), ct);
            Assert.True(paidA.StatusCode == HttpStatusCode.Created, await paidA.Content.ReadAsStringAsync(ct));

            // Now B, on the same truck: the per-box fee only, and the trail says who carries the gate charge.
            var shared = await QuoteAsync(client, b.Booking.OrderNo, ct, together);
            Assert.Equal(["GATEFEE"], shared.Boxes.Single().Due.Select(d => d.ChargeCode));
            Assert.Equal(160.50m, shared.Total);
            Assert.Contains(boxA, shared.GateChargeCarriedBy);
            var waived = Assert.Single(shared.Boxes.Single().Tried, t => t.ChargeCode == "GATETRIP");
            Assert.Equal("PER_TRIP_ON_OTHER_BOX", waived.Outcome);
            Assert.Contains(a.Booking.OrderNo, waived.Note);

            // The receipt is re-quoted the same way: without the other booking named, 160.50 is not the price.
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{Window}/receipts", Pay(b, shared, 160.50m), ct)).StatusCode);
            var paidB = await client.PostAsJsonAsync($"{Window}/receipts", Pay(b, shared, 160.50m, [a.Booking.OrderNo]), ct);
            Assert.True(paidB.StatusCode == HttpStatusCode.Created, await paidB.Content.ReadAsStringAsync(ct));
            Assert.Equal(["GATEFEE"], (await paidB.Content.ReadFromJsonAsync<ReceiptResponse>(ct))!.Lines.Select(l => l.ChargeCode));

            // Both leave on ONE truck: three cash lines earned on the visit, one gate charge between them.
            var outA = await PickUpAsync(client, boxA, ct);
            await PickUpAsync(client, boxB, ct, outA.TruckVisitId);
            var charges = await ChargesWhenAsync(both, rows => rows.Count(c => c.Status == "EARNED") == 3, "both gate-outs to be earned", ct);
            var trip = Assert.Single(charges, c => c.ChargeCode == "GATETRIP");
            Assert.Equal((boxA, "EARNED", (Guid?)outA.TruckVisitId), (trip.ContainerNo, trip.Status, trip.TruckVisitId));
        }
        finally { await CleanAsync(both, shiftId); }
    }

    // ── gate-out release gate B2.3: a gate-out is later than the box's previous move ──

    [Fact]
    public async Task A_gate_out_timed_before_the_box_came_in_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        try
        {
            await BookAsync(client, carrierRef, [box], ct);
            var gateIn = await DropOffAsync(client, box, ct);

            var before = Uri.EscapeDataString(gateIn.TransactionAt.AddHours(-1).ToString("O"));
            var early = (await client.GetFromJsonAsync<GatePreflightResponse>(
                $"{Gate}/preflight?branchId={SctLcb01}&containerNo={box}&direction=OUT&at={before}", ct))!;
            Assert.Equal("BLOCKED", early.Decision);
            Assert.Equal("BLOCK", Assert.Single(early.Findings, f => f.Code == "BEFORE_PREVIOUS_MOVE").Severity);

            // Recording it backdated is refused the same way; at the real time the rule says nothing.
            var backdated = await client.PostAsJsonAsync($"{Gate}/transactions", new
            {
                branchId = SctLcb01, containerNo = box, direction = "OUT", tripType = "PICK_UP_CONT",
                transactionAt = gateIn.TransactionAt.AddHours(-1),
                truck = new { plate = "70-4444" },
                seals = new object[] { new { sealNo = $"ZZK-{box[^4..]}", sealType = "LINE", isIntact = true } },
            }, ct);
            Assert.Equal(HttpStatusCode.Conflict, backdated.StatusCode);
            Assert.Contains("BEFORE_PREVIOUS_MOVE", await backdated.Content.ReadAsStringAsync(ct));
            Assert.DoesNotContain((await PreflightAsync(client, box, "OUT", ct)).Findings, f => f.Code == "BEFORE_PREVIOUS_MOVE");
        }
        finally { await CleanAsync(carrierRef, null); }
    }

    // ── withholding tax: the clerk's choice on a receipt over 1,000 (owner 2026-10-01; Vector GateIn.cs:2118, 3214) ──

    [Fact]
    public async Task Withholding_tax_is_offered_over_a_thousand_and_comes_off_what_is_paid_not_off_the_invoice()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var boxes = new[] { NewBox(), NewBox(), NewBox(), NewBox() };
        Guid? shiftId = null;
        try
        {
            var booking = await BookAsync(client, carrierRef, boxes, ct);
            foreach (var box in boxes) await DropOffAsync(client, box, ct);

            // ฿749.00: not over a thousand, so it is not offered — and cannot be asked for.
            var small = await QuoteWhenAsync(client, booking.Booking.OrderNo, q => q.Boxes.All(b => b.NextMovementCode == "FULL_OUT"),
                "Revenue to see the gate-ins", ct);
            Assert.Equal(749.00m, small.Total);   // (100 + 4 × 150) × 1.07
            Assert.Null(small.WithholdingTax);
            shiftId = await OpenDrawerAsync(client, ct);
            object Pay(decimal amount, decimal expected, string[]? vas, bool withholdingTax) => new
            {
                bookingId = booking.Booking.BookingId, bookingContainerIds = small.Boxes.Select(b => b.BookingContainerId).ToArray(),
                payments = new object[] { new { channel = "CASH", amount } }, expectedTotal = expected, vas, withholdingTax,
            };
            await ExpectFieldAsync(await client.PostAsJsonAsync($"{Window}/receipts", Pay(749.00m, 749.00m, null, true), ct), "withholdingTax", ct);

            // With the wash on every box it is ฿1,605.00: 3% of the 1,500 before VAT may be withheld.
            var big = await QuoteAsync(client, booking.Booking.OrderNo, ct, "&vas=VASWASH");
            Assert.Equal((1500.00m, 105.00m, 1605.00m), (big.Subtotal, big.Tax, big.Total));
            Assert.Equal(new WithholdingTaxResponse(3m, 45.00m, 1560.00m), big.WithholdingTax);

            // Applied: the payments are the nett, not the total.
            await ExpectFieldAsync(await client.PostAsJsonAsync($"{Window}/receipts", Pay(1605.00m, 1605.00m, ["VASWASH"], true), ct), "payments", ct);
            var paid = await client.PostAsJsonAsync($"{Window}/receipts", Pay(1560.00m, 1605.00m, ["VASWASH"], true), ct);
            Assert.True(paid.StatusCode == HttpStatusCode.Created, await paid.Content.ReadAsStringAsync(ct));
            var receipt = (await paid.Content.ReadFromJsonAsync<ReceiptResponse>(ct))!;
            Assert.Equal((1500.00m, 105.00m, 1605.00m), (receipt.Subtotal, receipt.Tax, receipt.Total));   // the tax invoice is unchanged
            Assert.Equal(((decimal?)3m, 45.00m, 1560.00m), (receipt.WithholdingTaxRate, receipt.WithholdingTaxAmount, receipt.NettAmount));

            // The drawer holds what was actually handed over, and the receipt still prints.
            var drawer = (await client.GetFromJsonAsync<ShiftResponse>($"{Window}/shifts/current?branchId={SctLcb01}", ct))!;
            Assert.Equal(1560.00m, drawer.Expected.Single(e => e.Channel == "CASH").Amount);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Window}/receipts/{receipt.ReceiptId}/receipt.pdf", ct)).StatusCode);
        }
        finally { await CleanAsync(carrierRef, shiftId); }
    }

    // ── the truck at the gate is not the truck that was paid for (§7.4, §7.5) ──

    [Fact]
    public async Task A_truck_of_another_category_or_haulier_than_paid_is_warned_about_and_still_goes()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        Guid? shiftId = null;
        try
        {
            var booking = await BookAsync(client, carrierRef, [box], ct);
            await DropOffAsync(client, box, ct);
            var quote = await QuoteWhenAsync(client, booking.Booking.OrderNo, q => q.Boxes.Single().NextMovementCode == "FULL_OUT",
                "Revenue to see the gate-in", ct, "&truckCategoryCode=18_WHEEL&haulierCode=HAU-SHT");
            Assert.Equal(310.30m, quote.Total);   // (140 + 150) × 1.07

            shiftId = await OpenDrawerAsync(client, ct);
            var paid = await client.PostAsJsonAsync($"{Window}/receipts", new
            {
                bookingId = booking.Booking.BookingId, bookingContainerIds = quote.Boxes.Select(b => b.BookingContainerId).ToArray(),
                truckCategoryCode = "18_WHEEL", haulierCode = "HAU-SHT",
                payments = new object[] { new { channel = "CASH", amount = 310.30m } }, expectedTotal = 310.30m,
            }, ct);
            Assert.True(paid.StatusCode == HttpStatusCode.Created, await paid.Content.ReadAsStringAsync(ct));

            Task<GatePreflightResponse> AskAsync(string truck) =>
                client.GetFromJsonAsync<GatePreflightResponse>($"{Gate}/preflight?branchId={SctLcb01}&containerNo={box}&direction=OUT{truck}", ct)!;
            await EventuallyAsync(() => AskAsync(""), v => v.Coupon is not null, "the paid coupon", ct);

            // The barrier is not told which truck: nothing to compare.
            Assert.DoesNotContain((await AskAsync("")).Findings, f => f.Code.EndsWith("_NOT_AS_PAID"));
            // The truck that was paid for: nothing to say.
            Assert.DoesNotContain((await AskAsync("&truckCategoryCode=18_WHEEL&haulierCode=HAU-SHT")).Findings, f => f.Code.EndsWith("_NOT_AS_PAID"));

            // Another truck: a warning for the category, a note for the haulier, and the box may still go.
            var other = await AskAsync("&truckCategoryCode=6_WHEEL&haulierCode=HAU-LCH");
            Assert.Equal("ALLOWED", other.Decision);
            var category = Assert.Single(other.Findings, f => f.Code == "TRUCK_CATEGORY_NOT_AS_PAID");
            Assert.Equal("WARN", category.Severity);
            Assert.Contains("6_WHEEL", category.Message);
            Assert.Contains("18_WHEEL", category.Message);
            var haulier = Assert.Single(other.Findings, f => f.Code == "HAULIER_NOT_AS_PAID");
            Assert.Equal("INFO", haulier.Severity);
            Assert.Contains("HAU-LCH", haulier.Message);

            // Recording the move says the same on the EIR's response; reading the EIR back does not.
            var gateOut = await client.PostAsJsonAsync($"{Gate}/transactions", new
            {
                branchId = SctLcb01, containerNo = box, direction = "OUT", tripType = "PICK_UP_CONT",
                truck = new { plate = "70-3333", haulierCode = "HAU-LCH", truckCategoryCode = "6_WHEEL" },
                seals = new object[] { new { sealNo = $"ZZK-{box[^4..]}", sealType = "LINE", isIntact = true } },
            }, ct);
            Assert.True(gateOut.StatusCode == HttpStatusCode.Created, await gateOut.Content.ReadAsStringAsync(ct));
            var eir = (await gateOut.Content.ReadFromJsonAsync<GateTransactionResponse>(ct))!;
            Assert.Equal(["HAULIER_NOT_AS_PAID", "TRUCK_CATEGORY_NOT_AS_PAID"],
                eir.Findings!.Where(f => f.Code.EndsWith("_NOT_AS_PAID")).Select(f => f.Code).Order());
            Assert.Null((await client.GetFromJsonAsync<GateTransactionResponse>($"{Gate}/transactions/{eir.GateTransactionId}", ct))!.Findings);

            // The second box of a visit is compared with the visit's truck.
            await ExpectFieldAsync(await client.GetAsync($"{Gate}/preflight?branchId={SctLcb01}&containerNo={box}&direction=OUT&truckVisitId={Guid.NewGuid()}", ct), "truckVisitId", ct);
        }
        finally { await CleanAsync(carrierRef, shiftId); }
    }

    [Fact]
    public async Task Two_boxes_on_one_truck_pay_the_gate_charge_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var boxes = new[] { NewBox(), NewBox() }.Order().ToArray();
        Guid? shiftId = null;
        try
        {
            var booking = await BookAsync(client, carrierRef, boxes, ct);
            var first = await DropOffAsync(client, boxes[0], ct);
            await DropOffAsync(client, boxes[1], ct, first.TruckVisitId);
            var quote = await QuoteWhenAsync(client, booking.Booking.OrderNo, q => q.Boxes.All(b => b.NextMovementCode == "FULL_OUT"),
                "Revenue to see both gate-ins", ct);

            // One truck: the first box carries the PER_TRIP gate charge, the second says why it has none (§7.1, §7.3).
            var (carrier, other) = (quote.Boxes.Single(b => b.ContainerNo == boxes[0]), quote.Boxes.Single(b => b.ContainerNo == boxes[1]));
            Assert.Contains(carrier.Due, d => d.ChargeCode == "GATETRIP");
            Assert.DoesNotContain(other.Due, d => d.ChargeCode == "GATETRIP");
            var waived = Assert.Single(other.Tried, t => t.ChargeCode == "GATETRIP");
            Assert.Equal("PER_TRIP_ON_OTHER_BOX", waived.Outcome);
            Assert.Contains(boxes[0], waived.Note);
            Assert.Equal(428.00m, quote.Total);   // (100 + 150 + 150) × 1.07

            // Quoted alone, the second box is its own truck and pays its own gate charge.
            var alone = await QuoteAsync(client, booking.Booking.OrderNo, ct, $"&bookingContainerIds={other.BookingContainerId}");
            Assert.Contains(alone.Boxes.Single().Due, d => d.ChargeCode == "GATETRIP");

            shiftId = await OpenDrawerAsync(client, ct);
            var paid = await client.PostAsJsonAsync($"{Window}/receipts", new
            {
                bookingId = booking.Booking.BookingId, bookingContainerIds = quote.Boxes.Select(b => b.BookingContainerId).ToArray(),
                payments = new object[] { new { channel = "CASH", amount = 428.00m } }, expectedTotal = 428.00m,
            }, ct);
            Assert.True(paid.StatusCode == HttpStatusCode.Created, await paid.Content.ReadAsStringAsync(ct));
            var receipt = (await paid.Content.ReadFromJsonAsync<ReceiptResponse>(ct))!;
            Assert.Equal(1, receipt.Lines.Count(l => l.ChargeCode == "GATETRIP"));
            Assert.Equal(2, receipt.Lines.Count(l => l.ChargeCode == "GATEFEE"));

            // ── the visit's mode is DERIVED from its moves: two drop-offs, then the same truck takes one away
            async Task<TruckVisitResponse> VisitAsync() => (await client.GetFromJsonAsync<TruckVisitResponse>($"{Gate}/visits/{first.TruckVisitId}", ct))!;
            Assert.Equal("DROPOFF", (await VisitAsync()).PickupDropoffMode);
            var taken = await PickUpAsync(client, boxes[0], ct, first.TruckVisitId);
            var visit = await VisitAsync();
            Assert.Equal("PICKUP_DROPOFF", visit.PickupDropoffMode);
            var listed = (await client.GetFromJsonAsync<Gecko.Data.PagedResult<TruckVisitResponse>>($"{Gate}/visits?search={visit.VisitNo}", ct))!;
            Assert.Equal("PICKUP_DROPOFF", Assert.Single(listed.Items).PickupDropoffMode);

            // A voided move never happened: the visit is a drop-off again.
            var voided = await client.PostAsJsonAsync($"{Gate}/transactions/{taken.GateTransactionId}/void",
                new { reason = "took the wrong box", rowVersion = taken.RowVersion }, ct);
            Assert.True(voided.IsSuccessStatusCode, await voided.Content.ReadAsStringAsync(ct));
            Assert.Equal("DROPOFF", (await VisitAsync()).PickupDropoffMode);
        }
        finally { await CleanAsync(carrierRef, shiftId); }
    }
}
