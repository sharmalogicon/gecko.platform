using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/credit-receipt-detail.{pdf|xlsx} (Vector CreditReceiptDetailList), owner 2026-10-10
/// defaults: a row per issued credit invoice — non-VAT and VAT-able amounts at rate × quantity, VAT, NET, exact (no
/// whole-baht rounding); W/H Tax, Net Received and the payment channels blank (no credit-invoice payments yet); the Grand
/// Total; the Summary by Charge Code. Voided invoices out.
///
/// The test writes its own invoices (ZZC-…, 2019-03-26), removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class CreditReceiptDetailApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly DateTimeOffset Day = new(2019, 3, 26, 10, 0, 0, TimeSpan.FromHours(7));

    private static async Task RemoveAsync(string prefix)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE l FROM billing.invoice_line l JOIN billing.invoice i ON i.invoice_id = l.invoice_id WHERE i.invoice_no LIKE @prefix + '%';
            DELETE FROM billing.invoice WHERE invoice_no LIKE @prefix + '%';
            """;
        command.Parameters.AddWithValue("@prefix", prefix);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Credit_invoices_split_non_vat_and_vat_and_sum_by_charge_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        using var me = JsonDocument.Parse(await client.GetStringAsync("/auth/me", ct));
        var user = me.RootElement.GetProperty("userId").GetGuid();
        var prefix = $"ZZC-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}-";
        try
        {
            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                foreach (var (no, status) in new[] { ("1", "ISSUED"), ("2", "VOIDED") })
                {
                    var invoice = new Invoice
                    {
                        InvoiceId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, InvoiceNo = prefix + no,
                        InvoiceType = "CREDIT", Status = status, PaymentTermCode = "CREDIT", BillTo = "CUSTOMER",
                        PayerPartyCode = "CUS-TAE", PayerName = $"Payer {no}", CurrencyCode = "THB",
                        SubtotalAmount = 1300m, TaxAmount = 70m, TotalAmount = 1370m,
                        IssuedAt = Day, IssuedBy = user, CreatedAt = Day, UpdatedAt = Day,
                    };
                    db.Invoices.Add(invoice);
                    InvoiceLine Line(short n, string code, decimal quantity, decimal rate, decimal tax) => new()
                    {
                        InvoiceLineId = Guid.NewGuid(), TenantId = TestDatabase.Sct, InvoiceId = invoice.InvoiceId, LineNo = n,
                        ChargeId = Guid.NewGuid(), OrderNo = "ZZ-ORD-1", ChargeCode = code, ChargeName = $"Charge {code}",
                        Quantity = quantity, UnitRate = rate, Amount = quantity * rate, TaxRate = tax == 0 ? 0 : 7m, TaxAmount = tax, CreatedAt = Day,
                    };
                    db.InvoiceLines.AddRange(
                        Line(1, "ZZ-A", 2m, 150m, 0m),        // non-VAT: 300, not the RDL's unit rate 150
                        Line(2, "ZZ-B", 1m, 1000m, 70m));     // VAT-able
                }
                await db.SaveChangesAsync(ct);
            }

            var response = await client.GetAsync(
                $"/api/revenue/reports/accounting/credit-receipt-detail.xlsx?branchId={SctLcb01}&dateFrom=2019-03-26&dateTo=2019-03-26", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var sheet = book.Worksheet(1);
            var rows = Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
                .Select(r => Enumerable.Range(1, 15).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

            Assert.Contains(rows, r => r[0] == "CREDIT RECEIPT DETAIL LIST");
            Assert.Equal(
                [prefix + "1", "26/03/2019", "ZZ-ORD-1", "CUS-TAE", "Payer 1", "300.00", "1,000.00", "70.00", "1,370.00", "", "", "", "", "", ""],
                rows.Single(r => r[0] == prefix + "1"));
            Assert.DoesNotContain(rows, r => r[0] == prefix + "2");
            Assert.Equal(["Grand Total :", "300.00", "1,000.00", "70.00", "1,370.00"], rows.Single(r => r[4] == "Grand Total :")[4..9]);

            Assert.Equal(["ZZ-A", "", "Charge ZZ-A", "300.00", "0.00", "0.00", "300.00"], rows.Single(r => r[0] == "ZZ-A")[..7]);
            Assert.Equal(["ZZ-B", "", "Charge ZZ-B", "0.00", "1,000.00", "70.00", "1,070.00"], rows.Single(r => r[0] == "ZZ-B")[..7]);
            Assert.Equal(["Total :", "300.00", "1,000.00", "70.00", "1,370.00"], rows.Single(r => r[2] == "Total :")[2..7]);

            var pdf = await client.GetAsync(
                $"/api/revenue/reports/accounting/credit-receipt-detail.pdf?branchId={SctLcb01}&dateFrom=2019-03-26&dateTo=2019-03-26", ct);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }
        finally { await RemoveAsync(prefix); }
    }
}
