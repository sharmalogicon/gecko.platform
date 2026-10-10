using System.Net;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/lift-off-washing.{pdf|xlsx} (Vector TMS.Accounting.LiftOffWashing), owner 2026-10-10:
/// a line per container moved (MTY_IN by default) with the lift-off, washing and chemical charges on that move — by the
/// RDL's codes and the tenant's mapping — cancelled charges left out, and the grand total.
///
/// Uses the SCT LCB01 fixture's empty gate-in of 2026-10-05; the test hangs its own charges on that move (and a
/// tenant mapping row for its own code) and removes them in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class LiftOffWashingApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private const string Wash = "ZZ-WASH";   // the tenant maps it to Detergent

    private sealed record Move(Guid GateTransactionId, Guid BookingContainerId, string ContainerNo, string? TypeCode);

    private static async Task<Move> FixtureMoveAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT TOP 1 gate_transaction_id, booking_container_id, container_no, equipment_type_code
            FROM gecko_tos.gate.gate_transaction
            WHERE tenant_id = @tenant AND branch_id = @branch AND movement_code = 'MTY_IN' AND status = 'COMPLETED'
              AND CAST(SWITCHOFFSET(transaction_at, '+07:00') AS DATE) = '2026-10-05';
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@branch", SctLcb01);
        await using var r = await command.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct), "The SCT fixture's empty gate-in of 2026-10-05 is missing.");
        return new Move(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));
    }

    private static Charge On(Move move, string code, decimal amount, string status = "UNBILLED")
    {
        var now = DateTimeOffset.UtcNow;
        return new Charge
        {
            ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = "GATE",
            OrderNo = "ZZW-ORDER", ContainerNo = move.ContainerNo, MovementCode = "MTY_IN", GateTransactionId = move.GateTransactionId,
            EirNo = "EIR-ZZW", ChargeCode = code, ChargeName = code, BillTo = "LINE", PaymentTermCode = "CREDIT", PayerPartyCode = "MAEU",
            Quantity = 1, UnitRate = amount, Amount = amount, CurrencyCode = "THB", TaxCode = "VAT7", TaxRate = 7,
            TaxAmount = Math.Round(amount * 0.07m, 2), Status = status,
            CancelledAt = status == "CANCELLED" ? now : null, CancelledBy = status == "CANCELLED" ? Guid.NewGuid() : null,
            CancelReason = status == "CANCELLED" ? "test" : null,
            CreatedAt = now, UpdatedAt = now,
        };
    }

    private static async Task RemoveAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZW-ORDER';
            DELETE FROM billing.report_charge_column WHERE tenant_id = @tenant AND charge_code = @code;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@code", Wash);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Each_container_moved_carries_its_lift_off_and_washing_charges_and_the_grand_total()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var move = await FixtureMoveAsync(ct);
        try
        {
            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                db.ReportChargeColumns.Add(new ReportChargeColumn { TenantId = TestDatabase.Sct, ReportKey = "LIFT_OFF_WASHING", ChargeCode = Wash, ColumnKey = "DETERGENT" });
                db.Charges.AddRange(
                    On(move, "SL001-CR", 450.50m),           // the RDL's lift-off code; no rounding to whole baht
                    On(move, Wash, 300m),                    // the tenant's washing code
                    On(move, "SC001-CR-C", 120m),            // the RDL's chemical code
                    On(move, "SC001-CR-W", 999m, "CANCELLED"));  // the RDL's detergent code, cancelled: not counted
                await db.SaveChangesAsync(ct);
            }

            var response = await client.GetAsync(
                $"/api/revenue/reports/accounting/lift-off-washing.xlsx?branchId={SctLcb01}&dateFrom=2026-10-05&dateTo=2026-10-05", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var sheet = book.Worksheet(1);
            Assert.Equal("Lift off / Cleaning", sheet.Cell(3, 1).GetString());
            var top = sheet.RowsUsed().First(r => r.Cell(1).GetString() == "Item").RowNumber();
            var grid = Enumerable.Range(top, sheet.LastRowUsed()!.RowNumber() - top + 1)
                .Select(r => Enumerable.Range(1, 8).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

            Assert.Equal(["Item", "CONTAINER NO.", "SIZE", "IN-DATE", "LIFT OFF", "Detergent", "CHEMICAL", "TOTAL "], grid[0]);
            Assert.Equal(["1", move.ContainerNo, move.TypeCode ?? "", "05/10/26", "450.50", "300.00", "120.00", "870.50"], grid[1]);
            Assert.Equal(["", "", "", "", "450.50", "300.00", "120.00", "870.50"], grid[^1]);

            // A day with no move prints the "-" of an empty total.
            var none = await client.GetAsync(
                $"/api/revenue/reports/accounting/lift-off-washing.pdf?branchId={SctLcb01}&dateFrom=2019-01-01&dateTo=2019-01-01", ct);
            Assert.Equal("application/pdf", none.Content.Headers.ContentType?.MediaType);
        }
        finally { await RemoveAsync(); }
    }
}
