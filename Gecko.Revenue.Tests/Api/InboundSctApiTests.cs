using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/inbound-sct[1].{pdf|xlsx} (Vector InboundSCT / InboundSCT1), owner 2026-10-10
/// defaults: lift charges billed in the window on the RDL's order types, a row per receipt (SCT) or receipt × container
/// (SCT1), amounts the charges' own, boxes counted once; SCT1's REUND is 90 %.
///
/// Uses an SCT fixture IMP CY/CY box and EXP CY/CY box; the test writes a receipt (2019-04-01, ZZI-) paying lift charges
/// on both (ZZI-ORDER), removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class InboundSctApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly DateTimeOffset At = new(2019, 4, 1, 10, 0, 0, TimeSpan.FromHours(7));
    private const string Payer = "ZZPAYER";

    private sealed record Box(Guid BookingId, Guid BookingContainerId, string ContainerNo, string TypeCode);

    private static async Task<Box> BoxAsync(string orderType, CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT TOP 1 b.booking_id, x.booking_container_id, x.container_no, r.equipment_type_code
            FROM gecko_tos.booking.booking b
            JOIN gecko_tos.booking.booking_container x ON x.booking_id = b.booking_id
            JOIN gecko_tos.booking.equipment_requirement r ON r.equipment_requirement_id = x.equipment_requirement_id
            WHERE b.tenant_id = @tenant AND b.order_type_code = @orderType AND b.status <> 'CANCELLED' AND b.deleted_at IS NULL
              AND x.deleted_at IS NULL AND x.container_no <> '' AND r.equipment_type_code LIKE '[24]0%'
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
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZI-ORDER';
            DELETE FROM cashier.receipt WHERE shift_id = @shift;
            DELETE FROM cashier.shift WHERE shift_id = @shift;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@shift", shift);
        await command.ExecuteNonQueryAsync();
    }

    private static List<string[]> Rows(byte[] xlsx, int width)
    {
        using var book = new XLWorkbook(new MemoryStream(xlsx));
        var sheet = book.Worksheet(1);
        return Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
            .Select(r => Enumerable.Range(1, width).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();
    }

    [Fact]
    public async Task Lift_charges_on_a_receipt_are_listed_per_receipt_and_per_box_with_the_refund()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        using var me = JsonDocument.Parse(await client.GetStringAsync("/auth/me", ct));
        var cashier = me.RootElement.GetProperty("userId").GetGuid();
        var import = await BoxAsync("IMP CY/CY", ct);
        var export = await BoxAsync("EXP CY/CY", ct);
        var shift = Guid.NewGuid();
        var receiptNo = $"ZZI-{Guid.NewGuid():N}"[..14];
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
                    ReceiptAt = At, ShiftId = shift, CashierUserId = cashier, PayerName = "Payer", CurrencyCode = "THB",
                    SubtotalAmount = 650m, TaxAmount = 0m, TotalAmount = 650m, Status = "ISSUED", IssuedFrom = "WINDOW", CreatedAt = At, UpdatedAt = At,
                };
                db.Receipts.Add(receipt);
                Charge Paid(Box box, string code, decimal quantity, decimal rate) => new()
                {
                    ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = "WINDOW", OrderNo = "ZZI-ORDER",
                    BookingId = box.BookingId, BookingContainerId = box.BookingContainerId, ContainerNo = box.ContainerNo, MovementCode = "FULL_OUT",
                    ChargeCode = code, ChargeName = code, BillTo = "CUSTOMER", PaymentTermCode = "CASH", PayerPartyCode = Payer,
                    Quantity = quantity, UnitRate = rate, Amount = quantity * rate, CurrencyCode = "THB", TaxRate = 0, TaxAmount = 0,
                    Status = "PAID", ReceiptId = receipt.ReceiptId, CreatedAt = At, UpdatedAt = At,
                };
                db.Charges.AddRange(
                    Paid(import, "SL004-CA", 2m, 100m),   // lift-on: 200, not the RDL's unit rate 100
                    Paid(import, "SL011-CA", 1m, 50m),    // lift-on (VAS) on the same box: the box counts once
                    Paid(export, "SL003-CA", 1m, 400m));  // lift-off for cargo
                await db.SaveChangesAsync(ct);
            }

            const string Window = "dateFrom=2019-04-01&dateTo=2019-04-01";
            var response = await client.GetAsync($"/api/revenue/reports/accounting/inbound-sct.xlsx?branchId={SctLcb01}&{Window}&customerCode={Payer}", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
            var sct = Rows(await response.Content.ReadAsByteArrayAsync(ct), 14);
            var line = sct.Single(r => r[5] == receiptNo);
            var size = import.TypeCode[..2];
            Assert.Equal(["01/04/19", "", "100.00", size == "20" ? "1" : "0", size == "40" ? "1" : "0", "250.00", "0", ""],
                new[] { line[1], line[6] }.Concat(line[8..]).ToArray());

            var none = Rows(await client.GetByteArrayAsync($"/api/revenue/reports/accounting/inbound-sct.xlsx?branchId={SctLcb01}&{Window}&customerCode=NOBODY", ct), 14);
            Assert.DoesNotContain(none, r => r[5] == receiptNo);

            var sct1 = Rows(await client.GetByteArrayAsync($"/api/revenue/reports/accounting/inbound-sct1.xlsx?branchId={SctLcb01}&{Window}&customerCode={Payer}", ct), 7);
            Assert.Contains(sct1, r => r[0] == "รายงานค่าภาระผ่านท่า");
            Assert.Equal([receiptNo, export.ContainerNo, "400.00", "360.00"], sct1.Single(r => r[2] == receiptNo).Where((_, i) => i is 2 or 3 or 5 or 6).ToArray());
            Assert.DoesNotContain(sct1, r => r[3] == import.ContainerNo);
            Assert.Equal(["400.00", "360.00"], sct1.Last()[5..]);

            var pdf = await client.GetAsync($"/api/revenue/reports/accounting/inbound-sct1.pdf?branchId={SctLcb01}&{Window}", ct);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }
        finally { await RemoveAsync(shift); }
    }
}
