using System.Globalization;
using System.Net;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/monitoring-day.{pdf|xlsx} (Vector MonitoringDay), owner 2026-10-10: a line per
/// monitoring charge (SM001-CR, or a code the tenant maps MONITORING) on a reefer box that came in laden, its plug-on
/// dates (laden gate-in/out), days and amount under 20' or 40', total and order type; the total row's 20'/40' amounts
/// are their own sums. No dates.
///
/// Uses SCT fixture reefer boxes with a laden gate-in and a dry one; the test hangs its own charges (ZZM-ORDER) and a
/// mapping row on them, removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class MonitoringDayApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private const string Mapped = "ZZ-MON";

    private sealed record Box(Guid BookingId, Guid BookingContainerId, string ContainerNo, string TypeCode, string OrderType, string Line, DateOnly LadenIn);

    private static async Task<Box> LadenBoxAsync(string typeLike, int skip, CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT b.booking_id, x.booking_container_id, x.container_no, r.equipment_type_code, b.order_type_code, b.line_party_code,
                   CAST(SWITCHOFFSET(MIN(g.transaction_at), '+07:00') AS DATE)
            FROM gecko_tos.booking.booking b
            JOIN gecko_tos.booking.booking_container x ON x.booking_id = b.booking_id
            JOIN gecko_tos.booking.equipment_requirement r ON r.equipment_requirement_id = x.equipment_requirement_id
            JOIN gecko_tos.gate.gate_transaction g ON g.booking_container_id = x.booking_container_id AND g.direction = 'IN'
                 AND g.full_empty = 'FULL' AND g.status = 'COMPLETED' AND g.deleted_at IS NULL
            WHERE b.tenant_id = @tenant AND b.branch_id = @branch AND b.status <> 'CANCELLED' AND b.deleted_at IS NULL
              AND x.deleted_at IS NULL AND x.container_no <> '' AND r.equipment_type_code LIKE @type
            GROUP BY b.booking_id, x.booking_container_id, x.container_no, r.equipment_type_code, b.order_type_code, b.line_party_code
            ORDER BY x.container_no
            OFFSET @skip ROWS FETCH NEXT 1 ROWS ONLY;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@branch", SctLcb01);
        command.Parameters.AddWithValue("@type", typeLike);
        command.Parameters.AddWithValue("@skip", skip);
        await using var r = await command.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct), $"No SCT fixture {typeLike} box with a laden gate-in.");
        return new Box(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), DateOnly.FromDateTime(r.GetDateTime(6)));
    }

    private static async Task RemoveAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZM-ORDER';
            DELETE FROM billing.report_charge_column WHERE tenant_id = @tenant AND charge_code = @code;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@code", Mapped);
        await command.ExecuteNonQueryAsync();
    }

    private static Charge On(Box box, string code, decimal days, decimal amount)
    {
        var now = DateTimeOffset.UtcNow;
        return new Charge
        {
            ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = "WINDOW", OrderNo = "ZZM-ORDER",
            BookingId = box.BookingId, BookingContainerId = box.BookingContainerId, ContainerNo = box.ContainerNo, MovementCode = "FULL_IN",
            ChargeCode = code, ChargeName = code, BillTo = "LINE", PaymentTermCode = "CREDIT", PayerPartyCode = box.Line,
            Quantity = days, ChargeableQuantity = days, UnitRate = amount / days, Amount = amount, CurrencyCode = "THB",
            TaxRate = 0, TaxAmount = 0, Status = "UNBILLED", CreatedAt = now, UpdatedAt = now,
        };
    }

    [Fact]
    public async Task Reefer_monitoring_charges_are_listed_under_their_size_with_their_own_totals()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var first = await LadenBoxAsync("20R%", 0, ct);
        var second = await LadenBoxAsync("20R%", 1, ct);
        var dry = await LadenBoxAsync("%GP", 0, ct);
        try
        {
            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                db.ReportChargeColumns.Add(new ReportChargeColumn { TenantId = TestDatabase.Sct, ReportKey = "MONITORING", ChargeCode = Mapped, ColumnKey = "MONITORING" });
                db.Charges.AddRange(
                    On(first, "SM001-CR", 3m, 600m),   // the RDL's code
                    On(second, Mapped, 2m, 400m),      // the tenant's
                    On(dry, "SM001-CR", 1m, 99m));     // not a reefer: left out
                await db.SaveChangesAsync(ct);
            }

            var response = await client.GetAsync($"/api/revenue/reports/accounting/monitoring-day.xlsx?branchId={SctLcb01}", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var sheet = book.Worksheet(1);
            var rows = Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
                .Select(r => Enumerable.Range(1, 11).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

            Assert.Contains(rows, r => r[0] == "MONITORING CHARGES FOR REEFER CONTAINER");
            var line = rows.Single(r => r[1] == first.ContainerNo);
            Assert.Equal(
                [first.ContainerNo, $"20'{first.TypeCode[2..]}", first.LadenIn.ToString("dd/MM/yy", CultureInfo.InvariantCulture),
                 line[4], "3", "600.00", "-", "-", "600.00", first.OrderType],
                line[1..]);
            Assert.Contains(rows, r => r[1] == second.ContainerNo && r[6] == "400.00");
            Assert.DoesNotContain(rows, r => r[1] == dry.ContainerNo);
            Assert.Equal(["5", "1,000.00", "-", "-", "1,000.00"], rows.Last()[5..10]);

            var pdf = await client.GetAsync($"/api/revenue/reports/accounting/monitoring-day.pdf?branchId={SctLcb01}&bookingType=export", ct);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }
        finally { await RemoveAsync(); }
    }
}
