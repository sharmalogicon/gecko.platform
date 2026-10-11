using System.Net;
using System.Text.Json;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// Vector's billing forms as PDFs: GET /api/revenue/invoices/{id}/invoice.pdf (TMS.Billing.CreditInvoiceReceipt; format=cash
/// for TMS.Billing.CashInvoiceReceipt) and GET /api/revenue/window/receipts/{id}/cash-invoice.pdf (the cash format for a
/// receipt). The layout itself is covered by InvoiceFormPdfTests; here: the routes, the format switch, scoping.
/// The test writes its own invoice and receipt (ZZF-…), removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class InvoiceFormApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly DateTimeOffset Day = new(2019, 3, 27, 10, 0, 0, TimeSpan.FromHours(7));

    private static async Task RemoveAsync(string prefix, Guid shift)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE l FROM billing.invoice_line l JOIN billing.invoice i ON i.invoice_id = l.invoice_id WHERE i.invoice_no LIKE @prefix + '%';
            DELETE FROM billing.invoice WHERE invoice_no LIKE @prefix + '%';
            DELETE p FROM cashier.receipt_payment p JOIN cashier.receipt r ON r.receipt_id = p.receipt_id WHERE r.shift_id = @shift;
            DELETE l FROM cashier.receipt_line l JOIN cashier.receipt r ON r.receipt_id = l.receipt_id WHERE r.shift_id = @shift;
            DELETE FROM cashier.receipt WHERE shift_id = @shift;
            DELETE FROM cashier.shift WHERE shift_id = @shift;
            """;
        command.Parameters.AddWithValue("@prefix", prefix);
        command.Parameters.AddWithValue("@shift", shift);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task An_invoice_prints_in_both_formats_and_a_receipt_in_the_cash_format()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctOwner);
        using var me = JsonDocument.Parse(await client.GetStringAsync("/auth/me", ct));
        var user = me.RootElement.GetProperty("userId").GetGuid();
        var prefix = $"ZZF-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}-";
        var shift = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        try
        {
            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                db.Invoices.Add(new Invoice
                {
                    InvoiceId = invoiceId, TenantId = TestDatabase.Sct, BranchId = SctLcb01, InvoiceNo = prefix + "1",
                    InvoiceType = "CREDIT", Status = "ISSUED", PaymentTermCode = "CREDIT", BillTo = "CUSTOMER",
                    PayerPartyCode = "CUS-TAE", PayerName = "Payer 1", CurrencyCode = "THB",
                    SubtotalAmount = 1000m, TaxAmount = 70m, TotalAmount = 1070m,
                    IssuedAt = Day, IssuedBy = user, CreatedAt = Day, UpdatedAt = Day,
                });
                db.InvoiceLines.Add(new InvoiceLine
                {
                    InvoiceLineId = Guid.NewGuid(), TenantId = TestDatabase.Sct, InvoiceId = invoiceId, LineNo = 1,
                    ChargeId = Guid.NewGuid(), OrderNo = "ZZ-ORD-1", ChargeCode = "ZZ-A", ChargeName = "Charge A",
                    Quantity = 1m, UnitRate = 1000m, Amount = 1000m, TaxRate = 7m, TaxAmount = 70m, CreatedAt = Day,
                });
                db.Shifts.Add(new Shift
                {
                    ShiftId = shift, TenantId = TestDatabase.Sct, BranchId = SctLcb01, CashierUserId = user, CurrencyCode = "THB",
                    OpenedAt = Day.AddDays(-1), OpeningFloat = 0m, Status = "CLOSED", ClosedAt = Day.AddDays(1), ClosedBy = user,
                    CreatedAt = Day, UpdatedAt = Day,
                });
                db.Receipts.Add(new Receipt
                {
                    ReceiptId = receiptId, TenantId = TestDatabase.Sct, BranchId = SctLcb01, ReceiptNo = prefix + "R",
                    ReceiptAt = Day, ShiftId = shift, CashierUserId = user, OrderNo = "ZZ-ORD-1", PayerName = "Payer 2",
                    CurrencyCode = "THB", SubtotalAmount = 100m, TaxAmount = 7m, TotalAmount = 107m, WithholdingTaxAmount = 0m,
                    Status = "ISSUED", IssuedFrom = "CASH_BILL", CreatedAt = Day, UpdatedAt = Day,
                });
                db.ReceiptLines.Add(new ReceiptLine
                {
                    ReceiptLineId = Guid.NewGuid(), TenantId = TestDatabase.Sct, ReceiptId = receiptId, LineNo = 1,
                    ChargeId = Guid.NewGuid(), ChargeCode = "ZZ-B", Description = "Charge B", Quantity = 1, UnitRate = 100m,
                    Amount = 100m, TaxCode = "VAT7", TaxRate = 7m, TaxAmount = 7m, CreatedAt = Day,
                });
                db.ReceiptPayments.Add(new ReceiptPayment
                {
                    ReceiptPaymentId = Guid.NewGuid(), TenantId = TestDatabase.Sct, ReceiptId = receiptId, Channel = "CASH",
                    Amount = 107m, CreatedAt = Day,
                });
                await db.SaveChangesAsync(ct);
            }

            foreach (var url in new[]
            {
                $"/api/revenue/invoices/{invoiceId}/invoice.pdf",
                $"/api/revenue/invoices/{invoiceId}/invoice.pdf?format=cash",
                $"/api/revenue/window/receipts/{receiptId}/cash-invoice.pdf",
            })
            {
                var response = await client.GetAsync(url, ct);
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {await response.Content.ReadAsStringAsync(ct)}");
                Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
                Assert.Equal("%PDF"u8.ToArray(), (await response.Content.ReadAsByteArrayAsync(ct))[..4]);
            }

            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/revenue/invoices/{invoiceId}/invoice.pdf?format=a4", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/revenue/invoices/{Guid.NewGuid()}/invoice.pdf", ct)).StatusCode);

            // Another tenant does not see SCT's invoice.
            var other = await api.ClientForAsync(RevenueApiFactory.SssOwner);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/revenue/invoices/{invoiceId}/invoice.pdf", ct)).StatusCode);
        }
        finally { await RemoveAsync(prefix, shift); }
    }
}
