using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/{sales-tax|withholding-tax}.{pdf|xlsx} through the real host (owner 2026-10-10):
/// the RDL's headers and columns, one line per receipt in receipt-number order, voided receipts as Vector prints them,
/// and the totals right.
///
/// Receipts are written straight into cashier.* as the fixture tenant (SCT LCB01) on a day in 2019 nothing else
/// uses, numbered ZZA-, and removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class AccountingReportApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly TimeSpan Bangkok = TimeSpan.FromHours(7);
    private const string Day = "2019-03-15";

    private static async Task<Guid> UserIdAsync(HttpClient client, CancellationToken ct)
    {
        using var me = JsonDocument.Parse(await client.GetStringAsync("/auth/me", ct));
        return me.RootElement.GetProperty("userId").GetGuid();
    }

    private sealed record Seed(string No, DateTimeOffset At, string Payer, decimal Subtotal, decimal Tax, string Status = "ISSUED", decimal Wht = 0m);

    /// <summary>A closed drawer holding the receipts; a receipt's number is the run's prefix + its own.</summary>
    private static async Task<Guid> SeedAsync(Guid cashier, string prefix, IEnumerable<Seed> seeds, CancellationToken ct)
    {
        var at = new DateTimeOffset(2019, 3, 15, 12, 0, 0, Bangkok);
        var shift = new Shift
        {
            ShiftId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, CashierUserId = cashier, CurrencyCode = "THB",
            OpenedAt = at.AddDays(-1), OpeningFloat = 0m, Status = "CLOSED", ClosedAt = at.AddDays(2), ClosedBy = cashier,
            CreatedAt = at, UpdatedAt = at,
        };
        await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
        db.Shifts.Add(shift);
        foreach (var s in seeds)
        {
            var receipt = new Receipt
            {
                ReceiptId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, ReceiptNo = prefix + s.No,
                ReceiptAt = s.At, ShiftId = shift.ShiftId, CashierUserId = cashier, OrderNo = "ZZA-ORDER",
                PayerPartyCode = null, PayerName = s.Payer, CurrencyCode = "THB",
                SubtotalAmount = s.Subtotal, TaxAmount = s.Tax, TotalAmount = s.Subtotal + s.Tax,
                WithholdingTaxRate = s.Wht > 0 ? 3m : null, WithholdingTaxAmount = s.Wht,
                Status = s.Status, IssuedFrom = "WINDOW", CreatedAt = s.At, UpdatedAt = s.At,
            };
            if (s.Status == "VOIDED") { receipt.VoidedAt = s.At.AddMinutes(5); receipt.VoidedBy = cashier; receipt.VoidReason = "test"; }
            db.Receipts.Add(receipt);
            db.ReceiptLines.Add(new ReceiptLine
            {
                ReceiptLineId = Guid.NewGuid(), TenantId = TestDatabase.Sct, ReceiptId = receipt.ReceiptId, LineNo = 1,
                ChargeId = Guid.NewGuid(), ChargeCode = "ZZ-1", Description = "Charge", ContainerNo = "ZZAU1000001",
                MovementCode = "MTY_OUT", Quantity = 1, UnitRate = s.Subtotal, Amount = s.Subtotal,
                TaxCode = "VAT7", TaxRate = 7m, TaxAmount = s.Tax, CreatedAt = s.At,
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
            """;
        command.Parameters.AddWithValue("@shift", shift);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The grid of the report's sheet: every row from the header row on, as the cells' text.</summary>
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

    private static DateTimeOffset At(int day, int hour, int minute, int second = 0) => new(2019, 3, day, hour, minute, second, Bangkok);

    [Fact]
    public async Task Sales_tax_lists_each_receipt_in_number_order_with_voided_ones_as_deleted_and_totals_the_issued()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var prefix = $"ZZA-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}-";
        var shift = await SeedAsync(await UserIdAsync(client, ct), prefix,
        [
            new("B", At(15, 23, 59, 30), "Beta Co., Ltd.", 1000m, 70m),             // the last second of the day is in
            new("A", At(15, 0, 0, 0), "บริษัท อัลฟา จำกัด", 2500.50m, 175.04m),       // the first second too
            new("C", At(15, 9, 0), "Gamma Ltd.", 300m, 21m, Status: "VOIDED"),
            new("D", At(16, 0, 0, 10), "Next Day Co.", 999m, 69.93m),                // the next day is not
        ], ct);
        try
        {
            var url = $"/api/revenue/reports/accounting/sales-tax.xlsx?branchId={SctLcb01}&dateFrom={Day}&dateTo={Day}";
            var response = await client.GetAsync(url, ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            var grid = Grid(await response.Content.ReadAsByteArrayAsync(ct), "วัน เดือน ปี");
            Assert.Equal(["วัน เดือน ปี", "ใบกำกับภาษี", "ชื่อสินค้า / ผู้รับบริการ", "มูลค่าสินค้า", "จำนวนเงิน", "หมายเหตุ"], grid[0]);
            Assert.Equal(["", "เล่ม/เลขที่", "", "บริการ (บาท)", "ภาษีมูลค่าเพิ่ม(บาท)", ""], grid[1]);
            Assert.Equal(
            [
                ["15/03/19", prefix + "A", "บริษัท อัลฟา จำกัด", "2,500.50", "175.04", ""],
                ["15/03/19", prefix + "B", "Beta Co., Ltd.", "1,000.00", "70.00", ""],
                ["15/03/19", prefix + "C", "Gamma Ltd.", "0.00", "0.00", "DELETED"],
                ["", "", "รวมทั้งสิ้น", "3,500.50", "245.04", ""],
            ], grid.Skip(2).Where(r => r[1].StartsWith(prefix) || r[2] == "รวมทั้งสิ้น").ToList());

            // A booking-type filter keeps only receipts with a line for such a booking: these have none.
            var filtered = await client.GetAsync(url + "&bookingType=import", ct);
            Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
            Assert.DoesNotContain(Grid(await filtered.Content.ReadAsByteArrayAsync(ct), "วัน เดือน ปี"), r => r[1].StartsWith(prefix));

            var pdf = await client.GetAsync(url.Replace(".xlsx", ".pdf"), ct);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync(ct))[..4]));
        }
        finally { await RemoveAsync(shift); }
    }

    [Fact]
    public async Task Withholding_tax_lists_the_issued_receipts_with_tax_withheld_and_their_grand_total()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var prefix = $"ZZA-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}-";
        var shift = await SeedAsync(await UserIdAsync(client, ct), prefix,
        [
            new("B", At(15, 10, 0), "Beta Co., Ltd.", 1000m, 70m, Wht: 30m),
            new("A", At(15, 9, 0), "Alpha Co., Ltd.", 280.37m, 19.63m, Wht: 2.80m),
            new("C", At(15, 11, 0), "No Tax Withheld", 500m, 35m),
            new("D", At(15, 12, 0), "Voided Co.", 2000m, 140m, Status: "VOIDED", Wht: 60m),
        ], ct);
        try
        {
            var url = $"/api/revenue/reports/accounting/withholding-tax.xlsx?branchId={SctLcb01}&dateFrom={Day}&dateTo={Day}";
            var response = await client.GetAsync(url, ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            var grid = Grid(await response.Content.ReadAsByteArrayAsync(ct), "วัน เดือน ปี");
            Assert.Equal(["วัน เดือน ปี", "เลขที่ใบเสร็จ", "บริษัท", "เงินได้ที่จ่าย", "จำนวนเงินหัก ณ ที่จ่าย", "ภาษีถูกหัก ณ ที่จ่าย", "หมายเหตุ"], grid[0]);
            Assert.Equal(
            [
                ["15 Mar 2019", prefix + "A", "Alpha Co., Ltd.", "ค่าบริการ", "280.37", "2.80", ""],
                ["15 Mar 2019", prefix + "B", "Beta Co., Ltd.", "ค่าบริการ", "1,000.00", "30.00", ""],
                ["", "", "", "รวมทั้งสิ้น", "1,280.37", "32.80", ""],
            ], grid.Skip(1).Where(r => r[1].StartsWith(prefix) || r[3] == "รวมทั้งสิ้น").ToList());
        }
        finally { await RemoveAsync(shift); }
    }

    [Fact]
    public async Task The_reports_refuse_a_bad_format_a_missing_day_and_an_unknown_booking_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var root = "/api/revenue/reports/accounting";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{root}/sales-tax.csv?branchId={SctLcb01}&dateFrom={Day}&dateTo={Day}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{root}/withholding-tax.pdf?branchId={SctLcb01}&dateFrom={Day}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{root}/sales-tax.pdf?branchId={SctLcb01}&dateFrom={Day}&dateTo={Day}&bookingType=CFS", ct)).StatusCode);
    }
}
