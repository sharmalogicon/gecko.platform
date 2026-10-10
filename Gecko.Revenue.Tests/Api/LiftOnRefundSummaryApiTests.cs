using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/lift-on-refund-summary.{pdf|xlsx} (Vector LiftOnChargeRefundSummary), owner
/// 2026-10-10 defaults: a row per import lift-on line (SL004-CA) paid in the window, the box in its FCL (IMP CY/CY) or DD
/// (IMP CYD) band by size, 70 % of its amount refunded — CYD 20'/40' too (the RDL's typo left them out) — then the total.
///
/// Uses an SCT fixture IMP CY/CY box and IMP CYD box; the test writes a receipt (2019-04-02, ZZL-) paying lift-on on both
/// (ZZL-ORDER), removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class LiftOnRefundSummaryApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly DateTimeOffset At = new(2019, 4, 2, 10, 0, 0, TimeSpan.FromHours(7));

    private sealed record Box(Guid BookingId, Guid BookingContainerId, string ContainerNo, string Size);

    private static async Task<Box> BoxAsync(string orderType, CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT TOP 1 b.booking_id, x.booking_container_id, x.container_no, LEFT(r.equipment_type_code, 2)
            FROM gecko_tos.booking.booking b
            JOIN gecko_tos.booking.booking_container x ON x.booking_id = b.booking_id
            JOIN gecko_tos.booking.equipment_requirement r ON r.equipment_requirement_id = x.equipment_requirement_id
            WHERE b.tenant_id = @tenant AND b.booking_type_code = 'IMPORT' AND b.order_type_code = @orderType AND b.status <> 'CANCELLED'
              AND b.deleted_at IS NULL AND x.deleted_at IS NULL AND x.container_no <> '' AND r.equipment_type_code LIKE '[24][05]%'
            ORDER BY b.order_no, x.container_no;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@orderType", orderType);
        await using var r = await command.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct), $"No SCT fixture {orderType} box.");
        return new Box(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3));
    }

    private static async Task RemoveAsync(Guid shift)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZL-ORDER';
            DELETE FROM cashier.receipt WHERE shift_id = @shift;
            DELETE FROM cashier.shift WHERE shift_id = @shift;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@shift", shift);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Import_lift_on_is_banded_by_order_type_and_size_and_refunded_at_seventy_percent()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        using var me = JsonDocument.Parse(await client.GetStringAsync("/auth/me", ct));
        var cashier = me.RootElement.GetProperty("userId").GetGuid();
        var cy = await BoxAsync("IMP CY/CY", ct);
        var cyd = await BoxAsync("IMP CYD", ct);
        var shift = Guid.NewGuid();
        var receiptNo = $"ZZL-{Guid.NewGuid():N}"[..14];
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
                    ReceiptAt = At, ShiftId = shift, CashierUserId = cashier, PayerName = "Consignee", CurrencyCode = "THB",
                    SubtotalAmount = 550m, TaxAmount = 0m, TotalAmount = 550m, Status = "ISSUED", IssuedFrom = "WINDOW", CreatedAt = At, UpdatedAt = At,
                };
                db.Receipts.Add(receipt);
                Charge Paid(Box box, string code, decimal amount) => new()
                {
                    ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = "WINDOW", OrderNo = "ZZL-ORDER",
                    BookingId = box.BookingId, BookingContainerId = box.BookingContainerId, ContainerNo = box.ContainerNo, MovementCode = "FULL_OUT",
                    ChargeCode = code, ChargeName = code, BillTo = "CUSTOMER", PaymentTermCode = "CASH", Quantity = 1, UnitRate = amount,
                    Amount = amount, CurrencyCode = "THB", TaxRate = 0, TaxAmount = 0, Status = "PAID", ReceiptId = receipt.ReceiptId,
                    CreatedAt = At, UpdatedAt = At,
                };
                db.Charges.AddRange(
                    Paid(cy, "SL004-CA", 200m),    // FCL
                    Paid(cyd, "SL004-CA", 300m),   // DD: refunded too
                    Paid(cy, "SL011-CA", 50m));    // not this report's code: not printed
                await db.SaveChangesAsync(ct);
            }

            var response = await client.GetAsync(
                $"/api/revenue/reports/accounting/lift-on-refund-summary.xlsx?branchId={SctLcb01}&dateFrom=2019-04-02&dateTo=2019-04-02", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var sheet = book.Worksheet(1);
            var rows = Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
                .Select(r => Enumerable.Range(1, 14).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

            Assert.Contains(rows, r => r[0] == "SUMMARY  OF  LIFT  ON  CHARGE  REFUND  TO  T.S LINE  CO.,LTD.");
            var mine = rows.Where(r => r[5] == receiptNo).ToList();
            Assert.Equal(2, mine.Count);

            string[] Bands(string orderType, string size) =>
            [
                .. new[] { "20", "40", "45" }.Select(s => orderType == "CY" && s == size ? "1" : "-"),
                .. new[] { "20", "40", "45" }.Select(s => orderType == "CYD" && s == size ? "1" : "-"),
            ];
            Assert.Contains(mine, r => r[7..].SequenceEqual([.. Bands("CY", cy.Size), "140.00"]));
            Assert.Contains(mine, r => r[7..].SequenceEqual([.. Bands("CYD", cyd.Size), "210.00"]));
            Assert.Equal("350.00", rows.Last()[13]);

            var pdf = await client.GetAsync(
                $"/api/revenue/reports/accounting/lift-on-refund-summary.pdf?branchId={SctLcb01}&dateFrom=2019-04-02&dateTo=2019-04-02", ct);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }
        finally { await RemoveAsync(shift); }
    }
}
