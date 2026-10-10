using System.Net;
using ClosedXML.Excel;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// GET /api/revenue/reports/accounting/unstuffing-activity and inbound-container (Vector UnstuffingActivity / InBoundContainer),
/// owner 2026-10-10: built as the RDLs are; Gecko records no unstuffing yet (D9), so they print the headings, columns and
/// a zero total row with no lines.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class UnstuffingReportsApiTests(RevenueApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    [Theory]
    [InlineData("unstuffing-activity", "UNSTUFFING ACTIVITY", "VESSEL NAME")]
    [InlineData("inbound-container", "CONTAINER  INBOUND REPORT", "FSC")]
    public async Task The_unstuffing_reports_print_their_layout_with_no_lines(string route, string title, string column)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);

        var response = await client.GetAsync($"/api/revenue/reports/accounting/{route}.xlsx?branchId={SctLcb01}&dateFrom=2026-09-01&dateTo=2026-09-30", ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
        var cells = book.Worksheet(1).CellsUsed().Select(c => c.GetFormattedString()).ToList();
        Assert.Contains(title, cells);
        Assert.Contains(column, cells);

        var pdf = await client.GetAsync($"/api/revenue/reports/accounting/{route}.pdf?branchId={SctLcb01}&dateFrom=2026-09-01&dateTo=2026-09-30", ct);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/revenue/reports/accounting/{route}.pdf?branchId={SctLcb01}&dateFrom=2026-09-01", ct)).StatusCode);
    }
}
