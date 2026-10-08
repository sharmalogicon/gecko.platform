using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Gecko.SharedKernel;
using Gecko.Tos.Application.Reports;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// The Vector list reports rebuilt (owner 2026-10-08) through the real host: each answers a PDF and an
/// Excel sheet whose grid has the RDL's headers in the RDL's order, the box that was gated is a row,
/// and the summary counts it under its line and size/type.
///
/// Boxes are ZZTU numbers booked with a ZZR- carrier ref, gated through the real barrier, removed in finally.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class ListReportApiTests(TosApiFactory api)
{
    private const string Prefix = "ZZR-";
    private const string Reports = "/api/tos/reports";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewRef() => $"{Prefix}{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static string NewBox()
    {
        var ten = $"ZZTU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    private static async Task GateInAsync(HttpClient client, string carrierRef, string box, string orderType, CancellationToken ct)
    {
        var booked = await client.PostAsJsonAsync("/api/tos/bookings", new
        {
            branchId = SctLcb01, orderTypeCode = orderType, lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
            validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
            containers = new object[] { new { containerNo = box } },
        }, ct);
        Assert.True(booked.StatusCode == HttpStatusCode.Created, $"booking returned {(int)booked.StatusCode}: {await booked.Content.ReadAsStringAsync(ct)}");

        var gated = await client.PostAsJsonAsync("/api/tos/gate/transactions", new
        {
            branchId = SctLcb01, containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT",
            tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 18000m, customsPermitNo = "ZZ-PERMIT-1",
            truck = new { plate = "70-4321", driverName = "Somchai P." }, grossWeightKg = 24100m, weightSource = "WEIGHBRIDGE",
            seals = new object[] { new { sealNo = $"ZZ-{box[^4..]}", sealType = "LINE", isIntact = true } },
            transactionAt = DateTimeOffset.UtcNow.AddHours(-2),
        }, ct);
        Assert.True(gated.StatusCode == HttpStatusCode.Created, $"gate in returned {(int)gated.StatusCode}: {await gated.Content.ReadAsStringAsync(ct)}");
    }

    /// <summary>The sheet's grid header row and the row of <paramref name="box"/>, as text.</summary>
    private static (List<string> Header, List<string>? Row, List<string> Lines) Read(byte[] xlsx, string firstHeader, string box, int boxColumn)
    {
        using var book = new XLWorkbook(new MemoryStream(xlsx));
        var sheet = book.Worksheet(1);
        var rows = sheet.RowsUsed().Select(r => r.Cells(1, sheet.LastColumnUsed()!.ColumnNumber()).Select(c => c.GetFormattedString()).ToList()).ToList();
        var header = rows.First(r => r[0] == firstHeader);
        var row = rows.FirstOrDefault(r => r.Count > boxColumn && r[boxColumn] == box);
        return (header.TakeWhile(h => h.Length > 0).ToList(), row, rows.Select(r => string.Join("|", r)).ToList());
    }

    [Fact]
    public async Task Gate_in_out_answers_the_RDL_columns_as_pdf_and_excel()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        try
        {
            await GateInAsync(client, carrierRef, box, "IMP CY/CY", ct);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var query = $"branchId={SctLcb01}&dateFrom={today.AddDays(-1):yyyy-MM-dd}&dateTo={today.AddDays(1):yyyy-MM-dd}&bookingBlNo={carrierRef}";

            var pdf = await client.GetAsync($"{Reports}/gate-in-out.pdf?{query}", ct);
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
            Assert.Equal(ListReport.PdfType, pdf.Content.Headers.ContentType?.MediaType);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync(ct))[..4]));

            var xlsx = await client.GetAsync($"{Reports}/gate-in-out.xlsx?{query}", ct);
            Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
            Assert.Equal(ListReport.XlsxType, xlsx.Content.Headers.ContentType?.MediaType);
            var (header, row, lines) = Read(await xlsx.Content.ReadAsByteArrayAsync(ct), "item", box, 2);
            Assert.Equal(GateInOutReport.Columns.Select(c => c.Header), header);
            Assert.NotNull(row);
            Assert.Equal((carrierRef, "20'GP", "MAEU", "70-4321", $"ZZ-{box[^4..]}"), (row[1], row[3], row[12], row[15], row[17]));
            Assert.NotEqual("", row[7]);   // Full In: the import box came in full
            // The summary: the line's row and the 20 GP column.
            Assert.Contains(lines, l => l.StartsWith("SUMMARY", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("MAEU|1|", StringComparison.Ordinal));

            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Reports}/gate-in-out.pdf?branchId={SctLcb01}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Reports}/gate-in-out.doc?{query}", ct)).StatusCode);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    [Fact]
    public async Task The_full_in_yard_report_lists_the_box_standing_in_the_yard()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        try
        {
            await GateInAsync(client, carrierRef, box, "IMP CY/CY", ct);

            var xlsx = await client.GetAsync($"{Reports}/full-in-yard.xlsx?branchId={SctLcb01}&bookingBlNo={carrierRef}", ct);
            Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
            var (header, row, lines) = Read(await xlsx.Content.ReadAsByteArrayAsync(ct), "Container No", box, 0);
            Assert.Equal(InYardReports.FullColumns.Select(c => c.Header), header);
            Assert.NotNull(row);
            Assert.Equal(("MAEU", carrierRef, "20", "GP", "IMP CY/CY", $"ZZ-{box[^4..]}"),
                (row[1], row[2], row[4], row[5], row[6], row[11]));
            Assert.Contains(row[8], new[] { "1", "2" });   // Vector's DATEDIFF + 1: in today, or yesterday across the depot's midnight
            Assert.Contains(lines, l => l.StartsWith("GRAND TOTAL|", StringComparison.Ordinal));

            // A full box is not in the empty report.
            var empty = await client.GetAsync($"{Reports}/empty-in-yard.xlsx?branchId={SctLcb01}&bookingBlNo={carrierRef}", ct);
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            var (emptyHeader, emptyRow, _) = Read(await empty.Content.ReadAsByteArrayAsync(ct), "Container No", box, 0);
            Assert.Equal(InYardReports.EmptyColumns.Select(c => c.Header), emptyHeader);
            Assert.Null(emptyRow);

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Reports}/full-in-yard.pdf?branchId={SctLcb01}&bookingBlNo={carrierRef}", ct)).StatusCode);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    [Fact]
    public async Task A_depot_the_caller_does_not_cover_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var clerk = await api.ClientForAsync(TosApiFactory.SctGateLcb);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await clerk.GetAsync($"{Reports}/empty-in-yard.pdf?branchId={TestDatabase.SctBkk01}", ct)).StatusCode);
    }

    [Fact]
    public void The_summary_counts_every_row_by_party_size_and_type_sorted_as_SSRS_sorts()
    {
        var m = SummaryMatrix.Of(
            [("MAEU", "40", "HC"), ("APL", "20", "GP"), ("MAEU", "20", "GP"), ("MAEU", "20", "GP")],
            "SUMMARY", splitHeader: false, "SUM", "Total");
        Assert.Equal([("20", "GP"), ("40", "HC")], m.Columns);
        Assert.Equal(["APL", "MAEU"], m.Rows.Select(r => r.Label));
        Assert.Equal([2, 1], m.Rows[1].Counts);
        Assert.Equal([3, 1], m.ColumnTotals);
    }
}
