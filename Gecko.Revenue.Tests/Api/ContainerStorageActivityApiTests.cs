using System.Globalization;
using System.Net;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/container-storage-activity.{pdf|xlsx} (Vector ContainerStorageActivityStandard),
/// owner 2026-10-10: a line per booked box with its gate dates, storage days and amounts and LO/LO — by the RDL's codes
/// and the tenant's mapping (a LOLO code empty or laden by its move) — the grand total, the size × dry/reefer count and
/// LIFT OFF EMPTY (lift-off billed to the line).
///
/// Uses the SCT LCB01 fixture box of the 2026-10-05 empty gate-in; the report day is its booking's vessel ETA, else its
/// first gate-in (as the report reads it). The test hangs its own charges and a mapping row on it, removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class ContainerStorageActivityApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private const string Lift = "ZZ-SLIFT";   // the tenant maps it LOLO: laden here, its move is FULL_OUT

    private sealed record Box(Guid BookingContainerId, string ContainerNo, string TypeCode, string OrderType, DateOnly Day, DateOnly EmptyIn);

    private static async Task<Box> FixtureBoxAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT TOP 1 g.booking_container_id, g.container_no, r.equipment_type_code, b.order_type_code,
                   CAST(SWITCHOFFSET(COALESCE(c.eta, (SELECT MIN(i.transaction_at) FROM gecko_tos.gate.gate_transaction i
                        WHERE i.booking_container_id = g.booking_container_id AND i.direction = 'IN' AND i.status = 'COMPLETED')), '+07:00') AS DATE),
                   CAST(SWITCHOFFSET(g.transaction_at, '+07:00') AS DATE)
            FROM gecko_tos.gate.gate_transaction g
            JOIN gecko_tos.booking.booking_container x ON x.booking_container_id = g.booking_container_id
            JOIN gecko_tos.booking.equipment_requirement r ON r.equipment_requirement_id = x.equipment_requirement_id
            JOIN gecko_tos.booking.booking b ON b.booking_id = g.booking_id
            LEFT JOIN gecko_tos.vessel.vessel_call c ON c.vessel_call_id = b.vessel_call_id
            WHERE g.tenant_id = @tenant AND g.branch_id = @branch AND g.movement_code = 'MTY_IN' AND g.status = 'COMPLETED'
              AND CAST(SWITCHOFFSET(g.transaction_at, '+07:00') AS DATE) = '2026-10-05';
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@branch", SctLcb01);
        await using var r = await command.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct), "The SCT fixture's empty gate-in of 2026-10-05 is missing.");
        return new Box(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3),
            DateOnly.FromDateTime(r.GetDateTime(4)), DateOnly.FromDateTime(r.GetDateTime(5)));
    }

    private static Charge On(Box box, string code, string movement, string billTo, decimal quantity, decimal amount)
    {
        var now = DateTimeOffset.UtcNow;
        return new Charge
        {
            ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = "WINDOW",
            OrderNo = "ZZS-ORDER", BookingContainerId = box.BookingContainerId, ContainerNo = box.ContainerNo, MovementCode = movement,
            ChargeCode = code, ChargeName = code, BillTo = billTo, PaymentTermCode = "CREDIT", PayerPartyCode = "MAEU",
            Quantity = quantity, ChargeableQuantity = quantity, UnitRate = amount / quantity, Amount = amount, CurrencyCode = "THB",
            TaxCode = "VAT7", TaxRate = 7, TaxAmount = Math.Round(amount * 0.07m, 2), Status = "UNBILLED", CreatedAt = now, UpdatedAt = now,
        };
    }

    private static async Task RemoveAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZS-ORDER';
            DELETE FROM billing.report_charge_column WHERE tenant_id = @tenant AND charge_code = @code;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@code", Lift);
        await command.ExecuteNonQueryAsync();
    }

    private static List<string[]> Rows(IXLWorksheet sheet, int width) =>
        Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
            .Select(r => Enumerable.Range(1, width).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

    [Fact]
    public async Task A_booked_box_carries_its_dates_storage_and_lolo_and_is_counted()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var box = await FixtureBoxAsync(ct);
        try
        {
            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                db.ReportChargeColumns.Add(new ReportChargeColumn { TenantId = TestDatabase.Sct, ReportKey = "STORAGE_ACTIVITY", ChargeCode = Lift, ColumnKey = "LOLO" });
                db.Charges.AddRange(
                    On(box, "SC006-CR", "MTY_OUT", "LINE", 5m, 500m),      // the RDL's empty storage: 5 days
                    On(box, "SL001-CR", "MTY_IN", "LINE", 1m, 450.50m),    // the RDL's LOLO empty, billed to the line
                    On(box, Lift, "FULL_OUT", "CUSTOMER", 1m, 300m));      // the tenant's LOLO on a full move: laden
                await db.SaveChangesAsync(ct);
            }

            var day = box.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var response = await client.GetAsync(
                $"/api/revenue/reports/accounting/container-storage-activity.xlsx?branchId={SctLcb01}&dateFrom={day}&dateTo={day}", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var rows = Rows(book.Worksheet(1), 17);
            Assert.Contains(rows, r => r[0] == "CONTAINER STORAGE ACTIVITY");

            var line = rows.Single(r => r[0] == box.ContainerNo && r[16] == box.OrderType);
            Assert.Equal(
            [
                box.ContainerNo, box.TypeCode, box.EmptyIn.ToString("dd/MM/yy", CultureInfo.InvariantCulture), line[3],
                "5", "500.00", "450.50", line[7], line[8], "-", "-", "300.00", "500.00", "750.50", "-", "-", box.OrderType,
            ], line);

            // The count block has a row for the order type and a Total; LIFT OFF EMPTY lists the box at 450.50.
            Assert.Contains(rows, r => r[0] == box.OrderType && r.Skip(1).Take(6).Any(x => x == "1"));
            var agentLine = rows.Last(r => r[0] == box.ContainerNo);
            Assert.Equal([box.ContainerNo, box.TypeCode, box.EmptyIn.ToString("dd/MM/yy", CultureInfo.InvariantCulture), "450.50"], agentLine[..4]);
        }
        finally { await RemoveAsync(); }
    }
}
