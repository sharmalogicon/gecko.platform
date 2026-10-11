using Gecko.Data.Documents;
using Gecko.MasterData.Contracts;
using Gecko.Tos.Domain;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static Gecko.Tos.Application.Reports.GateInOutReport;
using static Gecko.Tos.Application.Reports.OperationReportKit;

namespace Gecko.Tos.Application.Reports;

/// <summary>Vector's TMS.CustomerService.* reports (owner 2026-10-11) over Gecko's bookings and gate.</summary>
internal static class CustomerServiceReports
{
    // ── TMS.CustomerService.WeeklyMonthlyStatistics ──

    /// <summary>
    /// The desktop's "EXPORT - Monthly/Weekly" (Report.usp_CustomerService_WeeklyMonthlyStatistics): the empty and full
    /// gate-outs on the depot days asked for (any booking type unless one is asked), a block per agent (the move's line):
    /// per order type, the 20 / 40 Dry / Reefer boxes out each day as MTY OUT and FULL OUT, with a Total column and row.
    /// Only 20' and 40' are counted, as on Vector; reefer is master data's (Vector counted RH as dry). All the days print
    /// across one A3 sheet, scaled to fit, as the wide lists do.
    /// </summary>
    public static async Task<TabularReport> WeeklyMonthlyAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, DateOnly from, DateOnly to,
        string? agentCode, string? bookingType, string? orderType, CancellationToken ct)
    {
        var query = InboundReports.Moves(db, h.Branch, from, to).Where(m => m.Direction == GateRules.Out);
        if (Clean(agentCode) is { } agent) query = query.Where(m => m.LineCode == agent);
        if (Clean(bookingType) is { } type) query = query.Where(m => m.BookingTypeCode == type);
        if (Clean(orderType) is { } order) query = query.Where(m => m.OrderTypeCode == order);
        var moves = await query.ToListAsync(ct);
        var types = await TypesAsync(master, moves.Select(m => m.EquipmentTypeCode), ct);
        var days = moves.Select(m => LocalDay(h.Branch, m.At)!.Value).Distinct().Order().ToList();
        (string Size, bool Reefer)[] kinds = [("20", false), ("40", false), ("20", true), ("40", true)];
        string[] labels = ["20 Dry", "40 Dry", "20 Reefer", "40 Reefer"];
        int Count(IEnumerable<InboundReports.Move> set, DateOnly? day, string? load, (string Size, bool Reefer) kind) =>
            set.Count(m => (day is null || LocalDay(h.Branch, m.At) == day) && (load is null || m.FullEmpty == load)
                           && SizeType(types, m.EquipmentTypeCode).Size == kind.Size
                           && (m.EquipmentTypeCode is { } c && types.GetValueOrDefault(c)?.IsReefer == true) == kind.Reefer);

        IReadOnlyList<TabularColumn> columns =
        [
            new(3.24084),
            .. days.SelectMany(_ => new[] { new TabularColumn(1.65333, null, CellAlign.Center), new TabularColumn(1.60042, null, CellAlign.Center) }),
            new(1.25646, "#,0;(#,0)", CellAlign.Center),
        ];
        var blocks = moves.GroupBy(m => m.LineCode).OrderBy(g => g.Key, StringComparer.Ordinal).Select(byAgent =>
        {
            var rows = new List<TabularRow>
            {
                new(["Order Type", .. days.SelectMany(d => new object?[] { Day(d, "yyyy-MM-dd"), null }), null], RowKind.Subtotal),
            };
            foreach (var byOrder in byAgent.GroupBy(m => m.OrderTypeCode).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var set = byOrder.ToList();
                rows.Add(new TabularRow([byOrder.Key, .. days.SelectMany(_ => new object?[] { "MTY OUT", "FULL OUT" }), null], RowKind.Subtotal));
                foreach (var (kind, label) in kinds.Zip(labels))
                    rows.Add(new TabularRow([label,
                        .. days.SelectMany(d => new object?[] { Count(set, d, GateRules.Empty, kind), Count(set, d, GateRules.Full, kind) }),
                        Count(set, null, null, kind)]));
                rows.Add(new TabularRow(["Total",
                    .. days.SelectMany(d => new object?[] { kinds.Sum(k => Count(set, d, GateRules.Empty, k)), kinds.Sum(k => Count(set, d, GateRules.Full, k)) }),
                    kinds.Sum(k => Count(set, null, null, k))], RowKind.Subtotal));
            }
            return new TabularBlock(null, columns,
                [[new HeaderCell(byAgent.Key), .. days.Select(_ => new HeaderCell("DATE", ColSpan: 2)), new HeaderCell("Total")]],
                rows);
        }).ToList();

        var asked = Clean(bookingType);
        return new TabularReport(
            FileName: $"WeeklyMonthlyStatistics_{from:yyyyMMdd}-{to:yyyyMMdd}",
            Page: ReportPage.A3Landscape,
            MarginCm: 2,
            Heading:
            [
                new(h.BranchName, 10, Bold: true),
                new($"{(asked == "EXPORT" ? "OUTBOUND" : "INBOUND")}  STATISTIC OF  ………………  (Weekly , Monthly,Quarter)  ", 10, Bold: true),
                new($"BookingType: {asked}", 10, Bold: true),
                new($"From Date: {Day(from, "dd/MM/yyyy")} To Date: {Day(to, "dd/MM/yyyy")}", 10, Bold: true),
            ],
            HeadingRight: [new($"Print Date: {ListReport.Text(h.PrintedAt.DateTime, null)}")],
            Preamble: [],
            PreambleRight: [],
            Columns: columns,
            HeaderRows: [],
            Rows: [],
            PageLabel: "Page No#",
            After: blocks);
    }
}
