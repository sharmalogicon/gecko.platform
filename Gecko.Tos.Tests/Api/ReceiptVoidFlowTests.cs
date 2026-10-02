using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Revenue.Endpoints.Charges;
using Gecko.Revenue.Endpoints.Window;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// INVOICING_PROPOSAL parts A and D end to end, through the real host with both
/// dispatchers (Revenue → TOS for the coupon, TOS → Revenue for the gate event), on
/// FIXTURE data only — a receipt is a gap-free tax document.
///
/// The story: the cashier takes ฿160.50 on the wrong receipt; accounts voids it
/// (it keeps its number, the coupon leaves the barrier, the drawer expects the
/// float again); the customer pays on a new receipt that names the void; the box
/// goes out; now the receipt cannot be voided; the booking statement shows it all.
///
/// Same money as CashWindowFlowTests: SCT IMP CY/CY, GATEFEE ฿150 + 7% on FULL_OUT.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class ReceiptVoidFlowTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private const string Window = "/api/revenue/window";
    private const string Statement = "/api/revenue/charges/statement";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    /// <summary>EDI_COORDINATOR: no revenue.receipt.void, no revenue.charge.view.</summary>
    private const string SctEdi = "edi@sct.co.th";

    private static string NewBox()
    {
        var ten = $"ZZTU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    [Fact]
    public async Task A_wrong_receipt_is_voided_replaced_and_shown_on_the_booking_statement()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = $"ZZV-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        var box = NewBox();
        Guid? shiftId = null;

        await TestDatabase.RequireCouponAtAsync(SctLcb01, true);
        try
        {
            // ── a box in the yard, owing the gate fee on its way out
            var created = await client.PostAsJsonAsync("/api/tos/bookings", new
            {
                branchId = SctLcb01, orderTypeCode = "IMP CY/CY", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
                validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                requirements = new object[] { new { equipmentTypeCode = "40GP", qty = 1 } },
                containers = new object[] { new { containerNo = box } },
            }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var booking = (await created.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!;
            var orderNo = booking.Booking.OrderNo;

            await EventuallyAsync(() => PreflightAsync(client, box, "IN", ct), v => v.Decision == "ALLOWED", "the automatic FULL_IN coupon", ct);
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{Gate}/transactions", GateMove(box, "IN"), ct)).StatusCode);

            var opened = await client.PostAsJsonAsync($"{Window}/shifts", new { branchId = SctLcb01, openingFloat = 1000m }, ct);
            Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
            shiftId = (await opened.Content.ReadFromJsonAsync<ShiftResponse>(ct))!.ShiftId;

            var quote = await EventuallyAsync(() => QuoteAsync(client, orderNo, ct),
                q => q.Boxes.Single().NextMovementCode == "FULL_OUT", "Revenue to see the gate-in", ct);
            Assert.Equal(160.50m, quote.Total);

            // ── the wrong receipt
            var first = await PayAsync(client, booking, quote, null, ct);
            await EventuallyAsync(() => PreflightAsync(client, box, "OUT", ct), v => v.Decision == "ALLOWED", "the paid coupon", ct);

            // Who may void, and with what.
            var edi = await api.ClientForAsync(SctEdi);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.PostAsJsonAsync($"{Window}/receipts/{first.ReceiptId}/void", new { reason = "wrong customer" }, ct)).StatusCode);
            await AssertFieldAsync(await client.PostAsJsonAsync($"{Window}/receipts/{first.ReceiptId}/void", new { reason = "no" }, ct), "reason", ct);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"{Window}/receipts/{Guid.NewGuid()}/void", new { reason = "wrong customer" }, ct)).StatusCode);

            // ── void it: same number, VOIDED, the reason kept
            var voidResponse = await client.PostAsJsonAsync($"{Window}/receipts/{first.ReceiptId}/void", new { reason = "Keyed the wrong customer" }, ct);
            Assert.True(voidResponse.StatusCode == HttpStatusCode.OK, await voidResponse.Content.ReadAsStringAsync(ct));
            var voided = (await voidResponse.Content.ReadFromJsonAsync<ReceiptResponse>(ct))!;
            Assert.Equal((first.ReceiptNo, "VOIDED", "Keyed the wrong customer"), (voided.ReceiptNo, voided.Status, voided.VoidReason));
            Assert.NotNull(voided.VoidedAt);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{Window}/receipts/{first.ReceiptId}/void", new { reason = "again, twice" }, ct)).StatusCode);

            // The coupon leaves the barrier by itself (Revenue outbox → TOS)...
            var blocked = await EventuallyAsync(() => PreflightAsync(client, box, "OUT", ct), v => v.Decision == "BLOCKED", "the coupon to be withdrawn", ct);
            Assert.Contains(blocked.Findings, f => f.Code == "NO_COUPON");
            // ...the drawer no longer expects the money, and the fee is due again.
            var drawer = (await client.GetFromJsonAsync<ShiftResponse>($"{Window}/shifts/current?branchId={SctLcb01}", ct))!;
            Assert.Equal(1000m, drawer.Expected.Single(e => e.Channel == "CASH").Amount);
            var again = await QuoteAsync(client, orderNo, ct);
            Assert.Equal(160.50m, again.Total);
            Assert.Equal(first.ReceiptId, Assert.Single(again.VoidedReceipts!).ReceiptId);

            // ── pay again, naming the void; a receipt that is not voided cannot be "replaced"
            await AssertFieldAsync(await client.PostAsJsonAsync($"{Window}/receipts", Pay(booking, again, Guid.NewGuid()), ct), "replacesReceiptId", ct);
            var second = await PayAsync(client, booking, again, first.ReceiptId, ct);
            Assert.NotEqual(first.ReceiptNo, second.ReceiptNo);
            Assert.Equal(first.ReceiptNo, second.ReplacesReceiptNo);
            var firstNow = (await client.GetFromJsonAsync<ReceiptResponse>($"{Window}/receipts/{first.ReceiptId}", ct))!;
            Assert.Equal(second.ReceiptNo, firstNow.ReplacedByReceiptNo);
            Assert.Empty((await QuoteAsync(client, orderNo, ct)).VoidedReceipts!);

            // ── the box goes out on the new receipt; after that, no void
            await EventuallyAsync(() => PreflightAsync(client, box, "OUT", ct), v => v.Decision == "ALLOWED", "the new coupon", ct);
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{Gate}/transactions", GateMove(box, "OUT"), ct)).StatusCode);
            await EventuallyAsync(() => QuoteAsync(client, orderNo, ct),
                q => q.Boxes.Single().Settled.Any(c => c.Status == "EARNED"), "the charge to be earned", ct);
            var late = await client.PostAsJsonAsync($"{Window}/receipts/{second.ReceiptId}/void", new { reason = "changed my mind" }, ct);
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            Assert.Contains("credit note", await late.Content.ReadAsStringAsync(ct), StringComparison.OrdinalIgnoreCase);

            // ── the booking statement: one box, the cancelled line and the earned one, both receipts
            var statement = (await client.GetFromJsonAsync<BookingStatementResponse>($"{Statement}?orderNo={Uri.EscapeDataString(orderNo)}", ct))!;
            Assert.Equal((orderNo, "CUS-TAE"), (statement.OrderNo, statement.CustomerCode));
            Assert.False(string.IsNullOrWhiteSpace(statement.CustomerName));
            var row = Assert.Single(statement.Boxes);
            Assert.Equal(box, row.ContainerNo);
            var cancelled = Assert.Single(row.Lines, l => l.Charge.Status == "CANCELLED");
            Assert.Equal(first.ReceiptNo, cancelled.ReceiptNo);
            Assert.Contains(first.ReceiptNo, cancelled.Charge.CancelReason);
            var earned = Assert.Single(row.Lines, l => l.Charge.Status == "EARNED");
            Assert.Equal(second.ReceiptNo, earned.ReceiptNo);
            Assert.Equal(new StatementTotals(160.50m, 0m, 0m, 0m, 160.50m), statement.Totals);
            Assert.Equal([(first.ReceiptNo, "VOIDED"), (second.ReceiptNo, "ISSUED")], statement.Receipts.Select(r => (r.ReceiptNo, r.Status)));
            Assert.Equal((second.ReceiptNo, first.ReceiptNo),
                (statement.Receipts[0].ReplacedByReceiptNo, statement.Receipts[1].ReplacesReceiptNo));

            await AssertFieldAsync(await client.GetAsync(Statement, ct), "orderNo", ct);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Statement}?orderNo=ZZ-NO-SUCH-ORDER", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.GetAsync($"{Statement}?orderNo={Uri.EscapeDataString(orderNo)}", ct)).StatusCode);
            var other = await api.ClientForAsync(TosApiFactory.SssOwner);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Statement}?orderNo={Uri.EscapeDataString(orderNo)}", ct)).StatusCode);

            // ── the drawer closes on the float plus the one receipt that stands
            var closed = await client.PostAsJsonAsync($"{Window}/shifts/{shiftId}/close",
                new { counts = new object[] { new { channel = "CASH", countedAmount = 1160.50m } } }, ct);
            Assert.True(closed.StatusCode == HttpStatusCode.OK, await closed.Content.ReadAsStringAsync(ct));
            Assert.Equal(0m, Assert.Single((await closed.Content.ReadFromJsonAsync<ShiftResponse>(ct))!.Counts).Variance);
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

    private static object GateMove(string box, string direction) => new
    {
        branchId = SctLcb01, containerNo = box, direction,
        tripType = direction == "IN" ? "DROP_OFF_CONT" : "PICK_UP_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 18000m, customsPermitNo = "ZZ-PERMIT-1",
        truck = new { plate = "70-4321", driverName = "Somsak K." },
        grossWeightKg = direction == "IN" ? 21000m : (decimal?)null, weightSource = direction == "IN" ? "WEIGHBRIDGE" : null,
        seals = new object[] { new { sealNo = "ZZ-VOID-01", sealType = "LINE", isIntact = true } },
    };

    private static object Pay(BookingDetailResponse booking, WindowBookingResponse quote, Guid? replaces) => new
    {
        bookingId = booking.Booking.BookingId,
        bookingContainerIds = quote.Boxes.Select(b => b.BookingContainerId).ToArray(),
        payments = new object[] { new { channel = "CASH", amount = quote.Total } },
        expectedTotal = quote.Total,
        replacesReceiptId = replaces,
    };

    private static async Task<ReceiptResponse> PayAsync(HttpClient client, BookingDetailResponse booking, WindowBookingResponse quote, Guid? replaces, CancellationToken ct)
    {
        var paid = await client.PostAsJsonAsync($"{Window}/receipts", Pay(booking, quote, replaces), ct);
        Assert.True(paid.StatusCode == HttpStatusCode.Created, await paid.Content.ReadAsStringAsync(ct));
        return (await paid.Content.ReadFromJsonAsync<ReceiptResponse>(ct))!;
    }

    private static async Task<WindowBookingResponse> QuoteAsync(HttpClient client, string orderNo, CancellationToken ct) =>
        (await client.GetFromJsonAsync<WindowBookingResponse>($"{Window}/bookings?orderNo={Uri.EscapeDataString(orderNo)}", ct))!;

    private static async Task<GatePreflightResponse> PreflightAsync(HttpClient client, string box, string direction, CancellationToken ct) =>
        (await client.GetFromJsonAsync<GatePreflightResponse>($"{Gate}/preflight?branchId={SctLcb01}&containerNo={box}&direction={direction}", ct))!;

    private static async Task AssertFieldAsync(HttpResponseMessage response, string field, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {text}");
        using var body = JsonDocument.Parse(text);
        Assert.Contains(body.RootElement.GetProperty("errors").EnumerateObject(),
            p => string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase));
    }

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
