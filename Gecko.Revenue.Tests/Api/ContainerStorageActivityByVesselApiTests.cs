using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/container-storage-activity-by-vessel.{pdf|xlsx} (Vector ContainerStorageActivityByVslVoy),
/// owner 2026-10-10: the IMPORT boxes billed in the window — here paid on a receipt dated in it — a line each with its
/// storage and LO/LO billed (summed; cleaning is not storage), order type and shipper; the RDL's blank columns blank.
///
/// Uses an SCT fixture IMPORT box; the test writes a receipt (2019-03-28, ZZV-) and paid charges on that box, removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class ContainerStorageActivityByVesselApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly DateTimeOffset At = new(2019, 3, 28, 10, 0, 0, TimeSpan.FromHours(7));

    private sealed record Box(Guid BookingId, Guid BookingContainerId, string ContainerNo, string TypeCode, string OrderType, string? Customer);

    private static async Task<Box> ImportBoxAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT TOP 1 b.booking_id, x.booking_container_id, x.container_no, r.equipment_type_code, b.order_type_code, b.customer_party_code
            FROM gecko_tos.booking.booking b
            JOIN gecko_tos.booking.booking_container x ON x.booking_id = b.booking_id
            JOIN gecko_tos.booking.equipment_requirement r ON r.equipment_requirement_id = x.equipment_requirement_id
            WHERE b.tenant_id = @tenant AND b.booking_type_code = 'IMPORT' AND b.status <> 'CANCELLED' AND x.container_no IS NOT NULL
            ORDER BY b.order_no, x.container_no;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        await using var r = await command.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct), "No SCT fixture IMPORT box with a container number.");
        return new Box(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5));
    }

    private static async Task RemoveAsync(Guid shift)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZV-ORDER';
            DELETE FROM cashier.receipt WHERE shift_id = @shift;
            DELETE FROM cashier.shift WHERE shift_id = @shift;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@shift", shift);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task An_import_box_billed_in_the_window_shows_its_storage_and_lolo_summed()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        using var me = JsonDocument.Parse(await client.GetStringAsync("/auth/me", ct));
        var cashier = me.RootElement.GetProperty("userId").GetGuid();
        var box = await ImportBoxAsync(ct);
        var shift = Guid.NewGuid();
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
                    ReceiptId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, ReceiptNo = $"ZZV-{Guid.NewGuid():N}"[..14],
                    ReceiptAt = At, ShiftId = shift, CashierUserId = cashier, PayerName = "Shipper", CurrencyCode = "THB",
                    SubtotalAmount = 1049m, TaxAmount = 0m, TotalAmount = 1049m, Status = "ISSUED", IssuedFrom = "WINDOW", CreatedAt = At, UpdatedAt = At,
                };
                db.Receipts.Add(receipt);
                Charge Paid(string code, string movement, decimal amount) => new()
                {
                    ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = "WINDOW", OrderNo = "ZZV-ORDER",
                    BookingId = box.BookingId, BookingContainerId = box.BookingContainerId, ContainerNo = box.ContainerNo, MovementCode = movement,
                    ChargeCode = code, ChargeName = code, BillTo = "CUSTOMER", PaymentTermCode = "CASH", Quantity = 1, UnitRate = amount, Amount = amount,
                    CurrencyCode = "THB", TaxRate = 0, TaxAmount = 0, Status = "PAID", ReceiptId = receipt.ReceiptId, CreatedAt = At, UpdatedAt = At,
                };
                db.Charges.AddRange(
                    Paid("SC010-CR", "FULL_OUT", 600m),   // laden storage
                    Paid("SC006-CR", "MTY_OUT", 100m),    // empty storage: storage is both, summed
                    Paid("SL002-CR", "FULL_OUT", 350m),   // LO/LO
                    Paid("SC001-CR", "FULL_OUT", 99m));   // cleaning: the RDL counted it as storage; not any more
                await db.SaveChangesAsync(ct);
            }

            var response = await client.GetAsync(
                $"/api/revenue/reports/accounting/container-storage-activity-by-vessel.xlsx?branchId={SctLcb01}&dateFrom=2019-03-28&dateTo=2019-03-28", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var sheet = book.Worksheet(1);
            var rows = Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
                .Select(r => Enumerable.Range(1, 14).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

            Assert.Equal(["CONT NO.", "SIZE"], rows.Single(r => r[0] == "CONT NO.")[..2]);
            var line = rows.Single(r => r[0] == box.ContainerNo);
            Assert.Equal([box.ContainerNo, box.TypeCode, "", "700.00", "350.00", "", "", "", box.OrderType, box.Customer ?? ""],
                new[] { line[0], line[1], line[6], line[7], line[8], line[9], line[10], line[11], line[12], line[13] });
        }
        finally { await RemoveAsync(shift); }
    }
}
