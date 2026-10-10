using Gecko.Data.Documents;

namespace Gecko.Revenue.Application.Reports;

/// <summary>
/// Vector's unstuffing reports (TMS.Accounting.UnstuffingActivity and TMS.Accounting.InBoundContainer "UNSTUFFING"): import
/// CFS/CYD boxes by the day they were unstuffed (Vector's BookingContainer.StuffUnStuffDate). Owner 2026-10-10: built as
/// the RDLs are; Gecko records no unstuffing yet (gap D9 — no unstuff event or date per booked box), so the reports print
/// their headings, columns and total row with no lines. With an unstuff date, the lines become the IMP CFS / IMP CYD boxes
/// unstuffed in the window with their LO/LO (the RDL's SL002-CR), unstuffing (SU001-CR) and FSC (SF001-CR) charges.
/// </summary>
internal static class UnstuffingReports
{
    private const string Money = "#,0.00;(#,0.00);\"-\"";

    /// <summary>
    /// TMS.Accounting.UnstuffingActivity (Report.usp_Accounting_UnstuffingActivity) — UNSTUFFING ACTIVITY: NO, container,
    /// size/type, status (order type), the unstuffing charge, vessel name, voyage; then the total. No lines until Gecko
    /// records unstuffing (D9).
    /// </summary>
    public static TabularReport Activity(AccountingReportContext c) => new(
        FileName: $"UnstuffingActivity_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
        Page: ReportPage.A4Portrait,
        MarginCm: 0.3,
        Heading: [new(c.BranchName, 10, Bold: true), new("UNSTUFFING ACTIVITY", 10, Bold: true)],
        HeadingRight: [],
        Preamble: [],
        PreambleRight: [],
        Columns:
        [
            new(1.0, null, CellAlign.Center), new(2.8), new(1.6, null, CellAlign.Center), new(2.4), new(2.4, Money, CellAlign.Right),
            new(4.0), new(2.0),
        ],
        HeaderRows:
        [
            [new("NO"), new("CONTAINER NO."), new("SIZE/TYPE"), new("STATUS"), new("UNSTUFFING"), new("VESSEL NAME"), new("VOYAGE")],
        ],
        Rows: [new TabularRow([null, null, null, null, 0m, null, null], RowKind.Total)]);

    /// <summary>
    /// TMS.Accounting.InBoundContainer (Report.usp_Accounting_Unstuffing) — CONTAINER  INBOUND REPORT for a vessel/voyage:
    /// container, size, LO/LO, unstuffing and FSC charges, status; then the totals. Owner defaults: the window is
    /// dateFrom..dateTo (the RDL's query used DateFrom for both ends), a line per box. No lines until Gecko records
    /// unstuffing (D9).
    /// </summary>
    public static TabularReport Inbound(AccountingReportContext c, string? lineCode, string? vesselCode, string? voyage) => new(
        FileName: $"InBoundContainer_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
        Page: ReportPage.A4Portrait,
        MarginCm: 2.54,
        Heading:
        [
            new(c.BranchName, 10, Bold: true),
            new("CONTAINER  INBOUND REPORT", 10, Bold: true),
            new($"CARRIER: {lineCode}"),
            new($"VESSEL/VOY: {vesselCode} {voyage}    ARRIVED DATE:"),
        ],
        HeadingRight: [],
        Preamble: [],
        PreambleRight: [],
        Columns:
        [
            new(2.8), new(1.4, null, CellAlign.Center), new(2.0, Money, CellAlign.Right), new(2.0, Money, CellAlign.Right),
            new(2.0, Money, CellAlign.Right), new(2.0),
        ],
        HeaderRows:
        [
            [new("CONTAINER NO."), new("SIZE"), new("LO/LO"), new("UNSTUFFING"), new("FSC"), new("STATUS")],
            [new(""), new(""), new("CHARGE"), new("CHARGE"), new("CHARGE"), new("")],
        ],
        Rows: [new TabularRow([null, null, 0m, 0m, 0m, null], RowKind.Total)],
        PageLabel: null);
}
