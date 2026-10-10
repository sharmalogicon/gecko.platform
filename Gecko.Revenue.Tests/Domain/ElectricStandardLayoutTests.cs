using ClosedXML.Excel;
using Gecko.Revenue.Application;
using Gecko.Revenue.Application.Reports;
using Gecko.Tos.Contracts;

namespace Gecko.Revenue.Tests.Domain;

/// <summary>
/// TMS.Accounting.ElectricStandard's layout from reefer lines alone: the SCT fixture has no EXPORT reefer gated out full,
/// so the live test cannot reach a printed line. Owner 2026-10-10 defaults: LADEN TOTAL is the box's electricity, the
/// size rows count boxes and days, the grand total adds the charges' own VAT, and the Total row sums the size rows.
/// Invented boxes only.
/// </summary>
public sealed class ElectricStandardLayoutTests
{
    private static readonly TimeZoneInfo Bangkok = TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "ICT", "ICT");
    private static readonly DateTimeOffset Day = new(2026, 9, 20, 9, 0, 0, TimeSpan.FromHours(7));

    private static AccountingReportContext Context()
    {
        var branch = new BranchClockInfo(Guid.NewGuid(), "TST01", Bangkok);
        return new AccountingReportContext(branch, "Test Depot", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30),
            Day.AddDays(-19), Day.AddDays(11), "tester", Day, new AccountingReports.Letterhead("Test Co.", "Somewhere", "0000000000000", "00000"));
    }

    private static ReeferReports.ReeferLine Line(string containerNo, string size, string bookingType, int days,
        ReeferReports.Charges pti, ReeferReports.Charges electricity)
    {
        var box = new TosBookedBox(Guid.NewGuid(), Guid.NewGuid(), containerNo, $"{size}RF", "EXP CY/CY", bookingType, "LINE", "VSL", "001E",
            null, null, null, Day.AddDays(-days + 1), Day);
        return new ReeferReports.ReeferLine(box, $"{size}RF", size, "RF", bookingType, "VESSEL", pti,
            new ReeferReports.Charges(0, 0, 0, 0), electricity, days);
    }

    [Fact]
    public void Export_reefers_print_their_electricity_and_the_size_summary_with_vat()
    {
        var none = new ReeferReports.Charges(0, 0, 0, 0);
        var report = ReeferReports.ElectricStandard(Context(),
        [
            Line("TSTU0000001", "20", "EXPORT", 3, new ReeferReports.Charges(1, 500, 500, 35), new ReeferReports.Charges(1, 100, 300, 21)),
            Line("TSTU0000002", "40", "EXPORT", 2, none, new ReeferReports.Charges(1, 150, 300, 21)),
            Line("TSTU0000003", "40", "IMPORT", 5, none, new ReeferReports.Charges(1, 150, 750, 52.5m)),   // not EXPORT: left out
        ]);

        using var book = new XLWorkbook(new MemoryStream(report.Xlsx()));
        var sheet = book.Worksheet(1);
        var rows = Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
            .Select(r => Enumerable.Range(1, 13).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();

        Assert.Contains(rows, r => r[0] == "REEFER CONTAINERS MOVEMENT");
        Assert.Contains(rows, r => r[0] == "DATE  BETWEEN 01/09/2026 TO 30/09/2026");
        Assert.Equal(["TSTU0000001", "20RF", "", "1", "500.00", "", "", "-", "-", "18/09/26", "20/09/26", "3", "300.00"],
            rows.Single(r => r[0] == "TSTU0000001"));
        Assert.DoesNotContain(rows, r => r[0] == "TSTU0000003");

        // Size rows, then Total = their sum: boxes, days, amounts; the grand total with the charges' VAT.
        Assert.Equal(["20RF", "1", "-", "500.00", "-", "-", "-", "1", "3", "300.00", "856.00"], rows.Single(r => r[0] == "20RF")[..11]);
        Assert.Equal(["40RF", "-", "-", "-", "-", "-", "-", "1", "2", "300.00", "321.00"], rows.Single(r => r[0] == "40RF")[..11]);
        Assert.Equal(["Total", "1", "-", "500.00", "-", "-", "-", "2", "5", "600.00", "1,177.00"], rows.Single(r => r[0] == "Total")[..11]);
        Assert.True(report.Pdf().Length > 1000);
    }
}
