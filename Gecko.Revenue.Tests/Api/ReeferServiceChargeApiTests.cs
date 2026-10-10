using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/reefer-service-charge.{pdf|xlsx} (Vector ReeferServiceCharge), owner 2026-10-10
/// defaults: a line per reefer container on bookings whose vessel ETA is in the window — depot code, I/O bound, laden
/// dates, TFC code, PTI and power cost (the charges summed), the PTI's invoice/receipt no — then the PTI and Power totals.
///
/// Uses the SCT fixture's EXPORT 40RH reefers of the 2026-10-23 call; the test writes a receipt (ZZF-), a paid PTI charge and
/// unbilled power charges (ZZF-ORDER), removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class ReeferServiceChargeApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly DateTimeOffset At = new(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(7));

    private sealed record Box(Guid BookingId, Guid BookingContainerId, string ContainerNo, DateOnly Eta);

    private static async Task<Box> ReeferAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT TOP 1 b.booking_id, x.booking_container_id, x.container_no, CAST(SWITCHOFFSET(c.eta, '+07:00') AS DATE)
            FROM gecko_tos.booking.booking b
            JOIN gecko_tos.booking.booking_container x ON x.booking_id = b.booking_id
            JOIN gecko_tos.booking.equipment_requirement r ON r.equipment_requirement_id = x.equipment_requirement_id
            JOIN gecko_tos.vessel.vessel_call c ON c.vessel_call_id = b.vessel_call_id
            WHERE b.tenant_id = @tenant AND b.branch_id = @branch AND b.booking_type_code = 'EXPORT' AND b.status <> 'CANCELLED'
              AND b.deleted_at IS NULL AND x.deleted_at IS NULL AND x.container_no <> '' AND r.equipment_type_code = '40RH'
            ORDER BY x.container_no;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@branch", SctLcb01);
        await using var r = await command.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct), "No SCT fixture EXPORT 40RH box on a vessel call.");
        return new Box(r.GetGuid(0), r.GetGuid(1), r.GetString(2), DateOnly.FromDateTime(r.GetDateTime(3)));
    }

    private static async Task RemoveAsync(Guid shift)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZF-ORDER';
            DELETE FROM cashier.receipt WHERE shift_id = @shift;
            DELETE FROM cashier.shift WHERE shift_id = @shift;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@shift", shift);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_reefer_on_a_call_in_the_window_shows_its_pti_and_power_with_the_pti_receipt()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        using var me = JsonDocument.Parse(await client.GetStringAsync("/auth/me", ct));
        var cashier = me.RootElement.GetProperty("userId").GetGuid();
        var box = await ReeferAsync(ct);
        var shift = Guid.NewGuid();
        var receiptNo = $"ZZF-{Guid.NewGuid():N}"[..14];
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
                    SubtotalAmount = 300m, TaxAmount = 0m, TotalAmount = 300m, Status = "ISSUED", IssuedFrom = "WINDOW", CreatedAt = At, UpdatedAt = At,
                };
                db.Receipts.Add(receipt);
                Charge On(string code, string movement, decimal amount, Guid? receiptId) => new()
                {
                    ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = "WINDOW", OrderNo = "ZZF-ORDER",
                    BookingId = box.BookingId, BookingContainerId = box.BookingContainerId, ContainerNo = box.ContainerNo, MovementCode = movement,
                    ChargeCode = code, ChargeName = code, BillTo = "LINE", PaymentTermCode = receiptId is null ? "CREDIT" : "CASH",
                    Quantity = 1, UnitRate = amount, Amount = amount, CurrencyCode = "THB", TaxRate = 0, TaxAmount = 0,
                    Status = receiptId is null ? "UNBILLED" : "PAID", ReceiptId = receiptId, CreatedAt = At, UpdatedAt = At,
                };
                db.Charges.AddRange(
                    On("SE003-CR", "FULL_OUT", 300m, receipt.ReceiptId),   // PTI, paid on the receipt: its no. is INV NO
                    On("SE004-CR", "FULL_OUT", 200m, null),                // power: the two lines summed (the RDL took the larger)
                    On("SE004-CR", "FULL_IN", 100m, null));
                await db.SaveChangesAsync(ct);
            }

            var day = box.Eta.ToString("yyyy-MM-dd");
            var response = await client.GetAsync(
                $"/api/revenue/reports/accounting/reefer-service-charge.xlsx?branchId={SctLcb01}&dateFrom={day}&dateTo={day}&bookingType=export", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var sheet = book.Worksheet(1);
            var rows = Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
                .Select(r => Enumerable.Range(1, 15).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

            Assert.Contains(rows, r => r[0] == "REEFER SERVICE CHARGE");
            var line = rows.Single(r => r[1] == box.ContainerNo);
            Assert.NotEqual("", line[0]);                                   // the depot's code
            Assert.Equal(["O", ""], line[2..4]);                            // EXPORT; PTI date blank (no work orders)
            Assert.EndsWith("OCEANCREST", line[7]);
            Assert.Equal(["300.00", "", "300.00", "", "", "", receiptNo], line[8..]);
            Assert.Equal(["300.00", "", "300.00"], rows.Last()[8..11]);

            var pdf = await client.GetAsync(
                $"/api/revenue/reports/accounting/reefer-service-charge.pdf?branchId={SctLcb01}&dateFrom={day}&dateTo={day}", ct);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }
        finally { await RemoveAsync(shift); }
    }
}
