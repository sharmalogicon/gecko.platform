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
        public Guid? SurveyId { get; init; }
        public string? NextLocationCode { get; init; }
        public string? NextPrevLocation { get; init; }
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
                SurveyId = g.SurveyId, NextLocationCode = g.NextLocationCode, NextPrevLocation = b.NextPrevLocation,
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

    /// <summary>A move's damage codes from its gate survey, de-duplicated, in line order (Vector printed a row per damage).</summary>
    private static async Task<ILookup<Guid, string>> DamagesAsync(TosDbContext db, IEnumerable<Guid?> surveyIds, CancellationToken ct)
    {
        var ids = surveyIds.OfType<Guid>().Distinct().ToList();
        var found = new List<(Guid Survey, short Line, string Code)>();
        foreach (var slice in ids.Chunk(2000))
            found.AddRange((await db.SurveyDamages.AsNoTracking().Where(d => slice.Contains(d.SurveyId))
                    .Select(d => new { d.SurveyId, d.LineNo, d.DamageCode }).ToListAsync(ct))
                .Select(d => (d.SurveyId, d.LineNo, d.DamageCode)));
        return found.OrderBy(d => d.Line).DistinctBy(d => (d.Survey, d.Code)).ToLookup(d => d.Survey, d => d.Code);
    }

    // ── TMS.Operation.InBound.EmptyOut ──

    private static readonly string[] EmptyOutSizes =
        ["20GP", "20HC", "40GP", "40HC", "45HC", "20RF", "40RF", "40RH", "20HG", "40HG", "40OT", "20FR", "40FR", "20TK"];

    /// <summary>
    /// INBOUND EMPTY OUT REPORT (Report.usp_InBound_EmptyOut; menus "EMPTY OUT" and "INBOUND Empty Out"): every empty gate-out
    /// on the depot days asked for — whatever the booking type, as on Vector — in gate order, then the RDL's one-row Summary
    /// over its 14 fixed size/types and SUM. One row per move, its damage codes joined (Vector repeated the move per damage
    /// and counted each copy). Time is 24-hour (the RDL's hh:mm dropped AM/PM). The RDL has no page header or footer: the
    /// heading prints once and there is no page number.
    /// </summary>
    public static async Task<TabularReport> EmptyOutAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, DateOnly from, DateOnly to, string? agentCode, CancellationToken ct)
    {
        var query = Moves(db, h.Branch, from, to).Where(m => m.Direction == GateRules.Out && m.FullEmpty == GateRules.Empty);
        if (Clean(agentCode) is { } agent) query = query.Where(m => m.LineCode == agent);
        var moves = (await query.ToListAsync(ct)).OrderBy(m => m.At).ThenBy(m => m.EirNo, StringComparer.Ordinal).ToList();
        var types = await TypesAsync(master, moves.Select(m => m.EquipmentTypeCode), ct);
        var parties = await master.PartiesAsync(moves.Select(m => m.CustomerCode).OfType<string>().Distinct(), ct);
        var vessels = await master.VesselsAsync(moves.Select(m => m.VesselCode).OfType<string>().Distinct(), ct);
        var damages = await DamagesAsync(db, moves.Select(m => m.SurveyId), ct);
        string SizeOf(Move m)
        {
            var (size, type) = SizeType(types, m.EquipmentTypeCode);
            return $"{size}{type}";
        }

        var rows = moves.Select(m =>
        {
            var at = Local(h.Branch, m.At);
            return new TabularRow([m.ContainerNo, SizeOf(m), m.TruckPlate, m.HaulierCode, m.NextLocationCode ?? m.NextPrevLocation,
                m.VesselCode is { } v ? vessels.GetValueOrDefault(v)?.VesselName : null, m.Voyage,
                m.CustomerCode is { } c ? parties.GetValueOrDefault(c)?.Name : null, at, at, m.ConditionCode,
                m.SurveyId is { } s && damages[s].Any() ? string.Join(", ", damages[s]) : null, m.Remarks]);
        }).ToList();

        double[] summaryWidths = [2.46472, 1.23125, 1.27453, 1.21751, 1.15208, 1.14917, 1.07125, 0.99188, 1.09234, 1.15599, 1.07125, 1.08479, 1.08479, 1.07125, 0.96542, 1.04479];
        var summary = new TabularBlock("Summary",
            summaryWidths.Select((w, i) => new TabularColumn(w, null, i == 0 ? CellAlign.Left : CellAlign.Center)).ToList(),
            [[new HeaderCell("Container Size"), .. EmptyOutSizes.Select(s => new HeaderCell(s)), new HeaderCell("SUM")]],
            [new TabularRow(["TOTAL", .. EmptyOutSizes.Select(s => (object?)moves.Count(m => SizeOf(m) == s)), moves.Count])]);

        return new TabularReport(
            FileName: $"EmptyOut_{from:yyyyMMdd}-{to:yyyyMMdd}",
            Page: ReportPage.A3Landscape,
            MarginCm: 1.27,
            Heading: [],
            HeadingRight: [],
            Preamble:
            [
                new(h.BranchName, 10, Bold: true), new("INBOUND EMPTY OUT REPORT", 10, Bold: true),
                new($"From Gate Out Date :{Day(from, "dd/MM/yyyy")}     To Gate Out Date : {Day(to, "dd/MM/yyyy")}", 10, Bold: true),
                new($"AGENT:{Clean(agentCode)}", 10, Bold: true),
            ],
            PreambleRight: [],
            Columns:
            [
                new(2.32854), new(1.07125), new(1.57974), new(2.43376), new(2.15625), new(3.63771), new(1.38875), new(5.73276),
                new(1.70625, "dd/MM/yyyy"), new(1.20354, "HH:mm"), new(1.41521), new(2.12959), new(3.92875),
            ],
            HeaderRows:
            [
                [new("Container No"), new("Size"), new("Plate No."), new("TRUCK COMPANY"), new("NEXT LOCATION"), new("Vessel Name"),
                 new("Voyage No"), new("Consignee / SHIPPER"), new("Date"), new("Time"), new("Status"), new("Damage Condition"), new("Remarks")],
            ],
            Rows: rows,
            PageLabel: null,
            After: [summary]);
    }

    // ── TMS.Operation.InBound.ImportExportSummary ──

    /// <summary>
    /// SUMMARY (IMPORT - EXPORT) (Report.usp_InBound_ImportExportSummary): one year's moves of one kind (Vector's movement
    /// name, FULL IN by default) at the depot, optionally of one booking type, as an agent × month matrix of CONT (moves) and
    /// TEU (20' = 1, every other size 2 — the RDL's rule, not master data's 2.25 for 45'), with TOTAL column and Total row.
    /// The title stays "SUMMARY GATE IN CONTAINER" whatever is asked, as on Vector; zero prints blank in the agent rows.
    /// </summary>
    public static async Task<TabularReport> ImportExportSummaryAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, int year, string? movement, string? bookingType, CancellationToken ct)
    {
        var (direction, load) = Movement(movement ?? "FULL IN") ?? ("IN", "FULL");
        var query = Moves(db, h.Branch, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31))
            .Where(m => m.Direction == direction && m.FullEmpty == load);
        if (Clean(bookingType) is { } type) query = query.Where(m => m.BookingTypeCode == type);
        var moves = await query.ToListAsync(ct);
        var types = await TypesAsync(master, moves.Select(m => m.EquipmentTypeCode), ct);
        int Teu(Move m) => SizeType(types, m.EquipmentTypeCode).Size == "20" ? 1 : 2;
        int Month(Move m) => Local(h.Branch, m.At).Month;
        object?[] Cells(IReadOnlyCollection<Move> set) =>
        [
            .. Enumerable.Range(1, 12).SelectMany(month => new object?[] { set.Count(m => Month(m) == month), set.Where(m => Month(m) == month).Sum(Teu) }),
            set.Count, set.Sum(Teu),
        ];

        var rows = moves.GroupBy(m => m.LineCode).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TabularRow([g.Key, .. Cells(g.ToList())])).ToList();
        // The RDL's Total row has no format: its zeros print.
        rows.Add(new TabularRow(["Total", .. Cells(moves).Select(c => (object?)c?.ToString())], RowKind.Subtotal));

        double[] widths = [1.19944, 1.16828, 1.17709, 1.12417, 1.0977, 1.24354, 1.12417, 1.11187, 1.15063, 1.15063, 1.12417, 1.09771,
            1.15125, 1.17709, 1.12416, 1.07125, 1.60042, 1.12479, 1.15063, 1.09771, 1.12416, 1.04479, 1.41521, 1.09771, 1.0977, 1.15063];
        return new TabularReport(
            FileName: $"ImportExportSummary_{year}",
            Page: ReportPage.A3Landscape,
            MarginCm: 2,
            Heading: [new(h.BranchName, 10, Bold: true), new("<< SUMMARY GATE IN CONTAINER >>", 10, Bold: true), new($"Year:{year}", 10, Bold: true)],
            HeadingRight: [new($"Print Date: {h.PrintedAt:dd/MM/yyyy HH:mm}")],
            Preamble: [],
            PreambleRight: [],
            Columns: [new(1.94438), .. widths.Select(w => new TabularColumn(w, "0;(0);\"\"", CellAlign.Center))],
            HeaderRows:
            [
                [new("AGENT", RowSpan: 2), .. Months.Append("TOTAL").Select(m => new HeaderCell(m, ColSpan: 2))],
                [.. Enumerable.Range(0, 13).SelectMany(_ => new[] { new HeaderCell("CONT"), new HeaderCell("TEU") })],
            ],
            Rows: rows,
            PageLabel: "Page No#");
    }
}
