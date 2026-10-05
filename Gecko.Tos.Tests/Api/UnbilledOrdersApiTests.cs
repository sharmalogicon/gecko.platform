using System.Net;
using System.Net.Http.Json;
using Gecko.Revenue.Endpoints.Charges;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Vector's Unbilled Orders screen on Gecko's data (owner 2026-10-05): the credit lines the gate priced when
/// a box moved, one row per order with the desktop's filters, the lines of the ticked orders, and the printout.
/// SCT's GATE TEST (FULL_IN → FULL_OUT → MTY_IN) raises LIFTCR (฿300, customer credit) on FULL_OUT.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class UnbilledOrdersApiTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private const string Unbilled = "/api/revenue/charges/unbilled";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewBox()
    {
        var ten = $"ZZUU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
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

    private static async Task MoveAsync(HttpClient client, string box, string direction, CancellationToken ct)
    {
        await EventuallyAsync(async () => (await client.GetFromJsonAsync<GatePreflightResponse>(
                $"{Gate}/preflight?branchId={SctLcb01}&containerNo={box}&direction={direction}", ct))!,
            v => v.Decision != "BLOCKED", $"{box} allowed {direction}", ct);
        var response = await client.PostAsJsonAsync($"{Gate}/transactions", new
        {
            branchId = SctLcb01, containerNo = box, direction,
            tripType = direction == "IN" ? "DROP_OFF_CONT" : "PICK_UP_CONT",
            truck = new { plate = "70-3131" },
            grossWeightKg = 22000m, tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 19800m,
            seals = new object[] { new { sealNo = $"ZZU-{box[^4..]}", sealType = "LINE", isIntact = true } },
        }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"gate {direction} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }

    [Fact]
    public async Task A_moved_boxs_credit_lines_list_per_order_with_the_desktop_filters_and_print()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = $"ZZU-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        var box = NewBox();
        try
        {
            var created = await client.PostAsJsonAsync("/api/tos/bookings", new
            {
                branchId = SctLcb01, orderTypeCode = "GATE TEST", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
                validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
                containers = new object[] { new { containerNo = box } },
            }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var orderNo = (await created.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!.Booking.OrderNo;
            await MoveAsync(client, box, "IN", ct);
            await MoveAsync(client, box, "OUT", ct);   // FULL_OUT: the gate prices LIFTCR on credit

            string Url(string query = "") => $"{Unbilled}/orders?branchId={SctLcb01}&carrierRef={carrierRef}{query}";
            var page = await EventuallyAsync(async () => (await client.GetFromJsonAsync<UnbilledOrdersPage>(Url(), ct))!,
                p => p.Items.Count == 1, "the credit line accrued at the gate", ct);
            var order = page.Items.Single();
            Assert.Equal((orderNo, carrierRef, "GATE TEST", "CUS-TAE"), (order.OrderNo, order.CarrierRef, order.OrderTypeCode, order.CustomerCode));
            Assert.Equal((1, 2, 3), (order.Boxes, order.StepsDone, order.StepsTotal));
            Assert.Contains("CREDIT", order.PaymentTerms);
            Assert.Equal(order.Total, page.Total);

            // The desktop's filters: the box still has MTY_IN to do, so it is not COMPLETED; 2 of 3 moves is half or more.
            Assert.Empty((await client.GetFromJsonAsync<UnbilledOrdersPage>(Url("&progress=COMPLETED"), ct))!.Items);
            Assert.Single((await client.GetFromJsonAsync<UnbilledOrdersPage>(Url("&progress=HALF_COMPLETED"), ct))!.Items);
            Assert.Single((await client.GetFromJsonAsync<UnbilledOrdersPage>(Url("&customerCode=CUS-TAE&movementCode=FULL_OUT"), ct))!.Items);
            Assert.Empty((await client.GetFromJsonAsync<UnbilledOrdersPage>(Url("&paymentTermCode=CASH"), ct))!.Items);
            Assert.Empty((await client.GetFromJsonAsync<UnbilledOrdersPage>(Url("&orderTypeCode=IMP%20CY%2FCY"), ct))!.Items);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Url("&progress=SOMETIMES"), ct)).StatusCode);

            // The bottom grid: the ticked order's lines, with the box and its type.
            var lines = (await client.GetFromJsonAsync<List<UnbilledLineResponse>>($"{Unbilled}/lines?branchId={SctLcb01}&orderNo={orderNo}", ct))!;
            var lift = Assert.Single(lines, l => l.ChargeCode == "LIFTCR");
            Assert.Equal((box, "20GP", "FULL_OUT", "CREDIT"), (lift.ContainerNo, lift.EquipmentTypeCode, lift.MovementCode, lift.PaymentTermCode));
            Assert.Equal(order.Total, lines.Sum(l => l.Total));
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Unbilled}/lines?branchId={SctLcb01}", ct)).StatusCode);

            // The printout.
            var xlsx = await client.GetAsync($"{Unbilled}/orders.xlsx?branchId={SctLcb01}&carrierRef={carrierRef}", ct);
            Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
            Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            await TestDatabase.RemoveCashWindowAsync(carrierRef, null);
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }
}
