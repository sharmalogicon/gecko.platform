using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/apl-container-storage-activity.{pdf|xlsx} (Vector APLContainerStorageActivity, never
/// launched by the desktop), owner 2026-10-10 defaults: the Standard report's lines for EXPORT boxes by vessel ETA, STATUS
/// as the shipment type (EXP CY/CY → CY), the count by status, LIFT OFF EMPTY as the cash lift-off (SL001-CA).
///
/// Uses the SCT fixture's EXP CY/CY 40RH boxes of the 2026-10-23 call; the test hangs its own charges (ZZA-ORDER), removed
/// in finally, with the receipt (ZZA-) that took the cash lift-off.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class AplStorageActivityApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private sealed record Box(Guid BookingId, Guid BookingContainerId, string ContainerNo);

    private static async Task<Box> BoxAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT TOP 1 b.booking_id, x.booking_container_id, x.container_no
            FROM gecko_tos.booking.booking b
            JOIN gecko_tos.booking.booking_container x ON x.booking_id = b.booking_id
            JOIN gecko_tos.vessel.vessel_call c ON c.vessel_call_id = b.vessel_call_id
            WHERE b.tenant_id = @tenant AND b.branch_id = @branch AND b.booking_type_code = 'EXPORT' AND b.order_type_code = 'EXP CY/CY'
              AND b.status <> 'CANCELLED' AND b.deleted_at IS NULL AND x.deleted_at IS NULL AND x.container_no <> ''
              AND CAST(SWITCHOFFSET(c.eta, '+07:00') AS DATE) = '2026-10-23'
            ORDER BY x.container_no;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@branch", SctLcb01);
        await using var r = await command.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct), "No SCT fixture EXP CY/CY box on the 2026-10-23 call.");
        return new Box(r.GetGuid(0), r.GetGuid(1), r.GetString(2));
    }

    private static Charge On(Box box, string code, string movement, string billTo, string term, decimal days, decimal amount, Guid? receiptId = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new Charge
        {
            ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = "WINDOW", OrderNo = "ZZA-ORDER",
            BookingId = box.BookingId, BookingContainerId = box.BookingContainerId, ContainerNo = box.ContainerNo, MovementCode = movement,
            ChargeCode = code, ChargeName = code, BillTo = billTo, PaymentTermCode = term, PayerPartyCode = "CMDU",
            Quantity = days, ChargeableQuantity = days, UnitRate = amount / days, Amount = amount, CurrencyCode = "THB",
            TaxRate = 0, TaxAmount = 0, Status = term == "CASH" ? "PAID" : "UNBILLED", ReceiptId = receiptId, CreatedAt = now, UpdatedAt = now,
        };
    }

    private static async Task RemoveAsync(Guid shift)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZA-ORDER';
            DELETE FROM cashier.receipt WHERE shift_id = @shift;
            DELETE FROM cashier.shift WHERE shift_id = @shift;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@shift", shift);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Export_boxes_print_their_shipment_status_and_the_cash_lift_off()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var box = await BoxAsync(ct);
        using var me = JsonDocument.Parse(await client.GetStringAsync("/auth/me", ct));
        var cashier = me.RootElement.GetProperty("userId").GetGuid();
        var shift = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        try
        {
            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                db.Shifts.Add(new Shift
                {
                    ShiftId = shift, TenantId = TestDatabase.Sct, BranchId = SctLcb01, CashierUserId = cashier, CurrencyCode = "THB",
                    OpenedAt = at.AddHours(-1), OpeningFloat = 0m, Status = "CLOSED", ClosedAt = at, ClosedBy = cashier, CreatedAt = at, UpdatedAt = at,
                });
                var receipt = new Receipt
                {
                    ReceiptId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, ReceiptNo = $"ZZA-{Guid.NewGuid():N}"[..14],
                    ReceiptAt = at, ShiftId = shift, CashierUserId = cashier, PayerName = "Shipper", CurrencyCode = "THB",
                    SubtotalAmount = 150m, TaxAmount = 0m, TotalAmount = 150m, Status = "ISSUED", IssuedFrom = "WINDOW", CreatedAt = at, UpdatedAt = at,
                };
                db.Receipts.Add(receipt);
                db.Charges.AddRange(
                    On(box, "SC006-CR", "MTY_OUT", "LINE", "CREDIT", 2m, 200m),     // empty storage: 2 days
                    On(box, "SL001-CA", "MTY_IN", "CUSTOMER", "CASH", 1m, 150m, receipt.ReceiptId));   // the cash lift-off
                await db.SaveChangesAsync(ct);
            }

            var response = await client.GetAsync(
                $"/api/revenue/reports/accounting/apl-container-storage-activity.xlsx?branchId={SctLcb01}&dateFrom=2026-10-23&dateTo=2026-10-23", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var sheet = book.Worksheet(1);
            var rows = Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
                .Select(r => Enumerable.Range(1, 17).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

            Assert.Contains(rows, r => r[0] == "CONTAINER STORAGE ACTIVITY");
            var line = rows.First(r => r[0] == box.ContainerNo);
            Assert.Equal(["2", "200.00", "150.00"], line[4..7]);
            Assert.Equal("CY", line[16]);
            Assert.Contains(rows, r => r[0] == "CY" && r[1..7].Contains("2"));
            var cash = rows.Last(r => r[0] == box.ContainerNo);
            Assert.Equal("150.00", cash[3]);

            var pdf = await client.GetAsync(
                $"/api/revenue/reports/accounting/apl-container-storage-activity.pdf?branchId={SctLcb01}&dateFrom=2026-10-23&dateTo=2026-10-23", ct);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }
        finally { await RemoveAsync(shift); }
    }
}
