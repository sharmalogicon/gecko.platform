using System.Net;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/nyk.{pdf|xlsx} (Vector NYKReport), owner 2026-10-10 defaults: a line's charges on
/// its boxes moved in the window, a row per box, move and charge in NYK's upload format — F/E, cost, activity date (the
/// move's; a charge on no move, pre-cool, the box's empty gate-out), tariff item by charge code — cancelled charges out.
///
/// Uses the SCT fixture's HLCU 20RF empty released on 2026-09-22 (agentCode=HLCU in place of NYK); the test writes its own
/// charges (ZZK-ORDER), removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class NykReportApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private sealed record Move(Guid GateTransactionId, Guid BookingContainerId, string ContainerNo);

    private static async Task<Move> ReeferOutAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT TOP 1 g.gate_transaction_id, g.booking_container_id, g.container_no
            FROM gecko_tos.gate.gate_transaction g
            WHERE g.tenant_id = @tenant AND g.branch_id = @branch AND g.direction = 'OUT' AND g.full_empty = 'EMPTY'
              AND g.status = 'COMPLETED' AND g.line_party_code = 'HLCU' AND g.equipment_type_code = '20RF'
              AND CAST(SWITCHOFFSET(g.transaction_at, '+07:00') AS DATE) = '2026-09-22'
            ORDER BY g.container_no;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@branch", SctLcb01);
        await using var r = await command.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct), "The SCT fixture's HLCU 20RF empty released on 2026-09-22 is missing.");
        return new Move(r.GetGuid(0), r.GetGuid(1), r.GetString(2));
    }

    private static Charge On(Move move, string code, string? movement, bool onMove, decimal amount, string status = "UNBILLED")
    {
        var now = DateTimeOffset.UtcNow;
        return new Charge
        {
            ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = movement is null ? "MANUAL" : "WINDOW", OrderNo = "ZZK-ORDER",
            BookingContainerId = move.BookingContainerId, GateTransactionId = onMove ? move.GateTransactionId : null, ContainerNo = move.ContainerNo,
            MovementCode = movement, ChargeCode = code, ChargeName = code, BillTo = "LINE", PaymentTermCode = "CREDIT", PayerPartyCode = "HLCU",
            Quantity = 1, UnitRate = amount, Amount = amount, CurrencyCode = "THB", TaxRate = 0, TaxAmount = 0, Status = status,
            CancelReason = status == "CANCELLED" ? "test" : null, CancelledAt = status == "CANCELLED" ? now : null, CreatedAt = now, UpdatedAt = now,
        };
    }

    private static async Task RemoveAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZK-ORDER';
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_lines_charges_are_listed_by_box_move_and_tariff_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var move = await ReeferOutAsync(ct);
        try
        {
            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                db.Charges.AddRange(
                    On(move, "SL001-CR", "MTY_OUT", true, 300m),                  // lift-on on the move: CYCDLIFTE
                    On(move, "SE002-CR", null, false, 250m),                     // pre-cool on no move: the empty gate-out's date
                    On(move, "SC006-CR", "MTY_OUT", true, 99m, "CANCELLED"),     // cancelled: out
                    On(move, "ZZ-OTHER", "FULL_IN", false, 77m));                // no tariff item: out
                await db.SaveChangesAsync(ct);
            }

            var response = await client.GetAsync(
                $"/api/revenue/reports/accounting/nyk.xlsx?branchId={SctLcb01}&dateFrom=2026-09-22&dateTo=2026-09-22&agentCode=HLCU", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var sheet = book.Worksheet(1);
            var rows = Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
                .Select(r => Enumerable.Range(1, 24).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

            Assert.Contains(rows, r => r[0] == "<<NYK Report>>");
            var mine = rows.Where(r => r[2] == move.ContainerNo).ToList();
            Assert.Equal(2, mine.Count);
            Assert.Contains(mine, r => r[1] == "20RF" && r[3] == "E" && r[4] == "1" && r[5] == "THB" && r[6] == "300.00"
                                       && r[7] == "2026-09-22" && r[13] == "CYCDLIFTE" && r[21] == "BKK");
            Assert.Contains(mine, r => r[6] == "250.00" && r[7] == "2026-09-22" && r[13] == "MRCNPRECL");
            Assert.Equal("550.00", rows.Last()[6]);

            var pdf = await client.GetAsync($"/api/revenue/reports/accounting/nyk.pdf?branchId={SctLcb01}&dateFrom=2026-09-22&dateTo=2026-09-22", ct);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }
        finally { await RemoveAsync(); }
    }
}
