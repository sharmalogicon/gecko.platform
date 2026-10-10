using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/credit-invoice-listing.{pdf|xlsx} (Vector TMS.Accounting.CreditInvoiceListing),
/// owner 2026-10-10: issued credit invoices in EXS (EXPORT + REPO bookings) and IMS (IMPORT), each with its total
/// (Σ Service + Σ VAT), then the "Total" of both; INTERNAL and voided invoices in neither.
///
/// Invoices are written as the fixture tenant (SCT LCB01) on 2019-03-25, numbered ZZI-, each on a fixture booking of
/// the type wanted, and removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class CreditInvoiceListingApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly DateTimeOffset Day = new(2019, 3, 25, 10, 0, 0, TimeSpan.FromHours(7));

    private static async Task<Guid> BookingOfTypeAsync(string type, CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT TOP 1 booking_id FROM gecko_tos.booking.booking WHERE tenant_id = @tenant AND booking_type_code = @type ORDER BY order_no;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@type", type);
        return (Guid)(await command.ExecuteScalarAsync(ct))!;
    }

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
    public async Task Credit_invoices_are_listed_once_in_exs_and_ims_with_section_and_grand_totals()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        using var me = JsonDocument.Parse(await client.GetStringAsync("/auth/me", ct));
        var user = me.RootElement.GetProperty("userId").GetGuid();
        var prefix = $"ZZI-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}-";

        var seeds = new (string No, string Type, decimal Service, string Status)[]
        {
            ("1", "EXPORT", 1000m, "ISSUED"), ("2", "IMPORT", 500m, "ISSUED"), ("3", "INTERNAL", 200m, "ISSUED"),
            ("4", "REPO", 300m, "ISSUED"), ("5", "EXPORT", 400m, "VOIDED"),
        };
        try
        {
            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                foreach (var s in seeds)
                {
                    var tax = s.Service * 0.07m;
                    var invoice = new Invoice
                    {
                        InvoiceId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, InvoiceNo = prefix + s.No,
                        InvoiceType = "CREDIT", Status = s.Status, PaymentTermCode = "CREDIT", BillTo = "CUSTOMER",
                        PayerPartyCode = "CUS-TAE", PayerName = $"Payer {s.No}", CurrencyCode = "THB",
                        SubtotalAmount = s.Service, TaxAmount = tax, TotalAmount = s.Service + tax,
                        IssuedAt = Day, IssuedBy = user, CreatedAt = Day, UpdatedAt = Day,
                    };
                    db.Invoices.Add(invoice);
                    db.InvoiceLines.Add(new InvoiceLine
                    {
                        InvoiceLineId = Guid.NewGuid(), TenantId = TestDatabase.Sct, InvoiceId = invoice.InvoiceId, LineNo = 1,
                        ChargeId = Guid.NewGuid(), BookingId = await BookingOfTypeAsync(s.Type, ct), ChargeCode = "ZZ-1",
                        Quantity = 1, UnitRate = s.Service, Amount = s.Service, TaxRate = 7m, TaxAmount = tax, CreatedAt = Day,
                    });
                }
                await db.SaveChangesAsync(ct);
            }

            var response = await client.GetAsync(
                $"/api/revenue/reports/accounting/credit-invoice-listing.xlsx?branchId={SctLcb01}&dateFrom=2019-03-25&dateTo=2019-03-25", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var sheet = book.Worksheet(1);
            var top = sheet.RowsUsed().First(r => r.Cell(1).GetString() == "EXS").RowNumber();
            var grid = Enumerable.Range(top, sheet.LastRowUsed()!.RowNumber() - top + 1)
                .Select(r => Enumerable.Range(1, 10).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray())
                .Where(r => r.Any(x => x != ""))
                .ToList();

            string[] Row(string no, string number, string payer, string service, string vat, string total) =>
                [no, number, payer, "2019-03-25", "", "", service, vat, total, ""];
            string[] Sums(string first, string service, string vat, string total) => [first, "", "", "", "", "", service, vat, total, ""];
            Assert.Equal(
            [
                ["EXS", "Invoice No", "Customer Name", "Date", "Reimbursement", "Transport", "Service", "VAT 7 %", "TOTAL", "COM"],
                Row("1", prefix + "1", "Payer 1", "1,000.00", "70.00", "1,070.00"),
                Row("2", prefix + "4", "Payer 4", "300.00", "21.00", "321.00"),
                Sums("", "1,300.00", "91.00", "1,391.00"),
                ["IMS", "Invoice No", "Customer Name", "Date", "Reimbursement", "Transport", "Service", "VAT 7 %", "TOTAL", "COM"],
                Row("1", prefix + "2", "Payer 2", "500.00", "35.00", "535.00"),
                Sums("", "500.00", "35.00", "535.00"),
                Sums("Total", "1,800.00", "126.00", "1,926.00"),
            ], grid);
        }
        finally { await RemoveAsync(prefix); }
    }
}
