using System.Net;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/paper.{pdf|xlsx} (Vector Paper), owner 2026-10-10 defaults: a line per container
/// released empty on an EXPORT booking in the window with a paper or lashing charge — L.NET, PP., L.WOOD at the charges'
/// amounts (rate × quantity) and their sum — then the grand total; the agent is the gate move's line.
///
/// Uses the SCT fixture's EGLV export empties released on 2026-09-22; the test hangs its own charges (ZZP-ORDER) and a
/// mapping row on them, removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class PaperApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private const string Mapped = "ZZ-WOOD";

    private sealed record Box(Guid BookingId, Guid BookingContainerId, string ContainerNo, string TypeCode);

    private static async Task<IReadOnlyList<Box>> ReleasedAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT g.booking_id, g.booking_container_id, g.container_no, g.equipment_type_code
            FROM gecko_tos.gate.gate_transaction g
            JOIN gecko_tos.booking.booking b ON b.booking_id = g.booking_id
            WHERE g.tenant_id = @tenant AND g.branch_id = @branch AND g.direction = 'OUT' AND g.full_empty = 'EMPTY'
              AND g.status = 'COMPLETED' AND g.line_party_code = 'EGLV' AND b.booking_type_code = 'EXPORT' AND b.deleted_at IS NULL
              AND CAST(SWITCHOFFSET(g.transaction_at, '+07:00') AS DATE) = '2026-09-22'
            ORDER BY g.container_no;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@branch", SctLcb01);
        var boxes = new List<Box>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) boxes.Add(new Box(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3)));
        Assert.True(boxes.Count >= 2, "The SCT fixture's two EGLV export empties released on 2026-09-22 are missing.");
        return boxes;
    }

    private static Charge On(Box box, string code, decimal quantity, decimal rate)
    {
        var now = DateTimeOffset.UtcNow;
        return new Charge
        {
            ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = "WINDOW", OrderNo = "ZZP-ORDER",
            BookingId = box.BookingId, BookingContainerId = box.BookingContainerId, ContainerNo = box.ContainerNo, MovementCode = "MTY_OUT",
            ChargeCode = code, ChargeName = code, BillTo = "LINE", PaymentTermCode = "CREDIT", PayerPartyCode = "EGLV",
            Quantity = quantity, ChargeableQuantity = quantity, UnitRate = rate, Amount = quantity * rate, CurrencyCode = "THB",
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
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZP-ORDER';
            DELETE FROM billing.report_charge_column WHERE tenant_id = @tenant AND charge_code = @code;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@code", Mapped);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task An_export_empty_released_with_paper_and_lashing_is_listed_at_the_charged_amounts()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var boxes = await ReleasedAsync(ct);
        var (charged, bare) = (boxes[0], boxes[1]);
        try
        {
            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                db.ReportChargeColumns.Add(new ReportChargeColumn { TenantId = TestDatabase.Sct, ReportKey = "PAPER", ChargeCode = Mapped, ColumnKey = "L_WOOD" });
                db.Charges.AddRange(
                    On(charged, "SP001-CR", 2m, 150m),   // paper: 300, not the RDL's unit price 150
                    On(charged, "SL006-CR", 1m, 100m),   // lashing net
                    On(charged, Mapped, 1m, 50m));       // the tenant's lashing wood
                await db.SaveChangesAsync(ct);
            }

            var response = await client.GetAsync(
                $"/api/revenue/reports/accounting/paper.xlsx?branchId={SctLcb01}&dateFrom=2026-09-22&dateTo=2026-09-22&agentCode=EGLV", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));

            using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
            var sheet = book.Worksheet(1);
            var rows = Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
                .Select(r => Enumerable.Range(1, 9).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

            Assert.Contains(rows, r => r[0] == "รายงานค่าปูกระดาษและค่ารัดเชือกประจำ เดือน September 2026");
            var line = rows.Single(r => r[1] == charged.ContainerNo);
            Assert.Equal(["1", charged.ContainerNo, charged.TypeCode], line[..3]);
            Assert.Equal(["100.00", "300.00", "50.00", "450.00"], line[5..]);
            Assert.DoesNotContain(rows, r => r[1] == bare.ContainerNo);
            Assert.Equal(["100.00", "300.00", "50.00", "450.00"], rows.Last()[5..]);

            var pdf = await client.GetAsync($"/api/revenue/reports/accounting/paper.pdf?branchId={SctLcb01}&dateFrom=2026-09-22&dateTo=2026-09-22", ct);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }
        finally { await RemoveAsync(); }
    }
}
