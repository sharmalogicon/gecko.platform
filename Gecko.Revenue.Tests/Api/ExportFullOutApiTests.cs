using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/export-full-out.{pdf|xlsx} (Vector ExportFullOut), owner 2026-10-10: a row per
/// receipt/invoice of laden lift-on (SL002-CR on FULL_OUT) on EXPORT boxes, counted by size for CY+CFS, count and amount
/// by size for CY and CFS, TOTAL and the 80 % REFUND — each line under its own size and order type (corrected).
///
/// Uses the SCT fixture's two live EXP CY/CY boxes (both 40') and an export box of another order type; the test writes a receipt (2019-03-29, ZZE-) and paid charges, removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class ExportFullOutApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly DateTimeOffset At = new(2019, 3, 29, 10, 0, 0, TimeSpan.FromHours(7));

    private sealed record Box(Guid BookingId, Guid BookingContainerId, string ContainerNo, string TypeCode);

    private static async Task<Box> ExportBoxAsync(string orderType, string size, int skip, CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT b.booking_id, x.booking_container_id, x.container_no, r.equipment_type_code
            FROM gecko_tos.booking.booking b
            JOIN gecko_tos.booking.booking_container x ON x.booking_id = b.booking_id
            JOIN gecko_tos.booking.equipment_requirement r ON r.equipment_requirement_id = x.equipment_requirement_id
            WHERE b.tenant_id = @tenant AND b.booking_type_code = 'EXPORT' AND (b.order_type_code = @orderType OR (@orderType = '*' AND b.order_type_code NOT IN ('EXP CY/CY', 'EXP CFS')))
              AND b.status <> 'CANCELLED' AND b.deleted_at IS NULL AND x.deleted_at IS NULL AND x.container_no <> ''
              AND r.equipment_type_code LIKE @size + '%'
            ORDER BY b.order_no, x.container_no
            OFFSET @skip ROWS FETCH NEXT 1 ROWS ONLY;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@orderType", orderType);
        command.Parameters.AddWithValue("@size", size);
        command.Parameters.AddWithValue("@skip", skip);
        await using var r = await command.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct), $"No SCT fixture {orderType} {size}' export box with a container number.");
        return new Box(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3));
    }

    private static async Task RemoveAsync(Guid shift)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZE-ORDER';
            DELETE FROM cashier.receipt WHERE shift_id = @shift;
            DELETE FROM cashier.shift WHERE shift_id = @shift;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@shift", shift);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_receipt_of_export_lift_on_is_counted_by_size_and_order_type_with_the_refund()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        using var me = JsonDocument.Parse(await client.GetStringAsync("/auth/me", ct));
        var cashier = me.RootElement.GetProperty("userId").GetGuid();
        var first = await ExportBoxAsync("EXP CY/CY", "40", 0, ct);
        var second = await ExportBoxAsync("EXP CY/CY", "40", 1, ct);
        var other = await ExportBoxAsync("*", "20", 0, ct);
        var shift = Guid.NewGuid();
        var receiptNo = $"ZZE-{Guid.NewGuid():N}"[..14];
        try
        {
            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                db.Shifts.Add(new Shift
                {
                    ShiftId = shift, TenantId = TestDatabase.Sct, BranchId = SctLcb01, CashierUserId = cashier, CurrencyCode = "THB",
                    OpenedAt = At.AddHours(-1), OpeningFloat = 0m, Status = "CLOSED", ClosedAt = At.AddHours(1), ClosedBy = cashier,
                    CreatedAt = At, UpdatedAt = At,
                });
                var receipt = new Receipt
                {
                    ReceiptId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, ReceiptNo = receiptNo,
                    ReceiptAt = At, ShiftId = shift, CashierUserId = cashier, PayerName = "Line", CurrencyCode = "THB",
                    SubtotalAmount = 950m, TaxAmount = 0m, TotalAmount = 950m, Status = "ISSUED", IssuedFrom = "WINDOW", CreatedAt = At, UpdatedAt = At,
                };
                db.Receipts.Add(receipt);
                Charge Paid(Box box, string code, string movement, decimal amount) => new()
                {
                    ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = "WINDOW", OrderNo = "ZZE-ORDER",
                    BookingId = box.BookingId, BookingContainerId = box.BookingContainerId, ContainerNo = box.ContainerNo, MovementCode = movement,
                    ChargeCode = code, ChargeName = code, BillTo = "CUSTOMER", PaymentTermCode = "CASH", Quantity = 1, UnitRate = amount, Amount = amount,
                    CurrencyCode = "THB", TaxRate = 0, TaxAmount = 0, Status = "PAID", ReceiptId = receipt.ReceiptId, CreatedAt = At, UpdatedAt = At,
                };
                db.Charges.AddRange(
                    Paid(first, "SL002-CR", "FULL_OUT", 500m),    // CY 40'
                    Paid(second, "SL002-CR", "FULL_OUT", 400m),   // CY 40' at its own rate: 900, not the RDL's 2 x the first line's
                    Paid(other, "SL002-CR", "FULL_OUT", 300m),    // neither CY/CY nor CFS: in no column
                    Paid(first, "SC010-CR", "FULL_OUT", 50m));    // storage: not a lift-on
                await db.SaveChangesAsync(ct);
            }

            var response = await client.GetAsync(
                $"/api/revenue/reports/accounting/export-full-out.xlsx?branchId={SctLcb01}&dateFrom=2019-03-29&dateTo=2019-03-29", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var sheet = book.Worksheet(1);
            var rows = Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
                .Select(r => Enumerable.Range(1, 20).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

            Assert.Contains(rows, r => r[0] == "Invoice No" && r[18] == "THB" && r[19] == "THB(80%)");
            var line = rows.Single(r => r[0] == receiptNo);
            Assert.Equal("29/03/19", line[2]);

            // CY+CFS 20/40/45 | CY 20 n, amt, 40 n, amt, 45 n, amt | CFS the same | TOTAL | REFUND
            Assert.Equal(
                ["0", "2", "0", "0", "0", "2", "900", "0", "0", "0", "0", "0", "0", "0", "0", "900.00", "720.00"],
                line[3..]);

            var pdf = await client.GetAsync(
                $"/api/revenue/reports/accounting/export-full-out.pdf?branchId={SctLcb01}&dateFrom=2019-03-29&dateTo=2019-03-29", ct);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
            Assert.True((await pdf.Content.ReadAsByteArrayAsync(ct)).Length > 1000);
        }
        finally { await RemoveAsync(shift); }
    }
}
