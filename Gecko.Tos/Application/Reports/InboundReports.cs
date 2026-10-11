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
            HeadingRight: [new($"Print Date: {Stamp(h.PrintedAt, "dd-MM-yyyy HH:mm")}")],
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
            HeadingRight: [new($"Print Date: {Stamp(h.PrintedAt, "dd/MM/yyyy HH:mm")}")],
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

    /// <summary>The moves' seals by gate transaction, in the order they were recorded.</summary>
    private static async Task<ILookup<Guid, (string SealNo, string SealType)>> SealsAsync(TosDbContext db, IEnumerable<Guid> moveIds, CancellationToken ct)
    {
        var found = new List<(Guid Move, DateTimeOffset At, string SealNo, string SealType)>();
        foreach (var slice in moveIds.Distinct().Chunk(2000))
            found.AddRange((await db.GateTransactionSeals.AsNoTracking().Where(s => slice.Contains(s.GateTransactionId) && s.DeletedAt == null)
                    .Select(s => new { s.GateTransactionId, s.CreatedAt, s.SealNo, s.SealType }).ToListAsync(ct))
                .Select(s => (s.GateTransactionId, s.CreatedAt, s.SealNo, s.SealType)));
        return found.OrderBy(s => s.At).ToLookup(s => s.Move, s => (s.SealNo, s.SealType));
    }

    // ── TMS.Operation.InBound.NYKExternal ──

    private static readonly string[] NykSizes =
        ["20HC", "40GP", "40HC", "20GP", "20RF", "40RF", "40RH", "45HC", "20HG", "40HG", "20OT", "40OT", "20FR", "40FR", "20TK"];

    /// <summary>
    /// INBOUND FULL IN REPORT, the desktop's "NYK External" (Report.usp_InBound_NYKExternal; any agent, not only NYK): the full
    /// gate-ins on the depot days asked for, optionally of one booking type and order type, in gate order, with the RDL's 17
    /// columns — "PAY RECEIPT" prints the agent code and "Chassis No." the haulier code, as the RDL did; SCT seal and Damage
    /// blank as in the RDL; Weight the gate's gross weight in whole tonnes — then the Summary over 15 fixed size/types in the
    /// RDL's order and SUM. Completed moves only (Vector listed cancelled ones too); 24-hour time.
    /// </summary>
    public static async Task<TabularReport> NykExternalAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, DateOnly from, DateOnly to,
        string? agentCode, string? bookingType, string? orderType, CancellationToken ct)
    {
        var query = Moves(db, h.Branch, from, to).Where(m => m.Direction == GateRules.In && m.FullEmpty == GateRules.Full);
        if (Clean(agentCode) is { } agent) query = query.Where(m => m.LineCode == agent);
        if (Clean(bookingType) is { } type) query = query.Where(m => m.BookingTypeCode == type);
        if (Clean(orderType) is { } order) query = query.Where(m => m.OrderTypeCode == order);
        var moves = (await query.ToListAsync(ct)).OrderBy(m => m.At).ThenBy(m => m.EirNo, StringComparer.Ordinal).ToList();
        var types = await TypesAsync(master, moves.Select(m => m.EquipmentTypeCode), ct);
        var parties = await master.PartiesAsync(moves.Select(m => m.CustomerCode).OfType<string>().Distinct(), ct);
        var vessels = await master.VesselsAsync(moves.Select(m => m.VesselCode).OfType<string>().Distinct(), ct);
        var seals = await SealsAsync(db, moves.Select(m => m.GateTransactionId), ct);
        string? Seal(Move m, Func<string, bool> kind) => seals[m.GateTransactionId].Where(s => kind(s.SealType)).Select(s => s.SealNo).FirstOrDefault();
        string SizeOf(Move m, string between = "")
        {
            var (size, kind) = SizeType(types, m.EquipmentTypeCode);
            return $"{size}{between}{kind}";
        }

        var rows = moves.Select(m =>
        {
            var at = Local(h.Branch, m.At);
            return new TabularRow([m.ContainerNo, at, at, m.LineCode, SizeOf(m, "'"), m.TruckPlate, m.HaulierCode,
                Seal(m, IsAgentSeal), Seal(m, s => s == "CUSTOMS"), null,
                m.VesselCode is { } v ? vessels.GetValueOrDefault(v)?.VesselName : null, m.Voyage,
                m.CustomerCode is { } c ? parties.GetValueOrDefault(c)?.Name : null, m.ConditionCode, null, m.BookingBlNo,
                $"{Math.Floor((m.GrossKg ?? 0) / 1000)} TONS"]);
        }).ToList();

        double[] summaryWidths = [2.64757, 1.08703, 1.14145, 1.16417, 1.05833, 1.01459, 1.16791, 1.07125, 1.09771, 1.21709, 1.05833, 1.08479, 0.99188, 1.07125, 1.08479, 0.96542, 1.09771];
        var summary = new TabularBlock("Summary",
            summaryWidths.Select((w, i) => new TabularColumn(w, null, i == 0 ? CellAlign.Left : CellAlign.Center)).ToList(),
            [[new HeaderCell("Container Size"), .. NykSizes.Select(s => new HeaderCell(s)), new HeaderCell("SUM")]],
            [new TabularRow(["TOTAL", .. NykSizes.Select(s => (object?)moves.Count(m => SizeOf(m) == s)), moves.Count])]);

        return new TabularReport(
            FileName: $"InboundFullIn_{from:yyyyMMdd}-{to:yyyyMMdd}",
            Page: ReportPage.A3Landscape,
            MarginCm: 2,
            Heading:
            [
                new(h.BranchName, 10, Bold: true), new("<< INBOUND FULL IN REPORT >>", 10, Bold: true),
                new($"ระหว่างวันที่ Gate In :{Day(from)} ถึงวันที่ Gate In :{Day(to)}", 10, Bold: true),
                new($"Agent : {Clean(agentCode)}", 10, Bold: true),
            ],
            HeadingRight: [new($"Print Date: {Stamp(h.PrintedAt, "dd/MM/yyyy HH:mm:ss")}")],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(2.5), new(1.89146, "yyyyMMdd"), new(1.51401, "HH:mm"), new(2.20767), new(1.10796), new(1.87854), new(2.05021),
                new(2.15147), new(2.5), new(1.89413), new(5.18583), new(1.83854), new(5.63026), new(1.97083), new(1.70625), new(3.24083),
                new(1.73271),
            ],
            HeaderRows:
            [
                [new("Container No."), new("Date"), new("Time"), new("PAY RECEIPT "), new("Size"), new("Plate No."), new("Chassis No."),
                 new("Seal Agent"), new("Seal Customs"), new("SCT seal"), new("Vessel Name"), new("Voyage No"), new("Consignee"),
                 new("สภาพตู้"), new("Damage"), new("BL No."), new("Weight")],
            ],
            Rows: rows,
            PageLabel: "Page No#",
            After: [summary]);
    }

    // ── TMS.Operation.InBound.CustomsImportUnStuffing ──

    private static readonly string[] CustomsSizes =
        ["20GP", "20HC", "40GP", "40HC", "45HC", "20RF", "40RF", "40RH", "20HG", "40HG", "20OT", "40OT", "20FR", "40FR", "20TK"];

    /// <summary>
    /// สมุดควบคุมคอนเทนเนอร์ (กศก 84), the desktop's "IMPORT - Unstuffing Customs" (Report.usp_Inbound_CustomsImportUnStuffing):
    /// every IMPORT full gate-in on the depot days asked for — FCL included, as on Vector — numbered in gate order, with the
    /// shipment type as status, the box's first gate-out on that booking (full for FCL, else empty), the call's vessel, voyage,
    /// ETA and terminal (the RDL's "Loading Port" printed the booking's terminal), then the Summary by size/type and SUM.
    /// Unstuffing Date prints blank: Gecko records no unstuffing yet (gap D9). 40HG counts 40HG (the RDL repeated 20HG);
    /// 24-hour time.
    /// </summary>
    public static async Task<TabularReport> CustomsImportUnstuffingAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, DateOnly from, DateOnly to,
        string? agentCode, string? vesselCode, string? voyageNo, CancellationToken ct)
    {
        var query = Moves(db, h.Branch, from, to)
            .Where(m => m.Direction == GateRules.In && m.FullEmpty == GateRules.Full && m.BookingTypeCode == "IMPORT");
        if (Clean(agentCode) is { } agent) query = query.Where(m => m.LineCode == agent);
        if (Clean(vesselCode) is { } vessel) query = query.Where(m => m.VesselCode == vessel);
        if (Clean(voyageNo) is { } voyage) query = query.Where(m => m.VoyageIn == voyage || m.VoyageOut == voyage);
        var moves = (await query.ToListAsync(ct)).OrderBy(m => m.At).ThenBy(m => m.EirNo, StringComparer.Ordinal).ToList();
        var types = await TypesAsync(master, moves.Select(m => m.EquipmentTypeCode), ct);
        var parties = await master.PartiesAsync(moves.Select(m => m.CustomerCode).OfType<string>().Distinct(), ct);
        var vessels = await master.VesselsAsync(moves.Select(m => m.VesselCode).OfType<string>().Distinct(), ct);

        // The box's gate-outs on the same booking line.
        var lines = moves.Select(m => m.BookingContainerId).Distinct().ToList();
        var outs = new List<(Guid Box, string FullEmpty, DateTimeOffset At)>();
        foreach (var slice in lines.Chunk(2000))
            outs.AddRange((await db.GateTransactions.AsNoTracking()
                    .Where(g => slice.Contains(g.BookingContainerId) && g.Status == "COMPLETED" && g.DeletedAt == null && g.Direction == GateRules.Out)
                    .Select(g => new { g.BookingContainerId, g.FullEmpty, g.TransactionAt }).ToListAsync(ct))
                .Select(g => (g.BookingContainerId, g.FullEmpty, g.TransactionAt)));
        var outsByBox = outs.ToLookup(o => o.Box);
        string SizeOf(Move m, string between = "")
        {
            var (size, kind) = SizeType(types, m.EquipmentTypeCode);
            return $"{size}{between}{kind}";
        }

        var rows = moves.Select((m, i) =>
        {
            var status = ShipmentType(m.OrderTypeCode);
            var load = status == Fcl ? GateRules.Full : GateRules.Empty;
            var gateOut = outsByBox[m.BookingContainerId].Where(o => o.FullEmpty == load).Select(o => (DateTimeOffset?)o.At).Min();
            var vesselName = m.VesselCode is { } v ? vessels.GetValueOrDefault(v)?.VesselName ?? v : "";
            return new TabularRow([i + 1, Local(h.Branch, m.At), m.ContainerNo, SizeOf(m, "'"), status, null,
                gateOut is { } o ? Local(h.Branch, o) : null, m.BookingBlNo, $"{vesselName}/{m.Voyage}",
                m.Eta is { } eta ? Local(h.Branch, eta) : null, m.TerminalCode, m.LineCode,
                m.CustomerCode is { } c ? parties.GetValueOrDefault(c)?.Name : null, m.Remarks]);
        }).ToList();

        double[] summaryWidths = [2.5, 1.23, 1.20354, 1.12417, 1.15599, 1.09297, 1.15063, 1.09016, 1.08859, 1.12417, 1.02807, 1.20354, 1.15063, 1.15063, 1.12417, 1.33583, 1.09771];
        var summary = new TabularBlock("Summary",
            summaryWidths.Select((w, i) => new TabularColumn(w, null, i == 0 ? CellAlign.Left : CellAlign.Center)).ToList(),
            [[new HeaderCell("Container Size"), .. CustomsSizes.Select(s => new HeaderCell(s == "20TK" ? "20 TK" : s)), new HeaderCell("SUM")]],
            [new TabularRow(["TOTAL", .. CustomsSizes.Select(s => (object?)moves.Count(m => SizeOf(m) == s)), moves.Count])]);

        return new TabularReport(
            FileName: $"CustomsImportUnstuffing_{from:yyyyMMdd}-{to:yyyyMMdd}",
            Page: ReportPage.A3Landscape,
            MarginCm: 2,
            Heading:
            [
                new(h.BranchName, 10, Bold: true), new("สมุดควบคุมคอนเทนเนอร์ (กศก 84)", 10, Bold: true),
                new($"from Gate In date: {Day(from, "dd/MM/yyyy")}   to Gate In date:{Day(to, "dd/MM/yyyy")}", 10, Bold: true),
                new($"AGENT :{Clean(agentCode)}", 10, Bold: true), new($"Vessel Code :{Clean(vesselCode)}", 10, Bold: true),
                new($"Voyage No.:{voyageNo?.Trim()}", 10, Bold: true),
            ],
            HeadingRight: [new($"Printed By: {h.PrintedBy}"), new($"Printed On: {Stamp(h.PrintedAt, "dd/MM/yyyy")}")],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(1.36229), new(2.03129, "dd/MM/yyyy"), new(2.76458), new(1.15599), new(1.70308), new(3.24083),
                new(2.38443, "dd/MM/yyyy HH:mm"), new(3.61125), new(4.44875), new(2.1825, "dd/MM/yyyy"), new(2.5), new(1.57396),
                new(4.77541), new(2.77432),
            ],
            HeaderRows:
            [
                [new("Item"), new("Gate In Date"), new("Container No"), new("size"), new("status"), new("Unstuffing Date"),
                 new("Gate Out Date"), new("BL No."), new("Vessel/Voyage"), new("ETA"), new("Loading Port"), new("Agent"),
                 new("Consignee"), new("Remark")],
            ],
            Rows: rows,
            PageLabel: "Page No#",
            After: [summary]);
    }

    // ── TMS.Operation.InBound.DailyUnStuffing ──

    /// <summary>
    /// DAILY UNSTUFFING REPORT (Report.usp_InBound_DailyUnstuffingReport): Vector selected the CFS / CY-direct boxes by their
    /// unstuffing day, a block per agent + vessel + voyage. Gecko records no unstuffing yet (gap D9), so — as the owner ruled
    /// for the accounting unstuffing reports (2026-10-10) — the heading and the columns print with no lines.
    /// </summary>
    public static TabularReport DailyUnstuffing(ReportHeading h, DateOnly from, DateOnly to) => new(
        FileName: $"DailyUnstuffing_{from:yyyyMMdd}-{to:yyyyMMdd}",
        Page: ReportPage.A3Landscape,
        MarginCm: 2,
        Heading:
        [
            new(h.BranchName, 10, Bold: true), new("DAILY UNSTUFFING REPORT ", 10, Bold: true),
            new($"UNSTUFFING DATE FROM : {Day(from)}   TO : {Day(to)}", 10, Bold: true),
        ],
        HeadingRight: [new($"Print Date: {Stamp(h.PrintedAt, "dd/MM/yyyy HH:mm")}")],
        Preamble: [],
        PreambleRight: [],
        Columns:
        [
            new(2.40486), new(1.15062), new(1.12417), new(5.43688), new(2.57938), new(3.70007), new(2.46695), new(2.47355),
            new(4.28402), new(2.5),
        ],
        HeaderRows:
        [
            [new("Container No."), new("Size"), new("Type"), new("Consignee"), new("BOOKING MODE"), new("B/L No."), new("SEAL"),
             new("Change Status"), new("Condition"), new("Invoice No")],
        ],
        Rows: [],
        PageLabel: "Page No#");

    // ── TMS.Operation.InBound.ImportContainerList ──

    /// <summary>
    /// &lt;&lt; CONTAINER LIST REPORT &gt;&gt;, the desktop's "IMPORT Container List" (Report.usp_InBound_ImportContainerList): the IMPORT
    /// bookings whose vessel's ETA falls on the depot days asked for, a section per agent + vessel + voyage + ETA, each on its
    /// own page, with every booked box (a booking with no box prints its B/L alone): its full gate-in and gate-out, the truck
    /// of the gate-out else the gate-in, the gate-in's line and customs seals (else the declared seal), and the section's
    /// Summary by size / type with Total. Distinct boxes are counted, so the cells add up to the Total. UnStuffing prints
    /// blank: Gecko records no unstuffing yet (gap D9).
    /// </summary>
    public static async Task<TabularReport> ImportContainerListAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, DateOnly from, DateOnly to,
        string? agentCode, string? customerCode, string? vesselCode, string? voyageNo, CancellationToken ct)
    {
        var start = BranchClock.StartOf(h.Branch, from);
        var end = BranchClock.StartOf(h.Branch, to.AddDays(1));
        var bookings = db.Bookings.AsNoTracking()
            .Where(b => b.BranchId == h.Branch.BranchId && b.BookingTypeCode == "IMPORT" && b.Status != "CANCELLED" && b.DeletedAt == null);
        if (Clean(agentCode) is { } agent) bookings = bookings.Where(b => b.LinePartyCode == agent);
        if (Clean(customerCode) is { } customer) bookings = bookings.Where(b => b.CustomerPartyCode == customer);
        var lines = await (
                from b in bookings
                join c in db.VesselCalls on b.VesselCallId equals c.VesselCallId
                where c.Eta >= start && c.Eta < end
                join x in db.BookingContainers.Where(x => x.DeletedAt == null && (x.EndedAt == null || x.EndReason == "COMPLETED"))
                    on b.BookingId equals x.BookingId into boxes
                from x in boxes.DefaultIfEmpty()
                join r in db.EquipmentRequirements on x.EquipmentRequirementId equals r.EquipmentRequirementId into reqs
                from r in reqs.DefaultIfEmpty()
                select new
                {
                    b.BookingId, b.LinePartyCode, b.CarrierRef, b.CustomerRef, b.SubBlNo, b.OrderTypeCode, b.CustomerPartyCode, b.PodPortCode,
                    c.VesselCode, c.OperatorVoyageIn, c.OperatorVoyageOut, c.Eta,
                    BoxId = x == null ? (Guid?)null : x.BookingContainerId, ContainerNo = x == null ? null : x.ContainerNo,
                    Declared = x == null ? null : x.DeclaredSealNo, BoxRemarks = x == null ? null : x.Remarks,
                    Type = r == null ? null : r.EquipmentTypeCode,
                })
            .ToListAsync(ct);
        if (Clean(vesselCode) is { } vessel) lines = lines.Where(l => l.VesselCode == vessel).ToList();
        if (Clean(voyageNo) is { } voyage) lines = lines.Where(l => l.OperatorVoyageIn == voyage || l.OperatorVoyageOut == voyage).ToList();

        // The boxes' full moves, with their trucks and seals.
        var boxIds = lines.Select(l => l.BoxId).OfType<Guid>().Distinct().ToList();
        var full = new List<(Guid Box, Guid Move, string Direction, DateTimeOffset At, string? Type, string Plate, string? Haulier)>();
        foreach (var slice in boxIds.Chunk(2000))
            full.AddRange((await (
                    from g in db.GateTransactions.AsNoTracking()
                    where slice.Contains(g.BookingContainerId) && g.Status == "COMPLETED" && g.DeletedAt == null && g.FullEmpty == GateRules.Full
                    join v in db.TruckVisits on g.TruckVisitId equals v.TruckVisitId
                    select new { g.BookingContainerId, g.GateTransactionId, g.Direction, g.TransactionAt, g.EquipmentTypeCode, v.TruckPlate, v.HaulierPartyCode })
                .ToListAsync(ct))
                .Select(g => (g.BookingContainerId, g.GateTransactionId, g.Direction, g.TransactionAt, (string?)g.EquipmentTypeCode, g.TruckPlate, (string?)g.HaulierPartyCode)));
        var byBox = full.OrderBy(f => f.At).ToLookup(f => f.Box);
        var seals = await SealsAsync(db, full.Where(f => f.Direction == GateRules.In).Select(f => f.Move), ct);

        var types = await TypesAsync(master, lines.Select(l => l.Type).Concat(full.Select(f => f.Type)), ct);
        var parties = await master.PartiesAsync(lines.Select(l => l.CustomerPartyCode).Concat(full.Select(f => f.Haulier)).OfType<string>().Distinct(), ct);
        var vessels = await master.VesselsAsync(lines.Select(l => l.VesselCode).Distinct(), ct);
        string? Party(string? code) => code is null ? null : parties.GetValueOrDefault(code)?.Name;

        TabularColumn Col(double cm, string? format = null) => new(cm, format);
        IReadOnlyList<TabularColumn> columns =
        [
            Col(0.80698), Col(3.18386), Col(3.43927), Col(2.5), Col(1.29073), Col(2.07667), Col(2.23482, "dd-MM-yyyy"),
            Col(2.34125, "dd-MM-yyyy"), Col(2.10869, "dd-MM-yyyy"), Col(1.70625), Col(5.80729), Col(1.83854), Col(2.07667),
            Col(5.55034), Col(3.53188),
        ];
        IReadOnlyList<IReadOnlyList<HeaderCell>> headers =
        [
            [new("No."), new("Booking BLNo"), new("Sub B/L No."), new("Container No."), new("Size"), new("Status"), new("Gate In "),
             new("UnStuffing"), new("Gate Out"), new("Plate No."), new("Truck Company"), new("Seal Agent"), new("Seal Customs"),
             new("Consignee"), new("Remarks")],
        ];

        var blocks = new List<TabularBlock>();
        var sections = lines
            .GroupBy(l => (l.LinePartyCode, l.VesselCode, Voyage: Voyage("IMPORT", l.OperatorVoyageIn, l.OperatorVoyageOut), l.Eta))
            .OrderBy(s => s.Key.LinePartyCode, StringComparer.Ordinal).ThenBy(s => s.Key.Eta).ThenBy(s => s.Key.VesselCode, StringComparer.Ordinal);
        foreach (var section in sections)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rows = new List<TabularRow>();
            var counted = new List<(string? Size, string? Type, string Box)>();
            foreach (var l in section.OrderBy(l => BookingBlNo(l.CarrierRef, l.CustomerRef), StringComparer.Ordinal)
                         .ThenBy(l => l.SubBlNo, StringComparer.Ordinal).ThenBy(l => l.ContainerNo, StringComparer.Ordinal))
            {
                var moves = l.BoxId is { } id ? byBox[id].ToList() : [];
                var gateIn = moves.FirstOrDefault(m => m.Direction == GateRules.In);
                var gateOut = moves.FirstOrDefault(m => m.Direction == GateRules.Out);
                var truck = gateOut.Move != Guid.Empty ? gateOut : gateIn;
                var (size, type) = SizeType(types, gateIn.Type ?? l.Type);
                if (l.ContainerNo is { Length: > 0 } box && seen.Add(box)) counted.Add((size, type, box));
                string? Seal(Func<string, bool> kind) => gateIn.Move == Guid.Empty ? null
                    : seals[gateIn.Move].Where(s => kind(s.SealType)).Select(s => s.SealNo).FirstOrDefault();
                rows.Add(new TabularRow([
                    l.ContainerNo is { Length: > 0 } ? seen.Count : null, BookingBlNo(l.CarrierRef, l.CustomerRef), l.SubBlNo, l.ContainerNo,
                    size is null ? null : $"{size} {type}", l.OrderTypeCode,
                    gateIn.Move == Guid.Empty ? null : LocalDay(h.Branch, gateIn.At), null,
                    gateOut.Move == Guid.Empty ? null : LocalDay(h.Branch, gateOut.At),
                    truck.Move == Guid.Empty ? null : truck.Plate, truck.Move == Guid.Empty ? null : Party(truck.Haulier),
                    Seal(IsAgentSeal) ?? l.Declared, Seal(s => s == "CUSTOMS"), Party(l.CustomerPartyCode), l.BoxRemarks]));
            }
            var first = section.First();
            blocks.Add(new TabularBlock(null, columns, headers, rows, PageBreakBefore: true,
                Lines:
                [
                    new($"AGENT: {section.Key.LinePartyCode}          VOY: {section.Key.Voyage}          ETA: {Stamp(Local(h.Branch, section.Key.Eta), "dd/MM/yyyy")}"),
                    new($"VESSEL: {vessels.GetValueOrDefault(section.Key.VesselCode)?.VesselName ?? section.Key.VesselCode}          POD: {first.PodPortCode}"),
                ]));

            var sizes = counted.Select(c => (c.Size ?? "", c.Type ?? "")).Distinct()
                .OrderBy(s => s.Item1, StringComparer.Ordinal).ThenBy(s => s.Item2, StringComparer.Ordinal).ToList();
            blocks.Add(new TabularBlock("Summary",
                [new TabularColumn(1.25349 * 2.54), .. sizes.Select(_ => new TabularColumn(0.43518 * 2.54, null, CellAlign.Center)),
                    new TabularColumn(0.45794 * 2.54, null, CellAlign.Center)],
                [
                    [new HeaderCell("Container Size"), .. sizes.Select(s => new HeaderCell(s.Item1)), new HeaderCell("Total")],
                    [new HeaderCell(""), .. sizes.Select(s => new HeaderCell(s.Item2)), new HeaderCell("")],
                ],
                [new TabularRow(["TOTAL", .. sizes.Select(s => (object?)counted.Count(c => (c.Size ?? "") == s.Item1 && (c.Type ?? "") == s.Item2)), counted.Count])]));
        }

        return new TabularReport(
            FileName: $"ImportContainerList_{from:yyyyMMdd}-{to:yyyyMMdd}",
            Page: ReportPage.A3Landscape,
            MarginCm: 1.27,
            Heading:
            [
                new(h.BranchName, 10, Bold: true), new("<< CONTAINER LIST REPORT >>", 10, Bold: true),
                new($"Booking Date From : {Day(from)} To : {Day(to)}", 10, Bold: true),
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

    // ── TMS.Operation.InBound.MonthlyIncomingContainer ──

    private static readonly string[] MonthNames =
        ["JANUARY", "FEBUARY", "MARCH", "APRIL", "MAY", "JUNE", "JULY", "AUGUST", "SEPTEMBER", "OCTOBER", "NOVEMBER", "DECEMBER"];

    private static readonly double[] MonthlyWidths =
    [
        0.85958, 1.30938, 0.93014, 1.25646, 0.88604, 1.25646, 0.9125, 1.33583, 0.96132, 1.25646, 0.85958, 1.25799,
        0.83313, 1.30938, 0.85958, 1.25646, 0.85958, 1.28292, 0.99784, 1.25646, 0.85958, 1.28292, 0.89549, 1.25646,
        0.96542, 1.30938, 0.93076, 1.24354, 0.83313, 1.30938, 0.80667, 1.30938, 1.07125, 1.46813, 0.96542, 1.36229,
        0.96542, 1.36229, 0.9125, 1.33583, 0.93896, 1.38875, 0.85958, 1.33583, 0.9125, 1.36229, 0.96542, 1.33583,
        0.96542, 1.30938, 0.93896, 1.30937, 0.88604, 1.33583, 0.96542, 1.30937, 0.99187, 1.33583, 0.99187, 1.38875,
        0.83312, 1.30937, 0.93896, 1.36229, 0.93896, 1.25646, 0.93896, 1.23, 0.88604, 1.30937, 0.88604, 1.25646,
    ];

    /// <summary>
    /// &lt;&lt; Monthly Incoming Container Report &gt;&gt; (Report.usp_Inbound_MonthlyIncomingContainer): one year's moves of one kind
    /// (Vector's movement name, FULL IN by default), optionally of one booking type, as an AGENT × SERVICE (shipment type)
    /// matrix of 12 months × 20'/40'/45' × DRY/REEFER, with SUM CONT and TEU (20' = 1, other sizes 2), each agent's TOTAL,
    /// SUB.TOTAL, and the month's G.TOTAL and G.TEUS (printed in the first of the month's six cells; the RDL merged them).
    /// Reefer is master data's (Vector flagged only RF, so it counted RH as dry); SUB.TOTAL's FEB 45' DRY counts 45' dry boxes
    /// (the RDL's ignored the reefer flag). The 88 cm of columns print on one A3 sheet, scaled down; "FEBUARY" as the RDL.
    /// </summary>
    public static async Task<TabularReport> MonthlyIncomingAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, int year, string? movement, string? bookingType, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(movement) ? "FULL IN" : movement.Trim().Replace('_', ' ').ToUpperInvariant();
        var (direction, load) = Movement(name) ?? ("IN", "FULL");
        var query = Moves(db, h.Branch, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31)).Where(m => m.Direction == direction && m.FullEmpty == load);
        if (Clean(bookingType) is { } type) query = query.Where(m => m.BookingTypeCode == type);
        var moves = await query.ToListAsync(ct);
        var types = await TypesAsync(master, moves.Select(m => m.EquipmentTypeCode), ct);
        int Month(Move m) => Local(h.Branch, m.At).Month;
        string? Size(Move m) => SizeType(types, m.EquipmentTypeCode).Size;
        bool Reefer(Move m) => m.EquipmentTypeCode is { } c && types.GetValueOrDefault(c)?.IsReefer == true;
        int Teu(Move m) => Size(m) == "20" ? 1 : 2;
        string[] sizes = ["20", "40", "45"];
        object?[] Cells(IReadOnlyCollection<Move> set, bool zeros) =>
        [
            .. Enumerable.Range(1, 12).SelectMany(month => sizes.SelectMany(size => new[] { false, true }.Select(reefer =>
            {
                var n = set.Count(m => Month(m) == month && Size(m) == size && Reefer(m) == reefer);
                return zeros ? (object?)n.ToString(System.Globalization.CultureInfo.InvariantCulture) : n;
            }))),
            zeros ? set.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : set.Count, set.Sum(Teu),
        ];

        var rows = new List<TabularRow>();
        foreach (var agent in moves.GroupBy(m => m.LineCode).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var label = agent.Key;
            foreach (var service in agent.GroupBy(m => ShipmentType(m.OrderTypeCode)).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                rows.Add(new TabularRow([label, service.Key, .. Cells(service.ToList(), zeros: false)]));
                label = null;
            }
            rows.Add(new TabularRow([null, "TOTAL", .. Cells(agent.ToList(), zeros: false)], RowKind.Subtotal));
        }
        rows.Add(new TabularRow([]));
        rows.Add(new TabularRow(["SUB.TOTAL", null, .. Cells(moves, zeros: true)], RowKind.Subtotal));
        object?[] PerMonth(Func<IEnumerable<Move>, int> value) =>
            [.. Enumerable.Range(1, 12).SelectMany(month => new object?[] { value(moves.Where(m => Month(m) == month)), null, null, null, null, null })];
        rows.Add(new TabularRow(["G.TOTAL", null, .. PerMonth(set => set.Count()), moves.Count, null], RowKind.Total));
        rows.Add(new TabularRow(["G.TEUS", null, .. PerMonth(set => set.Sum(Teu)), null, moves.Sum(Teu)], RowKind.Total));

        const string blankZero = "0;(0);\"\"";
        return new TabularReport(
            FileName: $"MonthlyIncomingContainer_{year}",
            Page: ReportPage.A3Landscape,
            MarginCm: 1,
            Heading:
            [
                new(h.BranchName, 10, Bold: true), new("<< Monthly Incoming Container Report >>", 10, Bold: true),
                new($"Year:{year} Booking Type:{Clean(bookingType)} Movement Code:{name}", 10, Bold: true),
            ],
            HeadingRight: [new($"Print Date: {ListReport.Text(h.PrintedAt.DateTime, null)}", 10)],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(2.07667), new(2.21778),
                .. MonthlyWidths.Select(w => new TabularColumn(w, blankZero, CellAlign.Center)),
                new(1.57396, blankZero, CellAlign.Center), new(1.49458, null, CellAlign.Center),
            ],
            HeaderRows:
            [
                [new("AGENT", RowSpan: 3), new("SERVICE", RowSpan: 3), .. MonthNames.Select(m => new HeaderCell(m, ColSpan: 6)), new HeaderCell("SUM", ColSpan: 2)],
                [.. Enumerable.Range(0, 12).SelectMany(_ => sizes.Select(s => new HeaderCell(s + "'", ColSpan: 2))), new HeaderCell("", ColSpan: 2)],
                [.. Enumerable.Range(0, 36).SelectMany(_ => new[] { new HeaderCell("DRY"), new HeaderCell("REEFER") }), new HeaderCell("CONT"), new HeaderCell("TEU")],
            ],
            Rows: rows,
            PageLabel: "Page No#");
    }
}
