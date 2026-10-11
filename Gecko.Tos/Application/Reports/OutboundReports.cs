using Gecko.Data.Documents;
using static Gecko.Tos.Application.Reports.OperationReportKit;

namespace Gecko.Tos.Application.Reports;

/// <summary>Vector's TMS.Operation.OutBound.* reports (owner 2026-10-11) over Gecko's bookings and gate.</summary>
internal static class OutboundReports
{
    // ── reports whose rows come from Vector's M&R module, which Gecko does not have yet ──

    /// <summary>
    /// PTI REPORT (Report.usp_OutBound_PTI): Vector listed the PTI work orders (M&amp;R) started in the window. Gecko keeps no
    /// work orders yet, so the heading and the RDL's nine columns print with no lines (as the unstuffing reports do).
    /// </summary>
    public static TabularReport Pti(ReportHeading h, DateOnly from, DateOnly to) => new(
        FileName: $"PTI_{from:yyyyMMdd}-{to:yyyyMMdd}",
        Page: ReportPage.A4Landscape,
        MarginCm: 2.54,
        Heading: [new(h.BranchName, 10, Bold: true), new("PTI REPORT", 10, Bold: true)],
        HeadingRight: [new($"Printed By: {h.PrintedBy}"), new($"Printed On: {h.PrintedAt:dd/MM/yyyy HH:mm}")],
        Preamble: [],
        PreambleRight: [],
        Columns:
        [
            new(1.15062), new(2.5), new(1.28292), new(3.55833, "dd-MM-yyyy"), new(2.60583), new(2.5), new(2.5, "dd-MM-yyyy"),
            new(2.5, "dd-MM-yyyy"), new(2.5),
        ],
        HeaderRows:
        [
            [new("Item"), new("Container No"), new("Size"), new("Empty Gate In Date"), new("Container Grade "), new("Status"),
             new("Washing Date"), new("PTIDate"), new("Damage Code")],
        ],
        Rows: [],
        PageLabel: "Page No#");

    /// <summary>
    /// ContainerRepairSummaryList (Report.usp_OutBound_ContainerRepairSummaryList): Vector's was a stub over repair estimates
    /// (fixed estimate numbers, a charge amount unrelated to the box). Gecko keeps no repair estimates yet, so the heading,
    /// the columns and the TOTAL row print with no lines; the total is the sum of CN, DM and Sub Total = CN + DM (the RDL
    /// multiplied them).
    /// </summary>
    public static TabularReport ContainerRepairSummary(ReportHeading h, DateOnly from, DateOnly to)
    {
        TabularColumn Mid(double inches, string? format = null) => new(inches * 2.54, format, CellAlign.Center);
        return new(
            FileName: $"ContainerRepairSummaryList_{from:yyyyMMdd}-{to:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 2.54,
            Heading: [new(h.BranchName, 10, Bold: true), new("ContainerRepairSummaryList", 10, Bold: true)],
            HeadingRight: [new($"Print Date: {h.PrintedAt:dd-MM-yyyy HH:mm}")],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                Mid(0.48958), Mid(1.1875), Mid(0.85417), Mid(0.94792), Mid(0.82292), Mid(1.32292), Mid(1, "#,0.00"), Mid(1, "#,0.00"),
                Mid(1, "#,0.00"),
            ],
            HeaderRows:
            [
                [new("Item"), new("Container No"), new("Estimate No.", ColSpan: 3), new("Estimate Date"), new("CN Amount"),
                 new("DM Amount"), new("Sub Total")],
            ],
            Rows: [new TabularRow(["TOTAL", null, null, null, null, null, 0m, 0m, 0m], RowKind.Total)],
            PageLabel: "Page No#");
    }
}
