using Gecko.Data.Documents;
using Gecko.MasterData.Contracts;
using Gecko.Tos.Domain;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static Gecko.Tos.Application.Reports.GateInOutReport;
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

    // ── TMS.Operation.OutBound.TruckTurnAround ──

    /// <summary>
    /// มาตรฐานปล่อยตู้เปล่า CY, the desktop's "Truck Turn Around" (Report.usp_OutBound_TruckTurnAround): every empty gate-out
    /// of the trucks that arrived on the depot days asked for, by arrival, with the truck's arrival (document contact) and
    /// exit times and the minutes between (the visit's dwell; blank while the truck is still in). หมายเหตุ prints the agent
    /// code, as the RDL did. Gecko times the truck visit, so every box on one truck shows the same times. A4 landscape (the
    /// RDL says portrait but is drawn landscape).
    /// </summary>
    public static async Task<TabularReport> TruckTurnAroundAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, DateOnly from, DateOnly to, string? agentCode, CancellationToken ct)
    {
        var start = BranchClock.StartOf(h.Branch, from);
        var end = BranchClock.StartOf(h.Branch, to.AddDays(1));
        var query =
            from g in db.GateTransactions.AsNoTracking()
            join v in db.TruckVisits on g.TruckVisitId equals v.TruckVisitId
            where g.BranchId == h.Branch.BranchId && g.Status == "COMPLETED" && g.DeletedAt == null
                  && g.Direction == GateRules.Out && g.FullEmpty == GateRules.Empty
                  && v.DeletedAt == null && v.ArrivedAt >= start && v.ArrivedAt < end
            select new { g.ContainerNo, g.EquipmentTypeCode, g.LinePartyCode, g.EirNo, v.TruckPlate, v.ArrivedAt, v.GateOutAt, v.DwellMinutes };
        if (Clean(agentCode) is { } agent) query = query.Where(x => x.LinePartyCode == agent);
        var moves = (await query.ToListAsync(ct)).OrderBy(x => x.ArrivedAt).ThenBy(x => x.EirNo, StringComparer.Ordinal).ToList();
        var types = await TypesAsync(master, moves.Select(m => m.EquipmentTypeCode), ct);

        var rows = moves.Select((m, i) =>
        {
            var (size, type) = SizeType(types, m.EquipmentTypeCode);
            var arrived = Local(h.Branch, m.ArrivedAt);
            return new TabularRow([i + 1, arrived, m.TruckPlate, arrived, m.GateOutAt is { } o ? Local(h.Branch, o) : null,
                m.ContainerNo, size, type, m.DwellMinutes ?? (m.GateOutAt is { } x ? (int)(x - m.ArrivedAt).TotalMinutes : null), m.LinePartyCode]);
        }).ToList();

        return new TabularReport(
            FileName: $"TruckTurnAround_{from:yyyyMMdd}-{to:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 0.5,
            Heading: [new(h.BranchName, 10, Bold: true), new("มาตรฐานปล่อยตู้เปล่า CY", 10, Bold: true)],
            HeadingRight: [new($"Printed By: {h.PrintedBy}"), new($"Print Date: {h.PrintedAt:dd-MM-yyyy HH:mm}")],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(1.30937), new(2.5, "dd-MM-yyyy"), new(2.5), new(2.79104, "HH:mm"), new(2.5, "HH:mm"), new(2.5), new(1.12417),
                new(1.65333), new(2.5, "0;(0)"), new(2.5),
            ],
            HeaderRows:
            [
                [new("ลำดับ"), new("วัน/เดือน/ปี"), new("ทะเบียนรถ"), new("เวลาติดต่อเอกสาร"), new("เวลาออก"), new("เบอร์ตู้"),
                 new("ขนาด"), new("ประเภท"), new("รวมเวลา"), new("หมายเหตุ")],
            ],
            Rows: rows,
            PageLabel: "Page No#");
    }
}
