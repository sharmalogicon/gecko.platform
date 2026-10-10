using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/hyundai-refund.{pdf|xlsx} (Vector HyundaiRefund.rdl), owner 2026-10-10 defaults: a
/// row per receipt/invoice billing the agent's IMPORT boxes whose vessel ETA is in the window — boxes by size (once each,
/// however many lines), FCL and LCL at the RDL's fixed baht, CFS by m³, TOTAL and REFUND (80 % FCL + LCL, 100 % CFS).
///
/// Uses the SCT fixture's MAEU IMP CY/CY boxes of the 2026-09-19 call; the test writes a receipt (ZZR-) paying charges on a
/// 20' and a 40' (ZZY-ORDER), removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class HyundaiRefundApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly DateTimeOffset At = new(2026, 9, 20, 10, 0, 0, TimeSpan.FromHours(7));

    private sealed record Box(Guid BookingId, Guid BookingContainerId, string ContainerNo);

    private static async Task<Box> BoxAsync(string size, CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT TOP 1 b.booking_id, x.booking_container_id, x.container_no
            FROM gecko_tos.booking.booking b
            JOIN gecko_tos.booking.booking_container x ON x.booking_id = b.booking_id
            JOIN gecko_tos.booking.equipment_requirement r ON r.equipment_requirement_id = x.equipment_requirement_id
            JOIN gecko_tos.vessel.vessel_call c ON c.vessel_call_id = b.vessel_call_id
            WHERE b.tenant_id = @tenant AND b.branch_id = @branch AND b.booking_type_code = 'IMPORT' AND b.order_type_code = 'IMP CY/CY'
              AND b.line_party_code = 'MAEU' AND b.status <> 'CANCELLED' AND b.deleted_at IS NULL AND x.deleted_at IS NULL
              AND x.container_no <> '' AND r.equipment_type_code LIKE @size + '%'
              AND CAST(SWITCHOFFSET(c.eta, '+07:00') AS DATE) = '2026-09-19'
            ORDER BY x.container_no;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@branch", SctLcb01);
        command.Parameters.AddWithValue("@size", size);
        await using var r = await command.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct), $"No SCT fixture MAEU IMP CY/CY {size}' box on the 2026-09-19 call.");
        return new Box(r.GetGuid(0), r.GetGuid(1), r.GetString(2));
    }

    private static async Task RemoveAsync(Guid shift)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZY-ORDER';
            DELETE FROM cashier.receipt WHERE shift_id = @shift;
            DELETE FROM cashier.shift WHERE shift_id = @shift;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@shift", shift);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Import_boxes_billed_are_counted_once_and_refunded_at_the_fixed_rates()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        using var me = JsonDocument.Parse(await client.GetStringAsync("/auth/me", ct));
        var cashier = me.RootElement.GetProperty("userId").GetGuid();
        var twenty = await BoxAsync("20", ct);
        var forty = await BoxAsync("40", ct);
        var shift = Guid.NewGuid();
        var receiptNo = $"ZZR-{Guid.NewGuid():N}"[..14];
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
                    SubtotalAmount = 600m, TaxAmount = 0m, TotalAmount = 600m, Status = "ISSUED", IssuedFrom = "WINDOW", CreatedAt = At, UpdatedAt = At,
                };
                db.Receipts.Add(receipt);
                Charge Paid(Box box, string code, string movement) => new()
                {
                    ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = "WINDOW", OrderNo = "ZZY-ORDER",
                    BookingId = box.BookingId, BookingContainerId = box.BookingContainerId, ContainerNo = box.ContainerNo, MovementCode = movement,
                    ChargeCode = code, ChargeName = code, BillTo = "CUSTOMER", PaymentTermCode = "CASH", Quantity = 1, UnitRate = 200m,
                    Amount = 200m, CurrencyCode = "THB", TaxRate = 0, TaxAmount = 0, Status = "PAID", ReceiptId = receipt.ReceiptId,
                    CreatedAt = At, UpdatedAt = At,
                };
                db.Charges.AddRange(
                    Paid(twenty, "SL004-CA", "FULL_OUT"),
                    Paid(twenty, "SC010-CA", "FULL_OUT"),   // a second line on the same box: still one box
                    Paid(forty, "SL004-CA", "FULL_OUT"));
                await db.SaveChangesAsync(ct);
            }

            var response = await client.GetAsync(
                $"/api/revenue/reports/accounting/hyundai-refund.xlsx?branchId={SctLcb01}&dateFrom=2026-09-19&dateTo=2026-09-19&agentCode=MAEU", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var sheet = book.Worksheet(1);
            var rows = Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
                .Select(r => Enumerable.Range(1, 25).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

            Assert.Contains(rows, r => r[0] == "<<REFUND>>");
            var line = rows.Single(r => r[0] == receiptNo);
            Assert.Equal("19/09/2026", line[2]);
            Assert.Equal(["1", "1", "", "1", "1,550", "1", "2,650", "", ""], line[3..12]);
            Assert.Equal(["0.00", "0.00", "4,200.00", "3,360.00"], line[21..]);
            Assert.Equal(["4,200.00", "3,360.00"], rows.Last()[23..]);

            var pdf = await client.GetAsync(
                $"/api/revenue/reports/accounting/hyundai-refund.pdf?branchId={SctLcb01}&dateFrom=2026-09-19&dateTo=2026-09-19", ct);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }
        finally { await RemoveAsync(shift); }
    }
}
