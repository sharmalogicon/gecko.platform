using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Data;
using Gecko.Revenue.Endpoints.Reports;
using Gecko.Revenue.Infrastructure.Persistence.Entities;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/receipts (+ /list) through the real host.
///
/// A closed drawer and its receipts are written straight into cashier.* as the
/// fixture tenant, on days in January 2020 that nothing else uses (so the totals
/// are exact), numbered ZZR-, and removed in finally. The cashier is the SCT
/// accounts user, so the name comes back through Identity.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class ReceiptReportApiTests(RevenueApiFactory api)
{
    private const string Report = "/api/revenue/reports/receipts";

    /// <summary>EDI_COORDINATOR: no revenue.charge.view.</summary>
    private const string SctEdi = "edi@sct.co.th";

    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly Guid SctBkk01 = Guid.Parse("775AE785-33A5-F111-9B0D-00919E4766D5");

    // Bangkok noon on 10 and 11 January 2020, and a minute before midnight on the 11th.
    private static readonly DateTimeOffset Jan10Noon = new(2020, 1, 10, 12, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset Jan11Noon = new(2020, 1, 11, 12, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset Jan11Late = new(2020, 1, 11, 23, 59, 0, TimeSpan.FromHours(7));

    private static string Range(Guid branch, string from = "2020-01-10", string to = "2020-01-12") =>
        $"?branchId={branch}&from={from}&to={to}";

    private async Task<Guid> UserIdAsync(string email, CancellationToken ct)
    {
        var client = await api.ClientForAsync(email);
        using var me = JsonDocument.Parse(await client.GetStringAsync("/auth/me", ct));
        return me.RootElement.GetProperty("userId").GetGuid();
    }

    private static Receipt NewReceipt(Guid shift, Guid cashier, string no, DateTimeOffset at, decimal subtotal,
        string? payerCode, string payerName, bool voided = false) => new()
    {
        ReceiptId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, ReceiptNo = no, ReceiptAt = at,
        ShiftId = shift, CashierUserId = cashier, OrderNo = "ZZR-ORDER", PayerPartyCode = payerCode, PayerName = payerName,
        CurrencyCode = "THB", SubtotalAmount = subtotal, TaxAmount = Math.Round(subtotal * 0.07m, 2),
        TotalAmount = subtotal + Math.Round(subtotal * 0.07m, 2),
        Status = voided ? "VOIDED" : "ISSUED", VoidedAt = voided ? at.AddMinutes(5) : null,
        VoidedBy = voided ? cashier : null, VoidReason = voided ? "keyed twice" : null,
        CreatedAt = at, UpdatedAt = at,
    };

    private static ReceiptPayment Pay(Receipt r, string channel, decimal amount) => new()
    {
        ReceiptPaymentId = Guid.NewGuid(), TenantId = TestDatabase.Sct, ReceiptId = r.ReceiptId, Channel = channel,
        Amount = amount, ReferenceNo = channel == "CASH" ? null : "ZZ-REF", CreatedAt = r.ReceiptAt,
    };

    /// <summary>One closed drawer at LCB01: three issued receipts over two days (one walk-in), one voided.</summary>
    private async Task<Guid> SeedAsync(Guid cashier, string tag, CancellationToken ct)
    {
        var shift = new Shift
        {
            ShiftId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, CashierUserId = cashier, CurrencyCode = "THB",
            OpenedAt = Jan10Noon.AddHours(-4), OpeningFloat = 1000m, Status = "CLOSED", ClosedAt = Jan11Late.AddMinutes(1), ClosedBy = cashier,
            CreatedAt = Jan10Noon, UpdatedAt = Jan10Noon,
        };
        var a = NewReceipt(shift.ShiftId, cashier, $"ZZR-{tag}-1", Jan10Noon, 1000m, "CUS-TAE", "Customer A");
        var b = NewReceipt(shift.ShiftId, cashier, $"ZZR-{tag}-2", Jan11Noon, 500m, "CUS-TAE", "Customer A");
        var c = NewReceipt(shift.ShiftId, cashier, $"ZZR-{tag}-3", Jan11Late, 200m, null, "Walk-in Somchai");
        var v = NewReceipt(shift.ShiftId, cashier, $"ZZR-{tag}-4", Jan11Noon, 900m, "CUS-TAE", "Customer A", voided: true);

        await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
        db.Shifts.Add(shift);
        db.Receipts.AddRange(a, b, c, v);
        db.ReceiptPayments.AddRange(Pay(a, "CASH", 1070m), Pay(b, "TRANSFER", 535m), Pay(c, "CASH", 214m), Pay(v, "CASH", 963m));
        await db.SaveChangesAsync(ct);
        return shift.ShiftId;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path, CancellationToken ct)
    {
        var response = await client.GetAsync(path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} returned {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web)!;
    }

    [Fact]
    public async Task Receipts_are_totalled_by_day_shift_cashier_customer_and_channel()
    {
        var ct = TestContext.Current.CancellationToken;
        var cashier = await UserIdAsync(RevenueApiFactory.SctAccounts, ct);
        var shift = await SeedAsync(cashier, Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), ct);
        try
        {
            var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
            var report = await GetAsync<ReceiptsReportResponse>(client, Report + Range(SctLcb01), ct);

            // Issued only: 1,070 + 535 + 214. The void is beside it, not in it.
            Assert.Equal(new ReceiptTally(3, 1700m, 119m, 1819m), report.Total);
            Assert.Equal(new ReceiptTally(1, 900m, 63m, 963m), report.Voided);
            Assert.Equal(["THB"], report.Currencies);

            // Depot days, zero-filled; 23:59 Bangkok on the 11th is the 11th, not the 12th (UTC).
            Assert.Equal([new DateOnly(2020, 1, 10), new DateOnly(2020, 1, 11), new DateOnly(2020, 1, 12)], report.Days.Select(d => d.Day));
            Assert.Equal([1, 2, 0], report.Days.Select(d => d.Tally.Receipts));

            var drawer = Assert.Single(report.Shifts);
            Assert.Equal((shift, cashier, "CLOSED", 1819m), (drawer.ShiftId, drawer.CashierUserId, drawer.Status, drawer.Tally.Total));
            var who = Assert.Single(report.Cashiers);
            Assert.False(string.IsNullOrWhiteSpace(who.CashierName));
            Assert.Equal(who.CashierName, drawer.CashierName);

            Assert.Equal(2, report.Customers.Count);
            Assert.Equal(("CUS-TAE", 2, 1605m), (report.Customers[0].PayerCode, report.Customers[0].Tally.Receipts, report.Customers[0].Tally.Total));
            Assert.Equal((null, "Walk-in Somchai"), (report.Customers[1].PayerCode, report.Customers[1].PayerName));

            Assert.Equal(1284m, report.Channels.Single(c => c.Channel == "CASH").Amount);
            Assert.Equal(535m, report.Channels.Single(c => c.Channel == "TRANSFER").Amount);

            // The list: all four in time order; filtered to the voided one; searched by number.
            var list = await GetAsync<PagedResult<ReceiptRowResponse>>(client, $"{Report}/list{Range(SctLcb01)}", ct);
            Assert.Equal(4, list.TotalCount);
            Assert.Equal(["CASH"], list.Items[0].Channels);
            Assert.Equal(who.CashierName, list.Items[0].CashierName);
            var voided = Assert.Single((await GetAsync<PagedResult<ReceiptRowResponse>>(client, $"{Report}/list{Range(SctLcb01)}&status=VOIDED", ct)).Items);
            Assert.Equal("keyed twice", voided.VoidReason);
            Assert.Equal(1, (await GetAsync<PagedResult<ReceiptRowResponse>>(client, $"{Report}/list{Range(SctLcb01)}&search={voided.ReceiptNo}", ct)).TotalCount);
        }
        finally { await TestDatabase.RemoveReceiptTestRowsAsync(shift); }
    }

    [Theory]
    [InlineData("?from=2020-01-10&to=2020-01-12", "branchId")]
    [InlineData("?branchId=C558E785-33A5-F111-9B0D-00919E4766D5&to=2020-01-12", "from")]
    [InlineData("?branchId=C558E785-33A5-F111-9B0D-00919E4766D5&from=2020-01-12&to=2020-01-10", "to")]
    [InlineData("?branchId=C558E785-33A5-F111-9B0D-00919E4766D5&from=2019-01-01&to=2020-06-30", "to")]
    public async Task A_bad_range_is_a_400_on_the_field(string query, string field)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctOwner);
        var response = await client.GetAsync(Report + query, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, text);
        using var body = JsonDocument.Parse(text);
        Assert.Contains(body.RootElement.GetProperty("errors").EnumerateObject(),
            p => string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_report_stays_inside_the_callers_branches_and_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var cashier = await UserIdAsync(RevenueApiFactory.SctAccounts, ct);
        var shift = await SeedAsync(cashier, Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), ct);
        try
        {
            // The LCB01 ops manager reads LCB01, and is refused BKK01.
            var ops = await api.ClientForAsync(RevenueApiFactory.SctOpsLcb);
            Assert.Equal(3, (await GetAsync<ReceiptsReportResponse>(ops, Report + Range(SctLcb01), ct)).Total.Receipts);
            Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync(Report + Range(SctBkk01), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync($"{Report}/list{Range(SctBkk01)}", ct)).StatusCode);

            // Without revenue.charge.view there is no door.
            var edi = await api.ClientForAsync(SctEdi);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.GetAsync(Report + Range(SctLcb01), ct)).StatusCode);

            // Another tenant does not even find the depot.
            var other = await api.ClientForAsync(RevenueApiFactory.SssOwner);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(Report + Range(SctLcb01), ct)).StatusCode);
        }
        finally { await TestDatabase.RemoveReceiptTestRowsAsync(shift); }
    }
}
