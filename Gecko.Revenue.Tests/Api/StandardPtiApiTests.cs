using System.Net;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/pti-standard.{pdf|xlsx} (Vector StandardPTI), owner 2026-10-10 defaults: a line per
/// box gated out empty in the window — storage days and amount, lift-on, PTI (dates blank, amount printed), pre-cool, the
/// total — then the grand total of the printed lines.
///
/// Uses the SCT fixture's HLCU 20RF empty released on 2026-09-22; the test hangs its own charges (ZZT-ORDER), removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class StandardPtiApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private sealed record Move(Guid BookingContainerId, string ContainerNo);

    private static async Task<Move> ReeferOutAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT TOP 1 g.booking_container_id, g.container_no
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
        return new Move(r.GetGuid(0), r.GetString(1));
    }

    private static Charge On(Move move, string code, decimal days, decimal amount)
    {
        var now = DateTimeOffset.UtcNow;
        return new Charge
        {
            ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = "WINDOW", OrderNo = "ZZT-ORDER",
            BookingContainerId = move.BookingContainerId, ContainerNo = move.ContainerNo, MovementCode = "MTY_OUT",
            ChargeCode = code, ChargeName = code, BillTo = "LINE", PaymentTermCode = "CREDIT", PayerPartyCode = "HLCU",
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
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZT-ORDER';
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task An_empty_released_carries_storage_lift_on_pti_and_precool()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var move = await ReeferOutAsync(ct);
        try
        {
            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                db.Charges.AddRange(
                    On(move, "SC006-CR", 3m, 300m),    // empty storage: 3 days
                    On(move, "SL001-CR", 1m, 200m),    // lift-on
                    On(move, "SE003-CR", 1m, 500m),    // PTI: printed though no PTI date exists
                    On(move, "SE002-CR", 1m, 250m));   // pre-cool
                await db.SaveChangesAsync(ct);
            }

            var response = await client.GetAsync(
                $"/api/revenue/reports/accounting/pti-standard.xlsx?branchId={SctLcb01}&dateFrom=2026-09-22&dateTo=2026-09-22&agentCode=HLCU&type=RF", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var sheet = book.Worksheet(1);
            var rows = Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
                .Select(r => Enumerable.Range(1, 18).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

            Assert.Contains(rows, r => r[0] == "Container gate out :September 2026");
            var line = rows.Single(r => r[1] == move.ContainerNo);
            Assert.Equal("20 RF", line[3]);
            Assert.Equal(["3", "300.00", "200.00", "", "", "", "", "500.00"], line[6..14]);
            Assert.Equal(["22/09/26", "250.00", "1,250.00"], line[15..]);
            Assert.DoesNotContain(rows, r => r[3] is "40 HC");                 // type=RF only
            Assert.Equal("1,250.00", rows.Last()[17]);

            var pdf = await client.GetAsync($"/api/revenue/reports/accounting/pti-standard.pdf?branchId={SctLcb01}&dateFrom=2026-09-22&dateTo=2026-09-22", ct);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }
        finally { await RemoveAsync(); }
    }
}
