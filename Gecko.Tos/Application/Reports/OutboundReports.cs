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

    // ── TMS.Operation.OutBound.StockReport ──

    /// <summary>
    /// &lt;&lt; STOCK REPORT &gt;&gt; (Report.usp_OutBound_StockReport) — despite its name, the empty releases to EXPORT bookings on the
    /// depot days asked for, by container no, with the booked box's empty in, full in and full out around the release, the
    /// booking's FROM location, shipper and booking no. SEAL NO. is the booked box's declared seal, else the latest line /
    /// agent seal recorded at the gate on that box (Vector printed its container master's seal, which Gecko does not keep);
    /// REPO prints blank, as in the RDL.
    /// </summary>
    public static async Task<TabularReport> StockReportAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, DateOnly from, DateOnly to, string? agentCode, CancellationToken ct)
    {
        var query = InboundReports.Moves(db, h.Branch, from, to)
            .Where(m => m.Direction == GateRules.Out && m.FullEmpty == GateRules.Empty && m.BookingTypeCode == "EXPORT");
        if (Clean(agentCode) is { } agent) query = query.Where(m => m.LineCode == agent);
        var moves = (await query.ToListAsync(ct)).OrderBy(m => m.ContainerNo, StringComparer.Ordinal).ThenBy(m => m.At).ToList();
        var types = await TypesAsync(master, moves.Select(m => m.EquipmentTypeCode), ct);
        var parties = await master.PartiesAsync(moves.Select(m => m.CustomerCode).OfType<string>().Distinct(), ct);

        // The booked boxes' own moves (their empty in, full in / out) and seals.
        var boxes = moves.Select(m => m.BookingContainerId).Distinct().ToList();
        var declared = new Dictionary<Guid, string?>();
        var others = new List<(Guid Box, Guid Move, string Direction, string FullEmpty, DateTimeOffset At)>();
        foreach (var slice in boxes.Chunk(2000))
        {
            foreach (var x in await db.BookingContainers.AsNoTracking().Where(b => slice.Contains(b.BookingContainerId))
                         .Select(b => new { b.BookingContainerId, b.DeclaredSealNo }).ToListAsync(ct))
                declared[x.BookingContainerId] = x.DeclaredSealNo;
            others.AddRange((await db.GateTransactions.AsNoTracking()
                    .Where(g => slice.Contains(g.BookingContainerId) && g.Status == "COMPLETED" && g.DeletedAt == null)
                    .Select(g => new { g.BookingContainerId, g.GateTransactionId, g.Direction, g.FullEmpty, g.TransactionAt }).ToListAsync(ct))
                .Select(g => (g.BookingContainerId, g.GateTransactionId, g.Direction, g.FullEmpty, g.TransactionAt)));
        }
        var byBox = others.ToLookup(o => o.Box);
        var seals = new List<(Guid Move, DateTimeOffset At, string SealNo, string SealType)>();
        foreach (var slice in others.Select(o => o.Move).Distinct().Chunk(2000))
            seals.AddRange((await db.GateTransactionSeals.AsNoTracking().Where(s => slice.Contains(s.GateTransactionId) && s.DeletedAt == null)
                    .Select(s => new { s.GateTransactionId, s.CreatedAt, s.SealNo, s.SealType }).ToListAsync(ct))
                .Select(s => (s.GateTransactionId, s.CreatedAt, s.SealNo, s.SealType)));
        var sealsByMove = seals.Where(s => IsAgentSeal(s.SealType)).ToLookup(s => s.Move);
        string? Seal(Guid box) => declared.GetValueOrDefault(box)
            ?? byBox[box].SelectMany(o => sealsByMove[o.Move]).OrderByDescending(s => s.At).Select(s => s.SealNo).FirstOrDefault();
        DateTime? When(Guid box, string direction, string load, bool first)
        {
            var set = byBox[box].Where(o => o.Direction == direction && o.FullEmpty == load).Select(o => o.At).ToList();
            return set.Count == 0 ? null : Local(h.Branch, first ? set.Min() : set.Max());
        }

        var rows = moves.Select(m =>
        {
            var (size, type) = SizeType(types, m.EquipmentTypeCode);
            var (emptyIn, emptyOut) = (When(m.BookingContainerId, GateRules.In, GateRules.Empty, true), Local(h.Branch, m.At));
            var (fullIn, fullOut) = (When(m.BookingContainerId, GateRules.In, GateRules.Full, true), When(m.BookingContainerId, GateRules.Out, GateRules.Full, false));
            return new TabularRow([m.ContainerNo, Seal(m.BookingContainerId), $"{size}{type}", emptyIn, emptyIn, emptyOut, emptyOut,
                m.NextPrevLocation, m.CustomerCode is { } c ? parties.GetValueOrDefault(c)?.Name : null, fullIn, fullIn, fullOut, fullOut,
                m.BookingBlNo, null]);
        }).ToList();

        TabularColumn Col(double mm, string? format = null, CellAlign align = CellAlign.Left) => new(mm / 10, format, align);
        TabularColumn Date(double mm) => Col(mm, "dd/MM/yyyy", CellAlign.Center);
        TabularColumn Time() => Col(12.29311, "H:mm", CellAlign.Center);
        return new TabularReport(
            FileName: $"StockReport_{from:yyyyMMdd}-{to:yyyyMMdd}",
            Page: ReportPage.A3Landscape,
            MarginCm: 2,
            Heading:
            [
                new(h.BranchName, 10, Bold: true), new("<< STOCK REPORT >>", 10, Bold: true),
                new($"ACTIVITIES DATE: {Day(from)} TO: {Day(to)}"), new($"Agent: {Clean(agentCode)}"),
            ],
            HeadingRight: [new($"Print Date: {h.PrintedAt:dd/MM/yyyy}")],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                Col(26.86861), Col(22.14781), Col(10.56608, null, CellAlign.Center), Date(23.16795), Time(), Date(23.14791), Time(),
                Col(19.97292), Col(31.53963), Date(20.50208), Time(), Date(21.47223), Time(), Col(26.05834), Col(13.62292),
            ],
            HeaderRows:
            [
                [new("CONTAINER NO.", RowSpan: 2), new("SEAL NO.", RowSpan: 2), new("SIZE", RowSpan: 2), new("EMPTY IN", ColSpan: 2),
                 new("EMPTY OUT", ColSpan: 2), new("FROM", RowSpan: 2), new("SHIPPER", RowSpan: 2), new("FULL IN", ColSpan: 2),
                 new("FULL OUT", ColSpan: 2), new("BOOKING NO.", RowSpan: 2), new("REPO", RowSpan: 2)],
                [new("DATE"), new("TIME"), new("DATE"), new("TIME"), new("DATE"), new("TIME"), new("DATE"), new("TIME")],
            ],
            Rows: rows,
            PageLabel: "Page No#");
    }

    // ── TMS.Operation.OutBound.StuffingUnstuffing ──

    /// <summary>
    /// The desktop's "Stuffing - UnStuffing" (Report.usp_OubBound_ContainerInOut): a line per full gate-in of a booked box on
    /// the depot days asked for, numbered in container order, with the shipment type as Status (Vector's per order type), the
    /// booked box's empty / full in and out, its agent seal (declared, else the one on this gate-in), the stuff/unstuff date
    /// (its first full in), vessel/voyage, ETA, loading port, the customer and, as Remarks, this gate-in's remark (Vector's
    /// CY-direct remark, which Gecko does not keep). The summary prints its Total row only, as the RDL hid the agent rows;
    /// its columns run by size then type.
    /// </summary>
    public static async Task<TabularReport> StuffingUnstuffingAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, DateOnly from, DateOnly to, StuffingFilter f, CancellationToken ct)
    {
        var query = InboundReports.Moves(db, h.Branch, from, to).Where(m => m.Direction == GateRules.In && m.FullEmpty == GateRules.Full);
        if (Clean(f.AgentCode) is { } agent) query = query.Where(m => m.LineCode == agent);
        if (Clean(f.CustomerCode) is { } customer) query = query.Where(m => m.CustomerCode == customer);
        if (f.BookingBlNo?.Trim() is { Length: > 0 } bl) query = query.Where(m => m.CarrierRef == bl || m.CustomerRef == bl);
        if (Clean(f.VesselCode) is { } vessel) query = query.Where(m => m.VesselCode == vessel);
        if (Clean(f.VoyageNo) is { } voyage) query = query.Where(m => m.VoyageIn == voyage || m.VoyageOut == voyage);
        if (Clean(f.BookingType) is { } type) query = query.Where(m => m.BookingTypeCode == type);
        if (Clean(f.OrderType) is { } order) query = query.Where(m => m.OrderTypeCode == order);
        var moves = (await query.ToListAsync(ct)).OrderBy(m => m.ContainerNo, StringComparer.Ordinal).ThenBy(m => m.At).ToList();
        var types = await TypesAsync(master, moves.Select(m => m.EquipmentTypeCode), ct);
        var parties = await master.PartiesAsync(moves.Select(m => m.CustomerCode).OfType<string>().Distinct(), ct);
        var vessels = await master.VesselsAsync(moves.Select(m => m.VesselCode).OfType<string>().Distinct(), ct);

        var boxes = moves.Select(m => m.BookingContainerId).Distinct().ToList();
        var declared = new Dictionary<Guid, string?>();
        var others = new List<(Guid Box, string Direction, string FullEmpty, DateTimeOffset At)>();
        foreach (var slice in boxes.Chunk(2000))
        {
            foreach (var x in await db.BookingContainers.AsNoTracking().Where(b => slice.Contains(b.BookingContainerId))
                         .Select(b => new { b.BookingContainerId, b.DeclaredSealNo }).ToListAsync(ct))
                declared[x.BookingContainerId] = x.DeclaredSealNo;
            others.AddRange((await db.GateTransactions.AsNoTracking()
                    .Where(g => slice.Contains(g.BookingContainerId) && g.Status == "COMPLETED" && g.DeletedAt == null)
                    .Select(g => new { g.BookingContainerId, g.Direction, g.FullEmpty, g.TransactionAt }).ToListAsync(ct))
                .Select(g => (g.BookingContainerId, g.Direction, g.FullEmpty, g.TransactionAt)));
        }
        var byBox = others.ToLookup(o => o.Box);
        DateOnly? When(Guid box, string direction, string load, bool first)
        {
            var set = byBox[box].Where(o => o.Direction == direction && o.FullEmpty == load).Select(o => o.At).ToList();
            return set.Count == 0 ? null : LocalDay(h.Branch, first ? set.Min() : set.Max());
        }
        var seals = new List<(Guid Move, DateTimeOffset At, string SealNo, string SealType)>();
        foreach (var slice in moves.Select(m => m.GateTransactionId).Chunk(2000))
            seals.AddRange((await db.GateTransactionSeals.AsNoTracking().Where(s => slice.Contains(s.GateTransactionId) && s.DeletedAt == null)
                    .Select(s => new { s.GateTransactionId, s.CreatedAt, s.SealNo, s.SealType }).ToListAsync(ct))
                .Select(s => (s.GateTransactionId, s.CreatedAt, s.SealNo, s.SealType)));
        var sealsByMove = seals.Where(s => IsAgentSeal(s.SealType)).OrderBy(s => s.At).ToLookup(s => s.Move, s => s.SealNo);
        string SizeOf(InboundReports.Move m)
        {
            var (size, kind) = SizeType(types, m.EquipmentTypeCode);
            return $"{size}{kind}";
        }

        var rows = moves.Select((m, i) =>
        {
            var party = m.CustomerCode is { } c ? parties.GetValueOrDefault(c) : null;
            var vesselName = m.VesselCode is { } v ? vessels.GetValueOrDefault(v)?.VesselName ?? v : "";
            var fullIn = When(m.BookingContainerId, GateRules.In, GateRules.Full, true);
            return new TabularRow([i + 1, m.ContainerNo, SizeOf(m), ShipmentType(m.OrderTypeCode),
                When(m.BookingContainerId, GateRules.In, GateRules.Empty, true), When(m.BookingContainerId, GateRules.Out, GateRules.Empty, false),
                fullIn, When(m.BookingContainerId, GateRules.Out, GateRules.Full, false),
                declared.GetValueOrDefault(m.BookingContainerId) ?? sealsByMove[m.GateTransactionId].FirstOrDefault(), m.BookingBlNo,
                Day(fullIn, "yyyy-MM-dd"), $"{vesselName}/{m.Voyage}", Day(LocalDay(h.Branch, m.Eta), "yyyy-MM-dd"), m.PolPortCode,
                party is { IsCustomer: true } ? party.Name : "", m.Remarks]);
        }).ToList();

        var sizes = moves.Select(SizeOf).Distinct().Select(s => (Label: s, Order: SizeType(types, moves.First(m => SizeOf(m) == s).EquipmentTypeCode)))
            .OrderBy(s => s.Order.Size, StringComparer.Ordinal).ThenBy(s => s.Order.Type, StringComparer.Ordinal).Select(s => s.Label).ToList();
        var summary = new TabularBlock("Summary",
            [new TabularColumn(2.5), .. sizes.Select(_ => new TabularColumn(1.653, null, CellAlign.Center)), new TabularColumn(2.5, null, CellAlign.Center)],
            [[new HeaderCell("Container"), .. sizes.Select(s => new HeaderCell(s)), new HeaderCell("Total")]],
            [new TabularRow(["Total", .. sizes.Select(s => (object?)moves.Count(m => SizeOf(m) == s)), moves.Count], RowKind.Total)]);

        TabularColumn Col(double mm, string? format = null, CellAlign align = CellAlign.Left) => new(mm / 10, format, align);
        TabularColumn Date(double mm) => Col(mm, "dd-MM-yyyy", CellAlign.Center);
        return new TabularReport(
            FileName: $"StuffingUnstuffing_{from:yyyyMMdd}-{to:yyyyMMdd}",
            Page: ReportPage.A3Landscape,
            MarginCm: 1.27,
            Heading:
            [
                new(h.BranchName, 10, Bold: true),
                new(Clean(f.BookingType) == "IMPORT" ? "เปลียนชื่อ (IMP UNSTUFFING)" : "(STUFFING EXP CY/CY-IN  และ CY-IN (NON-NOMINATING)", 10, Bold: true),
                new($"Gate In date from: {Day(from, "dd/MM/yyyy")} To:{Day(to, "dd/MM/yyyy")}"), new($"Agent: {Clean(f.AgentCode)}"),
            ],
            HeadingRight: [new($"Printed By: {h.PrintedBy}"), new($"Printed On: {h.PrintedAt:dd/MM/yyyy HH.mm}")],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                Col(11.72987, null, CellAlign.Center), Col(25), Col(11.41805, null, CellAlign.Center), Col(17.32708, null, CellAlign.Center),
                Date(22.35417), Date(20.76667), Date(22.35417), Date(22.88333), Col(25.4), Col(32.40833),
                Col(25.52917, null, CellAlign.Center), Col(35.05417), Col(25.26458, null, CellAlign.Center), Col(25), Col(37.85125), Col(30.04473),
            ],
            HeaderRows:
            [
                [new("Item", RowSpan: 2), new("Container No", RowSpan: 2), new("Size", RowSpan: 2), new("Status", RowSpan: 2),
                 new("Empty", ColSpan: 2), new("Full", ColSpan: 2), new("Agent Seal", RowSpan: 2), new("Booking BLNo", RowSpan: 2),
                 new("Stuff/UnStuff Date", RowSpan: 2), new("Vessel/Voyage", RowSpan: 2), new("ETA/ETD", RowSpan: 2),
                 new("Loading Port", RowSpan: 2), new("Shipper Consignee", RowSpan: 2), new("Remarks", RowSpan: 2)],
                [new("In Date"), new("Out Date"), new("In Date"), new("Out Date")],
            ],
            Rows: rows,
            PageLabel: "Page No#",
            After: [summary]);
    }
}

/// <summary>Stuffing - UnStuffing's filters, Vector's (BookingBLNo matches the carrier or customer reference).</summary>
internal sealed record StuffingFilter(
    string? AgentCode, string? CustomerCode, string? BookingBlNo, string? VesselCode, string? VoyageNo, string? BookingType, string? OrderType);
