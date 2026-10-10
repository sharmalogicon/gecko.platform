using System.Globalization;
using System.Net;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/gate-oocl.{pdf|xlsx} (Vector GateOOCL), owner 2026-10-10 defaults: a line per
/// container the agent moved through the gate in the window — its gate dates read by booking type and direction, empty
/// and full storage (days, amount) and the LO/LO charged to the line as "Gate" — then the count by order type and size,
/// 20' at the RDL's flat 240.
///
/// Uses the SCT LCB01 fixture's empty gate-in of 2026-10-05; the test hangs its own charges on that box (ZZO-ORDER),
/// removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class GateOoclApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private sealed record Box(Guid BookingContainerId, string ContainerNo, string TypeCode, string OrderType, string Line, DateOnly EmptyIn);

    private static async Task<Box> FixtureBoxAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT TOP 1 g.booking_container_id, g.container_no, g.equipment_type_code, b.order_type_code, g.line_party_code,
                   CAST(SWITCHOFFSET(g.transaction_at, '+07:00') AS DATE)
            FROM gecko_tos.gate.gate_transaction g
            JOIN gecko_tos.booking.booking b ON b.booking_id = g.booking_id
            WHERE g.tenant_id = @tenant AND g.branch_id = @branch AND g.movement_code = 'MTY_IN' AND g.status = 'COMPLETED'
              AND CAST(SWITCHOFFSET(g.transaction_at, '+07:00') AS DATE) = '2026-10-05';
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@branch", SctLcb01);
        await using var r = await command.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct), "The SCT fixture's empty gate-in of 2026-10-05 is missing.");
        return new Box(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), DateOnly.FromDateTime(r.GetDateTime(5)));
    }

    private static Charge On(Box box, string code, string movement, string billTo, decimal days, decimal amount)
    {
        var now = DateTimeOffset.UtcNow;
        return new Charge
        {
            ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = "WINDOW", OrderNo = "ZZO-ORDER",
            BookingContainerId = box.BookingContainerId, ContainerNo = box.ContainerNo, MovementCode = movement,
            ChargeCode = code, ChargeName = code, BillTo = billTo, PaymentTermCode = "CREDIT", PayerPartyCode = box.Line,
            Quantity = days, ChargeableQuantity = days, UnitRate = amount / days, Amount = amount, CurrencyCode = "THB",
            TaxRate = 0, TaxAmount = 0, Status = "UNBILLED", CreatedAt = now, UpdatedAt = now,
        };
    }

    private static async Task RemoveAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZO-ORDER';
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_container_the_agent_moved_carries_its_dates_storage_and_gate_charge()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var box = await FixtureBoxAsync(ct);
        try
        {
            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                db.Charges.AddRange(
                    On(box, "SC006-CR", "MTY_OUT", "LINE", 5m, 500m),     // empty storage: 5 days
                    On(box, "SL001-CR", "MTY_IN", "LINE", 1m, 450.50m),   // LO/LO on the line: Gate
                    On(box, "SL002-CR", "FULL_OUT", "CUSTOMER", 1m, 99m)); // LO/LO on the customer: not Gate
                await db.SaveChangesAsync(ct);
            }

            var day = box.EmptyIn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var response = await client.GetAsync(
                $"/api/revenue/reports/accounting/gate-oocl.xlsx?branchId={SctLcb01}&dateFrom={day}&dateTo={day}&agentCode={box.Line}", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var sheet = book.Worksheet(1);
            var rows = Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
                .Select(r => Enumerable.Range(1, 17).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

            Assert.Contains(rows, r => r[0] == "OOCL GATE CHARGE LIST");
            var line = rows.Single(r => r[1] == box.ContainerNo);
            Assert.Equal(
                [box.ContainerNo, box.TypeCode[..2], box.TypeCode[2..], "", "", "", box.OrderType,
                 box.EmptyIn.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture), "", "5", "500.00", "", "", "", "", "450.50"],
                line[1..]);

            var size = box.TypeCode[..2];
            var count = rows.Single(r => r[0] == $"{box.OrderType} {size}");
            Assert.Equal(["1", size == "20" ? "240" : "0"], count[1..3]);
        }
        finally { await RemoveAsync(); }
    }
}
