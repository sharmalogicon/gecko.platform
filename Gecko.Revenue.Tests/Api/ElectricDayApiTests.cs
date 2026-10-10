using System.Net;
using ClosedXML.Excel;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/electricity-day[-elc|-precool|-pti].{pdf|xlsx} (Vector ElectricDay and its cuts),
/// owner 2026-10-10 defaults: reefer boxes gated out in the window, their PTI (SE003-CR), pre-cool (SE002-CR) and
/// electricity (SE004-CR, or the tenant's mapped codes) charges at their own amounts, TOTAL = the three; per-size matrices.
///
/// Uses the SCT fixture's reefer boxes gated out on 2026-09-22 (any movement code: the gate's direction decides); the test
/// hangs its own charges (ZZR-ORDER) and a mapping row on them, removed in finally.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class ElectricDayApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private const string Mapped = "ZZ-ELEC";

    private sealed record Box(Guid BookingId, Guid BookingContainerId, string ContainerNo, string TypeCode, string OrderType);

    private static async Task<Box> ReeferOutAsync(string fullEmpty, CancellationToken ct)
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT TOP 1 b.booking_id, x.booking_container_id, x.container_no, r.equipment_type_code, b.order_type_code
            FROM gecko_tos.booking.booking b
            JOIN gecko_tos.booking.booking_container x ON x.booking_id = b.booking_id
            JOIN gecko_tos.booking.equipment_requirement r ON r.equipment_requirement_id = x.equipment_requirement_id
            JOIN gecko_tos.gate.gate_transaction g ON g.booking_container_id = x.booking_container_id AND g.direction = 'OUT'
                 AND g.full_empty = @fullEmpty AND g.status = 'COMPLETED' AND g.deleted_at IS NULL
                 AND CAST(SWITCHOFFSET(g.transaction_at, '+07:00') AS DATE) = '2026-09-22'
            WHERE b.tenant_id = @tenant AND b.branch_id = @branch AND b.status <> 'CANCELLED' AND b.deleted_at IS NULL
              AND x.deleted_at IS NULL AND r.equipment_type_code = '20RF'
            ORDER BY x.container_no;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@branch", SctLcb01);
        command.Parameters.AddWithValue("@fullEmpty", fullEmpty);
        await using var r = await command.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct), $"No SCT fixture 20RF box gated out {fullEmpty} on 2026-09-22.");
        return new Box(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4));
    }

    private static async Task RemoveAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM billing.charge WHERE tenant_id = @tenant AND order_no = 'ZZR-ORDER';
            DELETE FROM billing.report_charge_column WHERE tenant_id = @tenant AND charge_code = @code;
            """;
        command.Parameters.AddWithValue("@tenant", TestDatabase.Sct);
        command.Parameters.AddWithValue("@code", Mapped);
        await command.ExecuteNonQueryAsync();
    }

    private static Charge On(Box box, string code, decimal quantity, decimal rate)
    {
        var now = DateTimeOffset.UtcNow;
        return new Charge
        {
            ChargeId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, Source = "WINDOW", OrderNo = "ZZR-ORDER",
            BookingId = box.BookingId, BookingContainerId = box.BookingContainerId, ContainerNo = box.ContainerNo, MovementCode = "FULL_OUT",
            ChargeCode = code, ChargeName = code, BillTo = "LINE", PaymentTermCode = "CREDIT", PayerPartyCode = "MAEU",
            Quantity = quantity, ChargeableQuantity = quantity, UnitRate = rate, Amount = quantity * rate, CurrencyCode = "THB",
            TaxRate = 0, TaxAmount = 0, Status = "UNBILLED", CreatedAt = now, UpdatedAt = now,
        };
    }

    private static List<string[]> Rows(byte[] xlsx, int width)
    {
        using var book = new XLWorkbook(new MemoryStream(xlsx));
        var sheet = book.Worksheet(1);
        return Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
            .Select(r => Enumerable.Range(1, width).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();
    }

    [Fact]
    public async Task A_reefer_gated_out_shows_its_pti_precool_and_electricity_and_the_size_matrices()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var full = await ReeferOutAsync("FULL", ct);
        var empty = await ReeferOutAsync("EMPTY", ct);
        try
        {
            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                db.ReportChargeColumns.Add(new ReportChargeColumn { TenantId = TestDatabase.Sct, ReportKey = "ELECTRICITY", ChargeCode = Mapped, ColumnKey = "ELECTRICITY" });
                db.Charges.AddRange(
                    On(full, "SE003-CR", 1m, 300m),     // PTI
                    On(full, "SE002-CR", 1m, 200m),     // pre-cool
                    On(full, "SE004-CR", 2m, 150m),     // electricity: 300
                    On(empty, Mapped, 1m, 50m));        // the tenant's electricity code
                await db.SaveChangesAsync(ct);
            }

            const string Window = "dateFrom=2026-09-22&dateTo=2026-09-22";
            var response = await client.GetAsync($"/api/revenue/reports/accounting/electricity-day.xlsx?branchId={SctLcb01}&{Window}", ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
            var rows = Rows(await response.Content.ReadAsByteArrayAsync(ct), 16);

            Assert.Contains(rows, r => r[0] == "ELECTRICITY CHARGES FOR REEFER CONTAINER");
            var line = rows.Single(r => r[1] == full.ContainerNo);
            Assert.Equal(["20RF", "", "", "", "300.00"], line[2..3].Concat(line[4..8]).ToArray());
            Assert.Equal(["200.00", "300.00", "800.00", full.OrderType], new[] { line[9], line[13], line[14], line[15] });
            Assert.Equal("50.00", rows.Single(r => r[1] == empty.ContainerNo)[13]);

            // The matrices: the PTI one has a 20RF column at the PTI rate; the ELECTRICITY one sums both boxes.
            var pti = rows.FindIndex(r => r[1] == "PTI");
            Assert.Equal(["SELLING RATE", "300.00"], rows[pti + 2][..2]);
            var electricity = rows.FindIndex(r => r[1] == "ELECTRICITY");
            Assert.Equal(["AMOUNT", "350.00"], rows[electricity + 4][..2]);

            // The PTI cut: the PTI amount; the ELEC cut lists only boxes gated out full.
            var ptiCut = Rows(await client.GetByteArrayAsync($"/api/revenue/reports/accounting/electricity-day-pti.xlsx?branchId={SctLcb01}&{Window}", ct), 6);
            Assert.Equal("300.00", ptiCut.Single(r => r[1] == full.ContainerNo)[4]);
            var elc = Rows(await client.GetByteArrayAsync($"/api/revenue/reports/accounting/electricity-day-elc.xlsx?branchId={SctLcb01}&{Window}", ct), 8);
            Assert.DoesNotContain(elc, r => r[1] == empty.ContainerNo);

            // Electricity STD is EXPORT boxes only: the IMPORT reefer gated out full is not on it.
            var std = Rows(await client.GetByteArrayAsync($"/api/revenue/reports/accounting/electricity-standard.xlsx?branchId={SctLcb01}&{Window}", ct), 13);
            Assert.Contains(std, r => r[0] == "REEFER CONTAINERS MOVEMENT");
            Assert.DoesNotContain(std, r => r[0] == full.ContainerNo);

            var pdf = await client.GetAsync($"/api/revenue/reports/accounting/electricity-day-precool.pdf?branchId={SctLcb01}&{Window}", ct);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
            var bad = await client.GetAsync($"/api/revenue/reports/accounting/electricity-day.pdf?branchId={SctLcb01}&{Window}&bookingType=NOPE", ct);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        finally { await RemoveAsync(); }
    }
}
