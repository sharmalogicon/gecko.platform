using System.Globalization;
using System.Net;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/hyundai-lift-off-summary.{pdf|xlsx} (Vector HyundaiLiftOffSummary), owner 2026-10-10
/// defaults: an agent's empties in by size/type — boxes in, lift-offs billed to the line (HMM RETURN), the rest (CONE
/// RETURN), rate and total, boxes cleaned and the cleaning charged, the total — then the Total row.
///
/// Uses the SCT LCB01 fixture's empty gate-in of 2026-10-05; the test hangs its own charges on that move (ZZH-ORDER),
/// removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class HyundaiLiftOffSummaryApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private sealed record Move(Guid BookingContainerId, string ContainerNo, string TypeCode, string Line, DateOnly Day);

    private static async Task<Move> FixtureMoveAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT TOP 1 g.booking_container_id, g.container_no, g.equipment_type_code, g.line_party_code,
                   CAST(SWITCHOFFSET(g.transaction_at, '+07:00') AS DATE)
            FROM gecko_tos.gate.gate_transaction g
            WHERE g.tenant_id = @tenant AND g.branch_id = @branch AND g.movement_code = 'MTY_IN' AND g.status = 'COMPLETED'
              AND CAST(SWITCHOFFSET(g.transaction_at, '+07:00') AS DATE) = '2026-10-05';
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@branch", SctLcb01);
        await using var r = await command.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct), "The SCT fixture's empty gate-in of 2026-10-05 is missing.");
        return new Move(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), DateOnly.FromDateTime(r.GetDateTime(4)));
    }

    private static Charge On(Move move, string code, string billTo, decimal amount)
    {
        var now = DateTimeOffset.UtcNow;
        return new Charge
        {
            ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = "WINDOW", OrderNo = "ZZH-ORDER",
            BookingContainerId = move.BookingContainerId, ContainerNo = move.ContainerNo, MovementCode = "MTY_IN",
            ChargeCode = code, ChargeName = code, BillTo = billTo, PaymentTermCode = "CREDIT", PayerPartyCode = move.Line,
            Quantity = 1, UnitRate = amount, Amount = amount, CurrencyCode = "THB", TaxRate = 0, TaxAmount = 0,
            Status = "UNBILLED", CreatedAt = now, UpdatedAt = now,
        };
    }

    private static async Task RemoveAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZH-ORDER';
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task An_agents_empties_in_are_summed_by_size_with_its_lift_off_and_cleaning()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var move = await FixtureMoveAsync(ct);
        try
        {
            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                db.Charges.AddRange(
                    On(move, "SL001-CR", "LINE", 450m),        // lift-off billed to the line: HMM RETURN
                    On(move, "SC001-CR-W", "LINE", 120m));     // washing
                await db.SaveChangesAsync(ct);
            }

            var day = move.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var response = await client.GetAsync(
                $"/api/revenue/reports/accounting/hyundai-lift-off-summary.xlsx?branchId={SctLcb01}&dateFrom={day}&dateTo={day}&agentCode={move.Line}", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var sheet = book.Worksheet(1);
            var rows = Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
                .Select(r => Enumerable.Range(1, 9).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

            Assert.Contains(rows, r => r[0] == "<<SUMMARY LIFT OFF REPORT>>");
            var label = $"{move.TypeCode[..2]} {move.TypeCode[2..]}";
            Assert.Equal([label, "1", "1", "0", "450.00", "450.00", "1", "120.00", "570.00"], rows.Single(r => r[0] == label));
            Assert.Equal(["Total", "1", "1", "0", "", "450.00", "1", "120.00", "570.00"], rows.Single(r => r[0] == "Total"));

            var pdf = await client.GetAsync(
                $"/api/revenue/reports/accounting/hyundai-lift-off-summary.pdf?branchId={SctLcb01}&dateFrom={day}&dateTo={day}", ct);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }
        finally { await RemoveAsync(); }
    }
}
