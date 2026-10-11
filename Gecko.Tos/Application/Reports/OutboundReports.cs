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
        HeadingRight: [new($"Printed By: {h.PrintedBy}"), new($"Printed On: {Stamp(h.PrintedAt, "dd/MM/yyyy HH:mm")}")],
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
            HeadingRight: [new($"Print Date: {Stamp(h.PrintedAt, "dd-MM-yyyy HH:mm")}")],
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
            HeadingRight: [new($"Printed By: {h.PrintedBy}"), new($"Print Date: {Stamp(h.PrintedAt, "dd-MM-yyyy HH:mm")}")],
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
            HeadingRight: [new($"Print Date: {Stamp(h.PrintedAt, "dd/MM/yyyy")}")],
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
            HeadingRight: [new($"Printed By: {h.PrintedBy}"), new($"Printed On: {Stamp(h.PrintedAt, "dd/MM/yyyy HH.mm")}")],
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

    // ── TMS.Operation.OutBound.ShorePassECT ──

    /// <summary>
    /// &lt;&lt;SHORE PASS REPORT&gt;&gt;, the desktop's "Shore Pass ECT" (Report.usp_OutBound_ShorePassECT): unlike Shore Pass 1-3,
    /// the booked boxes REQUIRED on the days asked for (a box with no required date is not listed, as on Vector), by booking
    /// no then container, with the line, shipper, feeder (vessel + voyage), the cargo weight of the box's latest full
    /// gate-in (0.00 before it comes in) and the destination port (final, else discharge). One row per box (Vector repeated
    /// a box with two full gate-ins). DEPOT prints the loading port asked for, as the RDL did.
    /// </summary>
    public static async Task<TabularReport> ShorePassEctAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, DateOnly from, DateOnly to,
        string? agentCode, string? bookingType, string? loadingPort, CancellationToken ct)
    {
        var bookings = db.Bookings.AsNoTracking().Where(b => b.BranchId == h.Branch.BranchId && b.Status != "CANCELLED" && b.DeletedAt == null);
        if (Clean(agentCode) is { } agent) bookings = bookings.Where(b => b.LinePartyCode == agent);
        if (Clean(bookingType) is { } type) bookings = bookings.Where(b => b.BookingTypeCode == type);
        if (Clean(loadingPort) is { } pol) bookings = bookings.Where(b => b.PolPortCode == pol);
        var (first, last) = (from, to);
        var boxes = await (
                from b in bookings
                join x in db.BookingContainers on b.BookingId equals x.BookingId
                join r in db.EquipmentRequirements on x.EquipmentRequirementId equals r.EquipmentRequirementId
                join c in db.VesselCalls on b.VesselCallId equals c.VesselCallId into calls
                from c in calls.DefaultIfEmpty()
                where x.DeletedAt == null && x.ContainerNo != null && x.ContainerNo != "" && x.RequiredDate >= first && x.RequiredDate <= last
                select new
                {
                    x.BookingContainerId, ContainerNo = x.ContainerNo!, r.EquipmentTypeCode, b.LinePartyCode, b.CustomerPartyCode,
                    b.CarrierRef, b.CustomerRef, b.DirectionCode, b.FpdPortCode, b.PodPortCode,
                    VesselCode = c == null ? null : c.VesselCode, VoyageIn = c == null ? null : c.OperatorVoyageIn, VoyageOut = c == null ? null : c.OperatorVoyageOut,
                })
            .ToListAsync(ct);

        var weights = new Dictionary<Guid, decimal?>();
        foreach (var slice in boxes.Select(b => b.BookingContainerId).Chunk(2000))
            foreach (var g in (await db.GateTransactions.AsNoTracking()
                         .Where(g => slice.Contains(g.BookingContainerId) && g.Status == "COMPLETED" && g.DeletedAt == null
                                     && g.Direction == GateRules.In && g.FullEmpty == GateRules.Full)
                         .Select(g => new { g.BookingContainerId, g.TransactionAt, g.CargoWeightKg }).ToListAsync(ct))
                     .OrderBy(g => g.TransactionAt))
                weights[g.BookingContainerId] = g.CargoWeightKg;
        var types = await TypesAsync(master, boxes.Select(b => (string?)b.EquipmentTypeCode), ct);
        var parties = await master.PartiesAsync(boxes.Select(b => b.LinePartyCode).Concat(boxes.Select(b => b.CustomerPartyCode)).OfType<string>().Distinct(), ct);
        var vessels = await master.VesselsAsync(boxes.Select(b => b.VesselCode).OfType<string>().Distinct(), ct);
        var ports = await master.PortsAsync(boxes.Select(b => b.FpdPortCode ?? b.PodPortCode).OfType<string>().Distinct(), ct);

        var rows = boxes
            .OrderBy(b => BookingBlNo(b.CarrierRef, b.CustomerRef), StringComparer.Ordinal).ThenBy(b => b.ContainerNo, StringComparer.Ordinal)
            .Select((b, i) =>
            {
                var (size, kind) = SizeType(types, b.EquipmentTypeCode);
                var vesselName = b.VesselCode is { } v ? vessels.GetValueOrDefault(v)?.VesselName ?? v : "";
                var port = b.FpdPortCode ?? b.PodPortCode;
                return new TabularRow([i + 1, b.ContainerNo, $"{size}{kind}", parties.GetValueOrDefault(b.LinePartyCode)?.Name,
                    b.CustomerPartyCode is { } c ? parties.GetValueOrDefault(c)?.Name : null,
                    $"{vesselName} {Voyage(b.DirectionCode ?? "", b.VoyageIn, b.VoyageOut)}", BookingBlNo(b.CarrierRef, b.CustomerRef),
                    weights.GetValueOrDefault(b.BookingContainerId) ?? 0m, port is null ? null : ports.GetValueOrDefault(port)?.Name ?? port]);
            }).ToList();

        return new TabularReport(
            FileName: $"ShorePassECT_{from:yyyyMMdd}-{to:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 0.5,
            Heading: [new(h.BranchName, 10, Bold: true), new("<<SHORE PASS REPORT>>", 10, Bold: true)],
            HeadingRight: [],
            Preamble: [new($"DATE  {Stamp(h.PrintedAt, "dd/MM/yyyy")}"), new($"DEPOT : {loadingPort?.Trim()}")],
            PreambleRight: [],
            Columns: [new(1.40639), new(2.35007), new(1.21896), new(4.21097), new(5.18993), new(6.53931), new(2.62347), new(1.88264, "0.00"), new(2.63906)],
            HeaderRows:
            [
                [new("No."), new("CONTAINER NO."), new("SIZE"), new("AGENT"), new("SHIPPER"), new("FEEDER"), new("BOOKING NO."), new("WEIGHT"), new("PORT")],
            ],
            Rows: rows,
            PageLabel: "Page No #");
    }

    // ── TMS.Operation.OutBound.TruckControl ──

    /// <summary>
    /// &lt;&lt; Truck Control Report &gt;&gt; (Report.usp_OutBound_TruckControl): the EXPORT vessel calls whose ETD falls on the depot
    /// days asked for (the screen's "ETD"; Vector filtered the schedule's ETA), a row per loading port + call + line: the
    /// line's dry and reefer CFS / CY / port closings, terminal, ETD, QTY (boxes gated in full) and the full gate-ins and
    /// gate-outs by 20' / 40' / 45' dry and 20'RF / 40'RH / 45'RH; each loading port's Total and the GrandTotal. Owner
    /// 2026-10-11: the reefer columns count every reefer of the size, on rows and totals alike (the RDL's rows counted only RF
    /// at 20' and RH at 40'), so the totals add up. Completed moves only; the gated box's own type. Terminal prints the code.
    /// </summary>
    public static async Task<TabularReport> TruckControlAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, DateOnly from, DateOnly to,
        string? agentCode, string? vesselCode, string? voyageNo, CancellationToken ct)
    {
        var start = BranchClock.StartOf(h.Branch, from);
        var end = BranchClock.StartOf(h.Branch, to.AddDays(1));
        var query =
            from b in db.Bookings.AsNoTracking()
            where b.BranchId == h.Branch.BranchId && b.BookingTypeCode == "EXPORT" && b.Status != "CANCELLED" && b.DeletedAt == null
            join c in db.VesselCalls on b.VesselCallId equals c.VesselCallId
            where !c.IsCancelled && c.DeletedAt == null && c.Etd >= start && c.Etd < end
            join l in db.VesselCallLines on b.VesselCallLineId equals l.VesselCallLineId into lines
            from l in lines.DefaultIfEmpty()
            select new
            {
                b.BookingId, b.PolPortCode, b.LinePartyCode, b.LinePartyId, c.VesselCallId, c.VesselCode, c.TerminalCode, c.Etd, c.Remarks,
                Voyage = l != null && l.VoyageOut != null ? l.VoyageOut : c.OperatorVoyageOut ?? c.OperatorVoyageIn,
            };
        if (Clean(agentCode) is { } agent) query = query.Where(x => x.LinePartyCode == agent);
        if (Clean(vesselCode) is { } vessel) query = query.Where(x => x.VesselCode == vessel);
        if (Clean(voyageNo) is { } voyage) query = query.Where(x => x.Voyage == voyage);
        var bookings = await query.ToListAsync(ct);

        var ids = bookings.Select(b => b.BookingId).ToList();
        var moves = new List<(Guid Booking, string Direction, string? Type)>();
        foreach (var slice in ids.Chunk(2000))
            moves.AddRange((await db.GateTransactions.AsNoTracking()
                    .Where(g => slice.Contains(g.BookingId) && g.Status == "COMPLETED" && g.DeletedAt == null && g.FullEmpty == GateRules.Full)
                    .Select(g => new { g.BookingId, g.Direction, g.EquipmentTypeCode }).ToListAsync(ct))
                .Select(g => (g.BookingId, g.Direction, (string?)g.EquipmentTypeCode)));
        var byBooking = moves.ToLookup(m => m.Booking);
        var types = await TypesAsync(master, moves.Select(m => m.Type), ct);
        var vessels = await master.VesselsAsync(bookings.Select(b => b.VesselCode).Distinct(), ct);

        (string Size, bool Reefer)[] kinds = [("20", false), ("40", false), ("45", false), ("20", true), ("40", true), ("45", true)];
        bool Is((string Size, bool Reefer) kind, string? type) =>
            SizeType(types, type).Size == kind.Size && (type is { } c && types.GetValueOrDefault(c)?.IsReefer == true) == kind.Reefer;
        object?[] Counts(IReadOnlyCollection<(Guid Booking, string Direction, string? Type)> set)
        {
            var cells = new List<object?> { set.Count(m => m.Direction == GateRules.In) };
            foreach (var direction in new[] { GateRules.In, GateRules.Out })
            {
                var counts = kinds.Select(k => set.Count(m => m.Direction == direction && Is(k, m.Type))).ToList();
                cells.AddRange(counts.Cast<object?>());
                cells.Add(counts.Sum());
            }
            return [.. cells];
        }

        var rows = new List<TabularRow>();
        foreach (var port in bookings.GroupBy(b => b.PolPortCode ?? "").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var label = port.Key;
            var groups = port.GroupBy(b => (b.VesselCallId, b.LinePartyCode, b.LinePartyId))
                .OrderBy(g => g.First().Etd).ThenBy(g => vessels.GetValueOrDefault(g.First().VesselCode)?.VesselName, StringComparer.Ordinal)
                .ThenBy(g => g.Key.LinePartyCode, StringComparer.Ordinal);
            foreach (var call in groups)
            {
                var first = call.First();
                var cutoffs = await CutoffLookup.EffectiveAsync(db, first.VesselCallId, first.LinePartyId, h.Branch.BranchId, ct);
                object?[] Closing(string kind) =>
                    cutoffs.FirstOrDefault(c => c.Kind == kind) is { } c ? [Local(h.Branch, c.At), Local(h.Branch, c.At)] : [null, null];
                rows.Add(new TabularRow([label, call.Key.LinePartyCode, vessels.GetValueOrDefault(first.VesselCode)?.VesselName ?? first.VesselCode,
                    first.Voyage, .. Closing("CFS_DRY"), .. Closing("YARD_DRY"), .. Closing("PORT_DRY"),
                    .. Closing("CFS_REEFER"), .. Closing("YARD_REEFER"), .. Closing("PORT_REEFER"),
                    first.TerminalCode, Local(h.Branch, first.Etd), .. Counts(call.SelectMany(b => byBooking[b.BookingId]).ToList()), first.Remarks]));
                label = null;
            }
            rows.Add(new TabularRow([null, "Total", null, null, .. Enumerable.Repeat<object?>(null, 14),
                .. Counts(port.SelectMany(b => byBooking[b.BookingId]).ToList()), null], RowKind.Subtotal));
            rows.Add(new TabularRow([]));
        }
        rows.Add(new TabularRow(["GrandTotal", null, null, null, .. Enumerable.Repeat<object?>(null, 14), .. Counts(moves), null], RowKind.Total));

        double[] mm = [25, 24.91179, 13.36344, 17.29181, 9.38959, 17.98251, 11.59165, 18.08278, 9.38959, 18.43264, 10.2271, 17.50347, 9.38959,
            18.65, 9.34862, 20.49113, 24.64111, 11.77083, 10.7125, 10.7125, 10.7125, 10.7125, 13.40569, 11.46513, 10.97708, 11.24167, 10.7125,
            10.7125, 10.7125, 10.97708, 10.97708, 11.50625, 11.50625, 68.39167];
        // The closing columns alternate date and time (from the 5th); ETD prints date and time; the counts are centred.
        // (The spec's width list lacked QTY's; it takes the count columns' 10.7mm.)
        var columns = mm.Select((w, i) => new TabularColumn(w / 10,
            i is >= 4 and <= 15 ? (i % 2 == 0 ? "dd/MM/yyyy" : "HH:mm") : i == 17 ? "dd/MM/yyyy HH:mm" : null,
            i is 0 or 1 or 2 or 33 ? CellAlign.Left : CellAlign.Center)).ToList();
        string[] closings = ["CFS CLOSING", "CY CLOSING", "LCB/CLOSING TIME"];
        string[] counted = ["20'", "40'", "45'", "20'RF", "40'RH", "45'RH", "Total"];
        return new TabularReport(
            FileName: $"TruckControl_{from:yyyyMMdd}-{to:yyyyMMdd}",
            Page: ReportPage.A3Landscape,
            MarginCm: 0.5,
            Heading:
            [
                new(h.BranchName, 10, Bold: true), new("<< Truck Control Report >>", 10, Bold: true),
                new($"From ETD: {Day(from, "dd/MM/yyyy")}   To ETD: {Day(to, "dd/MM/yyyy")}"),
            ],
            HeadingRight: [new($"Printed By: {h.PrintedBy}"), new($"Printed On: {Stamp(h.PrintedAt, "dd/MM/yyyy")}")],
            Preamble: [],
            PreambleRight: [],
            Columns: columns,
            HeaderRows:
            [
                [new("Loading Port", RowSpan: 3), new("AGENT", RowSpan: 3), new("VESSEL NAME ", RowSpan: 3), new(" VOY", RowSpan: 3),
                 new("Dry", ColSpan: 6), new("Reefer", ColSpan: 6), new("Terminal", RowSpan: 3), new("ETD DATE", RowSpan: 3), new("QTY", RowSpan: 3),
                 new("Full Gate In", ColSpan: 7), new("Full Gate Out", ColSpan: 7), new("Remarks", RowSpan: 3)],
                [.. closings.Concat(closings).Select(c => new HeaderCell(c, ColSpan: 2)),
                 .. counted.Select(c => new HeaderCell(c, RowSpan: 2)), .. counted.Select(c => new HeaderCell(c, RowSpan: 2))],
                [.. Enumerable.Range(0, 6).SelectMany(_ => new[] { new HeaderCell("DATE"), new HeaderCell("TIME") })],
            ],
            Rows: rows,
            PageLabel: "Page No#");
    }

    // ── TMS.Operation.OutBound.CYContainerMonthlyMovement ──

    /// <summary>
    /// CY CONTAINERS MOVEMENT (PORT) &lt;&lt; Monthly Report &gt;&gt; (Report.usp_OutBound_CYContainerMonthlyMovement): the EXPORT
    /// bookings' box places due on the depot days asked for, counted by SHIPPER and by AGENT under the destination port's
    /// region (gecko_master 33), DRY / REEFER and size, with Total column and row. Owner 2026-10-11: a place's day is its
    /// box's required date, else the vessel's ETD, else the booking's valid-from (Gecko rarely holds a required date); places
    /// with no box yet count too, as Vector counted every slot. A booking with no destination port or customer is out, as
    /// on Vector; a port without a region counts under NONE.
    /// </summary>
    public static async Task<TabularReport> CyMonthlyMovementAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, DateOnly from, DateOnly to, string? agentCode, CancellationToken ct)
    {
        var (first, last) = (from, to);
        var (start, end) = (BranchClock.StartOf(h.Branch, from), BranchClock.StartOf(h.Branch, to.AddDays(1)));
        var bookings = db.Bookings.AsNoTracking().Where(b => b.BranchId == h.Branch.BranchId && b.BookingTypeCode == "EXPORT"
            && b.Status != "CANCELLED" && b.DeletedAt == null && b.CustomerPartyCode != null && (b.FpdPortCode != null || b.PodPortCode != null));
        if (Clean(agentCode) is { } agent) bookings = bookings.Where(b => b.LinePartyCode == agent);
        var candidates = await (
                from b in bookings
                join c in db.VesselCalls on b.VesselCallId equals c.VesselCallId into calls
                from c in calls.DefaultIfEmpty()
                where (c != null && c.Etd >= start && c.Etd < end) || (b.ValidFrom >= first && b.ValidFrom <= last)
                      || db.BookingContainers.Any(x => x.BookingId == b.BookingId && x.RequiredDate >= first && x.RequiredDate <= last)
                select new
                {
                    b.BookingId, b.LinePartyCode, b.CustomerPartyCode, Port = b.FpdPortCode ?? b.PodPortCode, b.ValidFrom,
                    Etd = c == null ? (DateTimeOffset?)null : c.Etd,
                })
            .ToListAsync(ct);
        var ids = candidates.Select(b => b.BookingId).ToList();
        var requirements = new List<(Guid Booking, Guid Line, string Type, short Qty)>();
        var boxes = new List<(Guid Line, DateOnly? Required)>();
        foreach (var slice in ids.Chunk(2000))
        {
            requirements.AddRange((await db.EquipmentRequirements.AsNoTracking().Where(r => slice.Contains(r.BookingId))
                    .Select(r => new { r.BookingId, r.EquipmentRequirementId, r.EquipmentTypeCode, r.Qty }).ToListAsync(ct))
                .Select(r => (r.BookingId, r.EquipmentRequirementId, r.EquipmentTypeCode, r.Qty)));
            boxes.AddRange((await db.BookingContainers.AsNoTracking()
                    .Where(x => slice.Contains(x.BookingId) && x.DeletedAt == null && (x.EndedAt == null || x.EndReason == "COMPLETED"))
                    .Select(x => new { x.EquipmentRequirementId, x.RequiredDate }).ToListAsync(ct))
                .Select(x => (x.EquipmentRequirementId, x.RequiredDate)));
        }
        var boxesByLine = boxes.ToLookup(x => x.Line);
        var byId = candidates.ToDictionary(b => b.BookingId);

        // Every place of every line: its box's required date, else the vessel's ETD, else the booking's valid-from.
        var slots = new List<(Guid Booking, string Type)>();
        foreach (var r in requirements)
        {
            var b = byId[r.Booking];
            DateOnly? fallback = b.Etd is { } etd ? LocalDay(h.Branch, etd) : b.ValidFrom;
            var placed = boxesByLine[r.Line].Select(x => x.Required ?? fallback).ToList();
            placed.AddRange(Enumerable.Repeat(fallback, Math.Max(0, r.Qty - placed.Count)));
            slots.AddRange(placed.Where(d => d >= first && d <= last).Select(_ => (r.Booking, r.Type)));
        }

        var types = await TypesAsync(master, slots.Select(s => (string?)s.Type), ct);
        var ports = await master.PortsAsync(candidates.Select(b => b.Port).OfType<string>().Distinct(), ct);
        var parties = await master.PartiesAsync(candidates.Select(b => b.CustomerPartyCode).OfType<string>().Distinct(), ct);
        var counted = slots
            .Select(s => (Slot: s, Booking: byId[s.Booking]))
            .Where(x => x.Booking.CustomerPartyCode is { } c && parties.ContainsKey(c))
            .Select(x => (
                Shipper: parties[x.Booking.CustomerPartyCode!].Name, Agent: x.Booking.LinePartyCode,
                Column: (Region: ports.GetValueOrDefault(x.Booking.Port!)?.Region ?? "NONE",
                    Kind: x.Slot.Type is { } t && types.GetValueOrDefault(t)?.IsReefer == true ? "REEFER" : "DRY",
                    Size: SizeType(types, x.Slot.Type).Size ?? "")))
            .ToList();
        var columns = counted.Select(c => c.Column).Distinct()
            .OrderBy(c => c.Region, StringComparer.Ordinal).ThenBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Size, StringComparer.Ordinal).ToList();

        IReadOnlyList<IReadOnlyList<HeaderCell>> Headers(string corner) =>
        [
            [new(corner, RowSpan: 3), .. columns.GroupBy(c => c.Region).Select(g => new HeaderCell(g.Key, ColSpan: g.Count())), new("Total", RowSpan: 3)],
            [.. columns.GroupBy(c => (c.Region, c.Kind)).Select(g => new HeaderCell(g.Key.Kind, ColSpan: g.Count()))],
            [.. columns.Select(c => new HeaderCell(c.Size))],
        ];
        IReadOnlyList<TabularRow> Rows(Func<(string Shipper, string Agent, (string Region, string Kind, string Size) Column), string> by)
        {
            var rows = counted.GroupBy(by).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new TabularRow([g.Key, .. columns.Select(c => (object?)g.Count(x => x.Column == c)), g.Count()]))
                .ToList();
            rows.Add(new TabularRow(["Total", .. columns.Select(c => (object?)counted.Count(x => x.Column == c)), counted.Count], RowKind.Total));
            return rows;
        }
        IReadOnlyList<TabularColumn> Cols(double labelCm) =>
            [new(labelCm), .. columns.Select(_ => new TabularColumn(2.077, "0;(0);\"\"", CellAlign.Center)), new(1.574, null, CellAlign.Center)];

        return new TabularReport(
            FileName: $"CyContainerMonthlyMovement_{from:yyyyMMdd}-{to:yyyyMMdd}",
            Page: ReportPage.A3Landscape,
            MarginCm: 2,
            Heading:
            [
                new(h.BranchName, 10, Bold: true), new("CY CONTAINERS MOVEMENT (PORT)", 10, Bold: true), new("<< Monthly Report >>", 10, Bold: true),
                new($"ACTIVITIES DATE: {Day(from)} TO: {Day(to)}"),
            ],
            HeadingRight: [new($"Print Date: {Stamp(h.PrintedAt, "dd/MM/yyyy HH:mm")}")],
            Preamble: [],
            PreambleRight: [],
            Columns: Cols(6.733),
            HeaderRows: Headers("SHIPPER"),
            Rows: Rows(x => x.Shipper),
            PageLabel: "Page No#",
            After: [new TabularBlock(null, Cols(6.733), Headers("AGENT"), Rows(x => x.Agent))]);
    }
}

/// <summary>Stuffing - UnStuffing's filters, Vector's (BookingBLNo matches the carrier or customer reference).</summary>
internal sealed record StuffingFilter(
    string? AgentCode, string? CustomerCode, string? BookingBlNo, string? VesselCode, string? VoyageNo, string? BookingType, string? OrderType);
