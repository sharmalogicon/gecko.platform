using Gecko.Data.Documents;
using Gecko.MasterData.Contracts;
using Gecko.Tos.Domain;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static Gecko.Tos.Application.Reports.GateInOutReport;
using static Gecko.Tos.Application.Reports.OperationReportKit;

namespace Gecko.Tos.Application.Reports;

/// <summary>Who asked and when, printed in every Operation report's heading.</summary>
internal sealed record ReportHeading(BranchClock.Branch Branch, string BranchName, string PrintedBy, DateTimeOffset PrintedAt);

/// <summary>
/// Vector's TMS.Operation.InBound.* reports (owner 2026-10-11) over Gecko's gate: a move is a COMPLETED gate transaction
/// at the depot, selected by its direction and full/empty (movement codes differ by tenant), on the depot's days — the
/// To day included (Vector's procs cut it at midnight and lost it).
/// </summary>
internal static class InboundReports
{
    /// <summary>A completed move with its booking, truck and vessel call.</summary>
    internal sealed class Move
    {
        public Guid GateTransactionId { get; init; }
        public Guid BookingContainerId { get; init; }
        public DateTimeOffset At { get; init; }
        public string ContainerNo { get; init; } = "";
        public string Direction { get; init; } = "";
        public string FullEmpty { get; init; } = "";
        public string MovementCode { get; init; } = "";
        public string LineCode { get; init; } = "";
        public string? EquipmentTypeCode { get; init; }
        public string? ConditionCode { get; init; }
        public string? EirNo { get; init; }
        public decimal? GrossKg { get; init; }
        public decimal? VgmKg { get; init; }
        public string? Remarks { get; init; }
        public Guid BookingId { get; init; }
        public string OrderNo { get; init; } = "";
        public string BookingTypeCode { get; init; } = "";
        public string OrderTypeCode { get; init; } = "";
        public string? DirectionCode { get; init; }
        public string? CarrierRef { get; init; }
        public string? CustomerRef { get; init; }
        public string? CustomerCode { get; init; }
        public string? PolPortCode { get; init; }
        public string TruckPlate { get; init; } = "";
        public string? HaulierCode { get; init; }
        public string? VesselCode { get; init; }
        public string? VoyageIn { get; init; }
        public string? VoyageOut { get; init; }
        public DateTimeOffset? Eta { get; init; }
        public string? TerminalCode { get; init; }

        public string? BookingBlNo => GateInOutReport.BookingBlNo(CarrierRef, CustomerRef);
        public string? Voyage => GateInOutReport.Voyage(DirectionCode ?? "", VoyageIn, VoyageOut);
    }

    /// <summary>The depot's completed moves in [from, to] (depot days, both inclusive).</summary>
    public static IQueryable<Move> Moves(TosDbContext db, BranchClock.Branch branch, DateOnly from, DateOnly to)
    {
        var start = BranchClock.StartOf(branch, from);
        var end = BranchClock.StartOf(branch, to.AddDays(1));
        return
            from g in db.GateTransactions.AsNoTracking()
            where g.BranchId == branch.BranchId && g.Status == "COMPLETED" && g.DeletedAt == null && g.TransactionAt >= start && g.TransactionAt < end
            join b in db.Bookings on g.BookingId equals b.BookingId
            join v in db.TruckVisits on g.TruckVisitId equals v.TruckVisitId
            join c in db.VesselCalls on b.VesselCallId equals c.VesselCallId into calls
            from c in calls.DefaultIfEmpty()
            join l in db.VesselCallLines on b.VesselCallLineId equals l.VesselCallLineId into lines
            from l in lines.DefaultIfEmpty()
            select new Move
            {
                GateTransactionId = g.GateTransactionId, BookingContainerId = g.BookingContainerId, At = g.TransactionAt,
                ContainerNo = g.ContainerNo, Direction = g.Direction, FullEmpty = g.FullEmpty, MovementCode = g.MovementCode,
                LineCode = g.LinePartyCode, EquipmentTypeCode = g.EquipmentTypeCode, ConditionCode = g.ConditionCode, EirNo = g.EirNo,
                GrossKg = g.GrossWeightKg, VgmKg = g.VgmKg, Remarks = g.Remarks,
                BookingId = b.BookingId, OrderNo = b.OrderNo, BookingTypeCode = b.BookingTypeCode, OrderTypeCode = b.OrderTypeCode,
                DirectionCode = b.DirectionCode, CarrierRef = b.CarrierRef, CustomerRef = b.CustomerRef, CustomerCode = b.CustomerPartyCode,
                PolPortCode = b.PolPortCode, TruckPlate = v.TruckPlate, HaulierCode = v.HaulierPartyCode,
                VesselCode = c == null ? null : c.VesselCode,
                VoyageIn = l != null && l.VoyageIn != null ? l.VoyageIn : c == null ? null : c.OperatorVoyageIn,
                VoyageOut = l != null && l.VoyageOut != null ? l.VoyageOut : c == null ? null : c.OperatorVoyageOut,
                Eta = c == null ? null : c.Eta, TerminalCode = c == null ? null : c.TerminalCode,
            };
    }

    // ── TMS.Operation.InBound.DailyImportFullIn ──

    /// <summary>
    /// Daily IMPORT FULL-IN (Report.usp_InBound_DailyImportFullIn): the IMPORT full gate-ins on the depot days asked for, a
    /// block per agent (the move's line): a row per day with FULL IN (every move that day) and the CY / CYD / CFS boxes by
    /// size 20' / 40' / 45' (the order type's shipment type: FCL, LCL-CYD, LCL-CFS), then the agent's Total row. FULL IN
    /// counts every move, so it can exceed CY + CYD + CFS, as on Vector.
    /// </summary>
    public static async Task<TabularReport> DailyImportFullInAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, DateOnly from, DateOnly to, string? agentCode, CancellationToken ct)
    {
        var query = Moves(db, h.Branch, from, to).Where(m => m.Direction == GateRules.In && m.FullEmpty == GateRules.Full && m.BookingTypeCode == "IMPORT");
        if (Clean(agentCode) is { } agent) query = query.Where(m => m.LineCode == agent);
        var moves = await query.ToListAsync(ct);
        var types = await TypesAsync(master, moves.Select(m => m.EquipmentTypeCode), ct);

        string[] shipments = [Fcl, LclCyd, LclCfs];
        string[] sizes = ["20", "40", "45"];
        object?[] Counts(IReadOnlyCollection<Move> set) =>
        [
            set.Count,
            .. shipments.SelectMany(s => sizes.Select(z => (object?)set.Count(m =>
                ShipmentType(m.OrderTypeCode) == s && SizeType(types, m.EquipmentTypeCode).Size == z))),
        ];

        TabularColumn Mid(double cm) => new(cm, null, CellAlign.Center);
        IReadOnlyList<TabularColumn> columns =
        [
            new(2.5), Mid(1.78562), Mid(1.62687), Mid(1.65333), Mid(1.62688), Mid(1.62688), Mid(1.70625), Mid(1.49458),
            Mid(1.62688), Mid(1.52104), Mid(1.44167),
        ];
        var blocks = moves.GroupBy(m => m.LineCode).OrderBy(g => g.Key, StringComparer.Ordinal).Select(agent =>
        {
            var rows = agent.GroupBy(m => LocalDay(h.Branch, m.At)!.Value).OrderBy(d => d.Key)
                .Select(day => new TabularRow([day.Key.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture), .. Counts(day.ToList())]))
                .ToList();
            var all = agent.ToList();
            // The RDL's Total row spans each shipment type's three sizes with one figure; it prints in the first of them.
            object?[] TotalOf(string s) => [all.Count(m => ShipmentType(m.OrderTypeCode) == s && sizes.Contains(SizeType(types, m.EquipmentTypeCode).Size)), null, null];
            rows.Add(new TabularRow(["Total", all.Count, .. TotalOf(Fcl), .. TotalOf(LclCyd), .. TotalOf(LclCfs)], RowKind.Subtotal));
            return new TabularBlock(agent.Key, columns,
                [
                    [new HeaderCell("Date"), new HeaderCell("FULL IN"), new HeaderCell("CY", 3), new HeaderCell("CYD", 3), new HeaderCell("CFS", 3)],
                    [new HeaderCell(agent.Key), new HeaderCell(""), .. Enumerable.Range(0, 3).SelectMany(_ => sizes.Select(z => new HeaderCell(z + "'")))],
                ],
                rows);
        }).ToList();

        return new TabularReport(
            FileName: $"DailyImportFullIn_{from:yyyyMMdd}-{to:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 1.27,
            Heading:
            [
                new(h.BranchName, 12), new("<<Daily Import FULL IN>>", 12),
                new($"From Gate In: {Day(from)}   To Gate In: {Day(to)}", 12),
            ],
            HeadingRight: [new($"Print Date: {h.PrintedAt:dd-MM-yyyy HH:mm}")],
            Preamble: [],
            PreambleRight: [],
            Columns: columns,
            HeaderRows: [],
            Rows: [],
            PageLabel: "Page No#",
            After: blocks);
    }
}
