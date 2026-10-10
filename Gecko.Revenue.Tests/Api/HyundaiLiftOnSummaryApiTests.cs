using System.Net;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/hyundai-lift-on-summary.{pdf|xlsx} (Vector HyundaiLiftOnSummary), owner 2026-10-10
/// defaults: an agent's empties out by size/type — lift-ons billed to the line with rate and total, empty storage boxes,
/// days and total — then, labelled by size/type, PTI and pre-cool; S.TOTAL, VAT (the charges' own), G.TOTAL.
///
/// Uses the SCT fixture's HLCU 20RF empties released on 2026-09-22 (movement GOE: the charges hang on the gate move);
/// the test writes its own charges (ZZN-ORDER), removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class HyundaiLiftOnSummaryApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private sealed record Move(Guid GateTransactionId, Guid BookingContainerId, string ContainerNo);

    private static async Task<IReadOnlyList<Move>> ReefersOutAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT g.gate_transaction_id, g.booking_container_id, g.container_no
            FROM gecko_tos.gate.gate_transaction g
            WHERE g.tenant_id = @tenant AND g.branch_id = @branch AND g.direction = 'OUT' AND g.full_empty = 'EMPTY'
              AND g.status = 'COMPLETED' AND g.line_party_code = 'HLCU' AND g.equipment_type_code = '20RF'
              AND CAST(SWITCHOFFSET(g.transaction_at, '+07:00') AS DATE) = '2026-09-22'
            ORDER BY g.container_no;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@branch", SctLcb01);
        var moves = new List<Move>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) moves.Add(new Move(r.GetGuid(0), r.GetGuid(1), r.GetString(2)));
        Assert.True(moves.Count >= 2, "The SCT fixture's two HLCU 20RF empties released on 2026-09-22 are missing.");
        return moves;
    }

    private static Charge On(Move move, string code, string billTo, decimal quantity, decimal rate, decimal tax)
    {
        var now = DateTimeOffset.UtcNow;
        return new Charge
        {
            ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = "WINDOW", OrderNo = "ZZN-ORDER",
            BookingContainerId = move.BookingContainerId, GateTransactionId = move.GateTransactionId, ContainerNo = move.ContainerNo,
            MovementCode = "MTY_OUT", ChargeCode = code, ChargeName = code, BillTo = billTo, PaymentTermCode = "CREDIT", PayerPartyCode = "HLCU",
            Quantity = quantity, ChargeableQuantity = quantity, UnitRate = rate, Amount = quantity * rate, CurrencyCode = "THB",
            TaxCode = "VAT7", TaxRate = 7, TaxAmount = tax, Status = "UNBILLED", CreatedAt = now, UpdatedAt = now,
        };
    }

    private static async Task RemoveAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZN-ORDER';
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task An_agents_empties_out_carry_lift_on_storage_pti_and_precool_with_the_charges_vat()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var moves = await ReefersOutAsync(ct);
        var (billed, other) = (moves[0], moves[1]);
        try
        {
            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                db.Charges.AddRange(
                    On(billed, "SL001-CR", "LINE", 1m, 300m, 21m),        // lift-on billed to the line
                    On(billed, "SC006-CR", "LINE", 4m, 100m, 28m),        // empty storage: 4 days
                    On(billed, "SE003-CR", "LINE", 1m, 500m, 35m),        // PTI
                    On(billed, "SE002-CR", "LINE", 1m, 250m, 17.5m),      // pre-cool
                    On(other, "SL001-CR", "CUSTOMER", 1m, 300m, 21m));    // lift-on on the customer: not the line's
                await db.SaveChangesAsync(ct);
            }

            var response = await client.GetAsync(
                $"/api/revenue/reports/accounting/hyundai-lift-on-summary.xlsx?branchId={SctLcb01}&dateFrom=2026-09-22&dateTo=2026-09-22&agentCode=HLCU", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var sheet = book.Worksheet(1);
            var rows = Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
                .Select(r => Enumerable.Range(1, 9).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

            Assert.Contains(rows, r => r[0] == "<<SUMMARY LIFT ON REPORT>>");
            var reeferRows = rows.Where(r => r[0] == "20 RF").ToList();
            Assert.Equal(2, reeferRows.Count);
            Assert.Equal(["20 RF", "1", "300.00", "300.00", "", "1", "4", "400.00", "700.00"], reeferRows[0]);
            Assert.Equal(["20 RF", "1", "500.00", "500.00", "1", "1", "250.00", "250.00", "750.00"], reeferRows[1]);
            Assert.Equal(["Total", "1", "", "300.00", "", "1", "4", "400.00", "700.00"], rows.First(r => r[0] == "Total"));
            Assert.Equal(["S.TOTAL", "1,450.00"], rows.Single(r => r[7] == "S.TOTAL")[7..]);
            Assert.Equal(["VAT", "101.50"], rows.Single(r => r[7] == "VAT")[7..]);
            Assert.Equal(["G.TOTAL", "1,551.50"], rows.Single(r => r[7] == "G.TOTAL")[7..]);

            var pdf = await client.GetAsync(
                $"/api/revenue/reports/accounting/hyundai-lift-on-summary.pdf?branchId={SctLcb01}&dateFrom=2026-09-22&dateTo=2026-09-22", ct);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }
        finally { await RemoveAsync(); }
    }
}
