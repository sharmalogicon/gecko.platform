using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Data;
using Gecko.Revenue.Endpoints.Charges;
using Gecko.Revenue.Infrastructure.Persistence.Entities;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/charges and /charges/unbilled through the real host.
///
/// Nothing in Revenue writes UNBILLED lines yet (credit accrual, PLAN_BILLING 6.3),
/// so the lines are written straight into billing.charge as the fixture tenant,
/// under a ZZC- order number, and removed in finally. The fixture already holds
/// other lines, so the unbilled totals are checked as a difference.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class ChargeApiTests(RevenueApiFactory api)
{
    private const string Register = "/api/revenue/charges";
    private const string Unbilled = "/api/revenue/charges/unbilled";

    /// <summary>EDI_COORDINATOR: no revenue.charge.view.</summary>
    private const string SctEdi = "edi@sct.co.th";

    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly Guid SctBkk01 = Guid.Parse("775AE785-33A5-F111-9B0D-00919E4766D5");

    private static string NewOrder() => $"ZZC-{Guid.NewGuid():N}"[..20].ToUpperInvariant();

    private static Charge Line(string orderNo, Guid branchId, string box, string status, string payer, decimal amount, string source = "GATE")
    {
        var credit = status is "UNBILLED" or "INVOICED";
        var now = DateTimeOffset.UtcNow;
        return new Charge
        {
            ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = branchId, Source = source,
            OrderNo = orderNo, ContainerNo = box, MovementCode = "FULL_OUT",
            GateTransactionId = source == "GATE" ? Guid.NewGuid() : null,
            BookingContainerId = source == "WINDOW" ? Guid.NewGuid() : null,
            EirNo = source == "GATE" ? $"EIR-ZZ-{box[^4..]}" : null,
            ChargeCode = "ZZ-LIFT", ChargeName = "Lift on (test)", BillTo = payer == "MAEU" ? "LINE" : "CUSTOMER",
            PaymentTermCode = credit ? "CREDIT" : "CASH", PayerPartyCode = payer,
            Quantity = 1, UnitRate = amount, Amount = amount, CurrencyCode = "THB",
            TaxCode = "VAT7", TaxRate = 7, TaxAmount = Math.Round(amount * 0.07m, 2),
            Status = status,
            WaivedAt = status == "WAIVED" ? now : null, WaivedBy = status == "WAIVED" ? Guid.NewGuid() : null,
            WaiveReason = status == "WAIVED" ? "test waiver" : null,
            CreatedAt = now, UpdatedAt = now,
        };
    }

    private static async Task SeedAsync(params Charge[] lines)
    {
        await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
        db.Charges.AddRange(lines);
        await db.SaveChangesAsync();
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path, CancellationToken ct)
    {
        var response = await client.GetAsync(path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} returned {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web)!;
    }

    [Fact]
    public async Task The_register_lists_and_filters_priced_lines_with_the_payer_named()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctOwner);
        var order = NewOrder();
        try
        {
            await SeedAsync(
                Line(order, SctLcb01, "ZZTU1000001", "UNBILLED", "CUS-TAE", 1000m),
                Line(order, SctLcb01, "ZZTU1000002", "UNBILLED", "CUS-TAE", 500m),
                Line(order, SctLcb01, "ZZTU1000003", "UNBILLED", "MAEU", 250m),
                Line(order, SctLcb01, "ZZTU1000004", "WAIVED", "CUS-TAE", 80m, source: "WINDOW"));

            var all = await GetAsync<PagedResult<ChargeResponse>>(client, $"{Register}?orderNo={order}", ct);
            Assert.Equal(4, all.TotalCount);
            var first = Assert.Single(all.Items, c => c.ContainerNo == "ZZTU1000001");
            Assert.Equal((1000m, 70m, 1070m, "UNBILLED", "GATE", "CREDIT"), (first.Amount, first.TaxAmount, first.Total, first.Status, first.Source, first.PaymentTermCode));
            Assert.False(string.IsNullOrWhiteSpace(first.PayerName));

            Assert.Equal(3, (await GetAsync<PagedResult<ChargeResponse>>(client, $"{Register}?orderNo={order}&status=UNBILLED", ct)).TotalCount);
            Assert.Equal(4, (await GetAsync<PagedResult<ChargeResponse>>(client, $"{Register}?orderNo={order}&status=unbilled,WAIVED", ct)).TotalCount);
            Assert.Equal(1, (await GetAsync<PagedResult<ChargeResponse>>(client, $"{Register}?orderNo={order}&source=WINDOW", ct)).TotalCount);
            Assert.Equal(1, (await GetAsync<PagedResult<ChargeResponse>>(client, $"{Register}?orderNo={order}&payerCode=MAEU", ct)).TotalCount);
            Assert.Equal(1, (await GetAsync<PagedResult<ChargeResponse>>(client, $"{Register}?orderNo={order}&containerNo=zztu%201000002", ct)).TotalCount);
            Assert.Equal(1, (await GetAsync<PagedResult<ChargeResponse>>(client, $"{Register}?search=ZZTU1000004&orderNo={order}", ct)).TotalCount);

            var waived = Assert.Single((await GetAsync<PagedResult<ChargeResponse>>(client, $"{Register}?orderNo={order}&status=WAIVED", ct)).Items);
            Assert.Equal("test waiver", waived.WaiveReason);
        }
        finally { await TestDatabase.RemoveChargeTestRowsAsync(order); }
    }

    [Fact]
    public async Task Unbilled_lines_are_totalled_per_payer()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var order = NewOrder();
        try
        {
            var before = await GetAsync<UnbilledResponse>(client, $"{Unbilled}?branchId={SctLcb01}", ct);

            await SeedAsync(
                Line(order, SctLcb01, "ZZTU2000001", "UNBILLED", "CUS-TAE", 1000m),
                Line(order, SctLcb01, "ZZTU2000001", "UNBILLED", "CUS-TAE", 300m),
                Line(order, SctLcb01, "ZZTU2000002", "UNBILLED", "CUS-TAE", 200m),
                Line(order, SctLcb01, "ZZTU2000003", "WAIVED", "CUS-TAE", 999m, source: "WINDOW"));

            var after = await GetAsync<UnbilledResponse>(client, $"{Unbilled}?branchId={SctLcb01}", ct);
            UnbilledPayerResponse? Payer(UnbilledResponse r) =>
                r.Payers.SingleOrDefault(p => p.PayerCode == "CUS-TAE" && p.BillTo == "CUSTOMER" && p.CurrencyCode == "THB");
            var was = Payer(before);
            var now = Payer(after)!;

            // Three lines on two boxes; the waived one is not unbilled.
            Assert.Equal((was?.Lines ?? 0) + 3, now.Lines);
            Assert.Equal((was?.Boxes ?? 0) + 2, now.Boxes);
            Assert.Equal((was?.Amount ?? 0) + 1500m, now.Amount);
            Assert.Equal((was?.Tax ?? 0) + 105m, now.Tax);
            Assert.Equal(now.Amount + now.Tax, now.Total);
            Assert.False(string.IsNullOrWhiteSpace(now.PayerName));
            Assert.Equal(before.Total + 1605m, after.Total);
        }
        finally { await TestDatabase.RemoveChargeTestRowsAsync(order); }
    }

    [Theory]
    [InlineData("status=BOGUS", "status")]
    [InlineData("source=EDI", "source")]
    [InlineData("from=2026-09-10T00:00:00Z&to=2026-09-01T00:00:00Z", "to")]
    public async Task A_bad_filter_is_a_400_on_the_field(string query, string field)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctOwner);
        var response = await client.GetAsync($"{Register}?{query}", ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, text);
        using var body = JsonDocument.Parse(text);
        Assert.Contains(body.RootElement.GetProperty("errors").EnumerateObject(),
            p => string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Charges_stay_inside_the_callers_branches_and_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var order = NewOrder();
        try
        {
            await SeedAsync(
                Line(order, SctLcb01, "ZZTU3000001", "UNBILLED", "CUS-TAE", 100m),
                Line(order, SctBkk01, "ZZTU3000002", "UNBILLED", "CUS-TAE", 100m));

            // The LCB01 ops manager reads LCB01's line only, and cannot ask for BKK01.
            var ops = await api.ClientForAsync(RevenueApiFactory.SctOpsLcb);
            var mine = await GetAsync<PagedResult<ChargeResponse>>(ops, $"{Register}?orderNo={order}", ct);
            Assert.Equal("ZZTU3000001", Assert.Single(mine.Items).ContainerNo);
            Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync($"{Register}?branchId={SctBkk01}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync($"{Unbilled}?branchId={SctBkk01}", ct)).StatusCode);

            // The owner reads both.
            var owner = await api.ClientForAsync(RevenueApiFactory.SctOwner);
            Assert.Equal(2, (await GetAsync<PagedResult<ChargeResponse>>(owner, $"{Register}?orderNo={order}", ct)).TotalCount);

            // Without revenue.charge.view there is no door at all.
            var edi = await api.ClientForAsync(SctEdi);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.GetAsync(Register, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.GetAsync(Unbilled, ct)).StatusCode);

            // Another tenant does not see the lines.
            var other = await api.ClientForAsync(RevenueApiFactory.SssOwner);
            Assert.Equal(0, (await GetAsync<PagedResult<ChargeResponse>>(other, $"{Register}?orderNo={order}", ct)).TotalCount);
        }
        finally { await TestDatabase.RemoveChargeTestRowsAsync(order); }
    }
}
