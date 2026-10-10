using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// The three cash receipt listings (Vector CashReceiptByUser / ByLiner / ByCompany) through the real host, owner 2026-10-10:
/// money split into the RDL's columns by charge code — the RDL's own list (SC006-CA → Container Storage), the tenant's
/// mapping (billing.report_charge_column), anything else in Other — each receipt counted once, voided receipts in their
/// own section and out of the issued totals, unpriced lines not counted.
///
/// Receipts are written as the fixture tenant (SCT LCB01) on 2019-03-20, numbered ZZC-, with a tenant mapping row for the
/// test's own code; all removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class CashReceiptListingApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly TimeSpan Bangkok = TimeSpan.FromHours(7);
    private const string Root = "/api/revenue/reports/accounting";
    private const string Lift = "ZZ-LIFT";      // the tenant maps it: Lift On / ค่ายกตู้

    private sealed record L(string Code, decimal Amount, decimal Tax, string? Box = "ZZCU1000001");

    private sealed record Seed(string No, int Hour, string Payer, L[] Lines, string Status = "ISSUED", decimal Wht = 0m);

    private static DateTimeOffset At(int hour) => new(2019, 3, 20, hour, 0, 0, Bangkok);

    private static async Task<Guid> UserIdAsync(HttpClient client, CancellationToken ct)
    {
        using var me = JsonDocument.Parse(await client.GetStringAsync("/auth/me", ct));
        return me.RootElement.GetProperty("userId").GetGuid();
    }

    private static async Task<Guid> SeedAsync(Guid cashier, string prefix, IEnumerable<Seed> seeds, CancellationToken ct)
    {
        var shift = new Shift
        {
            ShiftId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, CashierUserId = cashier, CurrencyCode = "THB",
            OpenedAt = At(0).AddDays(-1), OpeningFloat = 0m, Status = "CLOSED", ClosedAt = At(0).AddDays(2), ClosedBy = cashier,
            CreatedAt = At(0), UpdatedAt = At(0),
        };
        await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
        db.Shifts.Add(shift);
        db.ReportChargeColumns.Add(new ReportChargeColumn { TenantId = TestDatabase.Sct, ReportKey = "CASH_RECEIPT", ChargeCode = Lift, ColumnKey = "LIFT_ON" });
        db.ReportChargeColumns.Add(new ReportChargeColumn { TenantId = TestDatabase.Sct, ReportKey = "CASH_RECEIPT_COMPANY", ChargeCode = Lift, ColumnKey = "LIFT" });
        foreach (var s in seeds)
        {
            var (subtotal, tax) = (s.Lines.Sum(l => l.Amount), s.Lines.Sum(l => l.Tax));
            var receipt = new Receipt
            {
                ReceiptId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, ReceiptNo = prefix + s.No,
                ReceiptAt = At(s.Hour), ShiftId = shift.ShiftId, CashierUserId = cashier, OrderNo = "ZZC-ORDER",
                PayerName = s.Payer, CurrencyCode = "THB", SubtotalAmount = subtotal, TaxAmount = tax, TotalAmount = subtotal + tax,
                WithholdingTaxRate = s.Wht > 0 ? 3m : null, WithholdingTaxAmount = s.Wht,
                Status = s.Status, IssuedFrom = "WINDOW", CreatedAt = At(s.Hour), UpdatedAt = At(s.Hour),
            };
            if (s.Status == "VOIDED") { receipt.VoidedAt = At(s.Hour).AddMinutes(5); receipt.VoidedBy = cashier; receipt.VoidReason = "test"; }
            db.Receipts.Add(receipt);
            short n = 0;
            foreach (var l in s.Lines)
                db.ReceiptLines.Add(new ReceiptLine
                {
                    ReceiptLineId = Guid.NewGuid(), TenantId = TestDatabase.Sct, ReceiptId = receipt.ReceiptId, LineNo = ++n,
                    ChargeId = Guid.NewGuid(), ChargeCode = l.Code, Description = l.Code, ContainerNo = l.Box,
                    MovementCode = "MTY_OUT", Quantity = 1, UnitRate = l.Amount, Amount = l.Amount,
                    TaxCode = "VAT7", TaxRate = 7m, TaxAmount = l.Tax, CreatedAt = At(s.Hour),
                });
        }
        await db.SaveChangesAsync(ct);
        return shift.ShiftId;
    }

    private static async Task RemoveAsync(Guid shift)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE l FROM cashier.receipt_line l JOIN cashier.receipt r ON r.receipt_id = l.receipt_id WHERE r.shift_id = @shift;
            DELETE FROM cashier.receipt WHERE shift_id = @shift;
            DELETE FROM cashier.shift WHERE shift_id = @shift;
            DELETE FROM billing.report_charge_column WHERE tenant_id = @tenant AND charge_code = @code;
            """;
        command.Parameters.AddWithValue("@shift", shift);
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@code", Lift);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The sheet's rows from the header row on, as the cells' text.</summary>
    private static List<string[]> Grid(byte[] xlsx, string firstHeader)
    {
        using var book = new XLWorkbook(new MemoryStream(xlsx));
        var sheet = book.Worksheet(1);
        var last = sheet.LastColumnUsed()!.ColumnNumber();
        var top = sheet.RowsUsed().First(r => r.Cell(1).GetString() == firstHeader).RowNumber();
        return Enumerable.Range(top, sheet.LastRowUsed()!.RowNumber() - top + 1)
            .Select(r => Enumerable.Range(1, last).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray())
            .ToList();
    }

    private static readonly Seed[] Receipts =
    [
        // A: tenant code → Lift On, the RDL's SC006-CA → Container Storage, an unmapped code → Other; the free line counts nowhere.
        new("A", 9, "Alpha Co., Ltd.", [new(Lift, 1000m, 70m), new("SC006-CA", 200m, 14m), new("ZZ-MISC", 50m, 3.5m), new("ZZ-FREE", 0m, 0m)]),
        new("B", 10, "Beta Co., Ltd.", [new(Lift, 300m, 21m, "ZZCU1000002")], Wht: 9m),
        new("C", 11, "Gamma Ltd.", [new("SC006-CA", 100m, 7m)], Status: "VOIDED"),
        new("D", 12, "Nothing Priced", [new("ZZ-FREE", 0m, 0m)]),
    ];

    // Column indexes in the User/Liner grid: 8 Lift On (index 7) … 20 Container Storage (19), 22 Other (21), 23–26 totals.
    private static string[] Money(string[] r) => [r[7], r[19], r[21], r[22], r[23], r[24], r[25]];

    [Fact]
    public async Task By_user_sections_by_cashier_splits_money_by_charge_column_and_totals_each_receipt_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var me = await UserIdAsync(client, ct);
        var prefix = $"ZZC-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}-";
        var shift = await SeedAsync(me, prefix, Receipts, ct);
        try
        {
            var url = $"{Root}/cash-receipt-by-user.xlsx?branchId={SctLcb01}&dateFrom=2019-03-20T00:00&dateTo=2019-03-20&cashierUserId={me}";
            var response = await client.GetAsync(url, ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
            var grid = Grid(await response.Content.ReadAsByteArrayAsync(ct), "No.");

            Assert.Equal(["No.", "Invoice Date", "Invoice No", "Agent Code", "Customer Name", "Container No.", "Size/Type", "Handling"], grid[0][..8]);
            Assert.Equal(["Lift On", "Lift Off", "LOLO", "Handling", "Relocation", "Stuff/unStuff", "Facilities"], grid[1][7..14]);
            Assert.Equal(["Container Storage", "Cargo Storage", "Other", "Total Charge", "VAT", "VAT Include", "W/H Tax"], grid[0][19..26]);

            var a = grid.Single(r => r[2] == prefix + "A");
            Assert.Equal(["1", "20/03/2019", "Alpha Co., Ltd.", "ZZCU1000001"], new[] { a[0], a[1], a[4], a[5] });
            Assert.Equal(["1,000.00", "200.00", "50.00", "1,250.00", "87.50", "1,337.50", "0.00"], Money(a));
            Assert.Equal(["2", "300.00", "0.00", "0.00", "300.00", "21.00", "321.00", "9.00"], [grid.Single(r => r[2] == prefix + "B")[0], .. Money(grid.Single(r => r[2] == prefix + "B"))]);
            Assert.DoesNotContain(grid, r => r[2] == prefix + "D");

            // The cashier's Total, then the issued Grand Total: A + B, each once; the voided C is not in them.
            var issuedTotals = grid.TakeWhile(r => r[0] != "CANCELLED RECEIPTS").Where(r => r[6] is "Total" or "Grand Total").ToList();
            Assert.All(issuedTotals, t => Assert.Equal(["1,300.00", "200.00", "50.00", "1,550.00", "108.50", "1,658.50", "9.00"], Money(t)));

            // CANCELLED RECEIPTS: C on its own, with its own Total and Grand Total.
            var cancelled = grid.SkipWhile(r => r[0] != "CANCELLED RECEIPTS").ToList();
            Assert.Equal(["0.00", "100.00", "0.00", "100.00", "7.00", "107.00", "0.00"], Money(cancelled.Single(r => r[2] == prefix + "C")));
            Assert.Contains(cancelled, r => r[0].StartsWith("USER ID: "));
            Assert.Equal(2, cancelled.Count(r => r[6] is "Total" or "Grand Total" && r[22] == "100.00"));

            // Another cashier's filter leaves none of these.
            var other = await client.GetAsync(url.Replace(me.ToString(), Guid.NewGuid().ToString()), ct);
            Assert.DoesNotContain(Grid(await other.Content.ReadAsByteArrayAsync(ct), "No."), r => r[2].StartsWith(prefix));
        }
        finally { await RemoveAsync(shift); }
    }

    [Fact]
    public async Task By_liner_sections_by_line_with_no_grand_total_and_honours_the_time_window()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var prefix = $"ZZC-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}-";
        var shift = await SeedAsync(await UserIdAsync(client, ct), prefix, Receipts, ct);
        try
        {
            // 09:00 to 10:00 inclusive: A and B, not the voided C at 11:00.
            var response = await client.GetAsync($"{Root}/cash-receipt-by-liner.xlsx?branchId={SctLcb01}&dateFrom=2019-03-20T09:00&dateTo=2019-03-20T10:00", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
            var grid = Grid(await response.Content.ReadAsByteArrayAsync(ct), "No.");

            Assert.Equal(["Container No", "Size/Type", "Handling "], grid[0][5..8]);
            Assert.Equal("20/03/2019 09:00", grid.Single(r => r[2] == prefix + "A")[1]);
            Assert.Contains(grid, r => r[2] == prefix + "B");
            Assert.DoesNotContain(grid, r => r[2] == prefix + "C");
            Assert.Contains(grid, r => r[0].StartsWith("AGENT CODE : ") && r[0].EndsWith(prefix + "A"));
            Assert.DoesNotContain(grid, r => r[6] == "Grand Total");
        }
        finally { await RemoveAsync(shift); }
    }

    [Fact]
    public async Task By_company_lists_each_receipt_once_flags_voided_ones_and_summarises_by_charge_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var prefix = $"ZZC-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}-";
        var shift = await SeedAsync(await UserIdAsync(client, ct), prefix, Receipts, ct);
        try
        {
            var response = await client.GetAsync($"{Root}/cash-receipt-by-company.xlsx?branchId={SctLcb01}&dateFrom=2019-03-20&dateTo=2019-03-20", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
            var grid = Grid(await response.Content.ReadAsByteArrayAsync(ct), "Invoice Status");

            Assert.Equal(["Invoice Status", "ลำดับที่", "ใบกำกับภาษี", "วัน / เดือน / ปี", "ชื่อลูกค้า", "ทะเบียนรถ", "เบอร์ตู้", "Amount", "Vat 7%", "NET"], grid[0]);
            var mine = grid.Where(r => r[2].StartsWith(prefix)).ToList();
            Assert.Equal([prefix + "A", prefix + "B", prefix + "C"], mine.Select(r => r[2]));
            Assert.Equal(["", "20/03/2019", "Alpha Co., Ltd.", "ZZCU1000001", "1,250.00", "87.50", "1,337.50"],
                new[] { mine[0][0], mine[0][3], mine[0][4], mine[0][6], mine[0][7], mine[0][8], mine[0][9] });
            Assert.Equal("C", mine[2][0]);

            // The summary: the issued lines by the five rows — ZZ-LIFT is the tenant's ค่ายกตู้; SC006-CA is in none of them.
            var summary = grid.SkipWhile(r => r[0] != "Summary by Charge Code for all above Invoices").ToList();
            Assert.Equal(["", "รวมเงินก่อน VAT", "ภาษีมูลค่าเพิ่ม 7 %", "รวมเงินสุทธิ"], summary[1][..4]);
            Assert.Equal(["ค่ายกตู้", "1,300.00", "91.00", "1,391.00"], summary.Single(r => r[0] == "ค่ายกตู้")[..4]);
            Assert.Equal(["ค่าผ่านท่า", "0.00", "0.00", "0.00"], summary.Single(r => r[0] == "ค่าผ่านท่า")[..4]);
            Assert.Equal(6, summary.Count(r => r[0] is "ค่าผ่านท่า" or "ค่าภาระผ่านท่า" or "ค่าทำความสะอาด" or "ค่ายกตู้" or "ค่าชั่งน้ำหนัก" or "รวม"));

            var pdf = await client.GetAsync($"{Root}/cash-receipt-by-company.pdf?branchId={SctLcb01}&dateFrom=2019-03-20&dateTo=2019-03-20", ct);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }
        finally { await RemoveAsync(shift); }
    }
}
