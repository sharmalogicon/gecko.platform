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

    // ── TMS.CustomerService.BookingContainerList ──

    /// <summary>
    /// The desktop's "Booking Container List" (Report.usp_CustomerService_BookingContainerList): the boxes of EXPORT bookings
    /// that have gone out empty — on the depot days asked for, unless a booking no is asked (then any day, as on Vector) —
    /// by booking no then container, with the agent (the booking's agent, else its line), shipper, the booked size / type,
    /// the seal (declared, else the one recorded at the empty gate-out), the empty gate-out time, vessel, voyage and the
    /// destination port (final, else discharge). One row per box (its latest empty gate-out); cancelled bookings are out.
    /// </summary>
    public static async Task<TabularReport> BookingContainerListAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, DateOnly? from, DateOnly? to,
        string? agentCode, string? customerCode, string? vesselCode, string? voyageNo, string? bookingBlNo, CancellationToken ct)
    {
        var bookings = db.Bookings.AsNoTracking()
            .Where(b => b.BranchId == h.Branch.BranchId && b.BookingTypeCode == "EXPORT" && b.Status != "CANCELLED" && b.DeletedAt == null);
        if (Clean(agentCode) is { } agent) bookings = bookings.Where(b => (b.AgentPartyCode ?? b.LinePartyCode) == agent);
        if (Clean(customerCode) is { } customer) bookings = bookings.Where(b => b.CustomerPartyCode == customer);
        var blNo = bookingBlNo?.Trim() is { Length: > 0 } bl ? bl : null;
        if (blNo is not null) bookings = bookings.Where(b => b.CarrierRef == blNo || b.CustomerRef == blNo || b.SubBlNo == blNo);
        var outs = db.GateTransactions.Where(g => g.Status == "COMPLETED" && g.DeletedAt == null && g.Direction == GateRules.Out && g.FullEmpty == GateRules.Empty);
        if (blNo is null && from is { } first && to is { } last)
        {
            var (start, end) = (BranchClock.StartOf(h.Branch, first), BranchClock.StartOf(h.Branch, last.AddDays(1)));
            outs = outs.Where(g => g.TransactionAt >= start && g.TransactionAt < end);
        }
        var rows = await (
                from b in bookings
                join x in db.BookingContainers on b.BookingId equals x.BookingId
                join r in db.EquipmentRequirements on x.EquipmentRequirementId equals r.EquipmentRequirementId
                join g in outs on x.BookingContainerId equals g.BookingContainerId
                join c in db.VesselCalls on b.VesselCallId equals c.VesselCallId into calls
                from c in calls.DefaultIfEmpty()
                where x.DeletedAt == null && x.ContainerNo != null && x.ContainerNo != ""
                select new
                {
                    x.BookingContainerId, ContainerNo = x.ContainerNo!, x.DeclaredSealNo, r.EquipmentTypeCode, g.GateTransactionId, g.TransactionAt,
                    Agent = b.AgentPartyCode ?? b.LinePartyCode, b.CustomerPartyCode, b.CarrierRef, b.CustomerRef, b.FpdPortCode, b.PodPortCode,
                    VesselCode = c == null ? null : c.VesselCode, VoyageIn = c == null ? null : c.OperatorVoyageIn, VoyageOut = c == null ? null : c.OperatorVoyageOut,
                })
            .ToListAsync(ct);
        if (Clean(vesselCode) is { } vessel) rows = rows.Where(r => r.VesselCode == vessel).ToList();
        if (Clean(voyageNo) is { } voyage) rows = rows.Where(r => r.VoyageOut == voyage || r.VoyageIn == voyage).ToList();
        rows = rows.GroupBy(r => r.BookingContainerId).Select(g => g.MaxBy(r => r.TransactionAt)!).ToList();

        var sealsAt = new List<(Guid Move, DateTimeOffset At, string SealNo, string SealType)>();
        foreach (var slice in rows.Select(r => r.GateTransactionId).Chunk(2000))
            sealsAt.AddRange((await db.GateTransactionSeals.AsNoTracking().Where(s => slice.Contains(s.GateTransactionId) && s.DeletedAt == null)
                    .Select(s => new { s.GateTransactionId, s.CreatedAt, s.SealNo, s.SealType }).ToListAsync(ct))
                .Select(s => (s.GateTransactionId, s.CreatedAt, s.SealNo, s.SealType)));
        var seals = sealsAt.Where(s => IsAgentSeal(s.SealType)).OrderBy(s => s.At).ToLookup(s => s.Move, s => s.SealNo);
        var types = await TypesAsync(master, rows.Select(r => (string?)r.EquipmentTypeCode), ct);
        var parties = await master.PartiesAsync(rows.Select(r => r.CustomerPartyCode).OfType<string>().Distinct(), ct);
        var vessels = await master.VesselsAsync(rows.Select(r => r.VesselCode).OfType<string>().Distinct(), ct);
        var ports = await master.PortsAsync(rows.Select(r => r.FpdPortCode ?? r.PodPortCode).OfType<string>().Distinct(), ct);

        var grid = rows.OrderBy(r => BookingBlNo(r.CarrierRef, r.CustomerRef), StringComparer.Ordinal).ThenBy(r => r.ContainerNo, StringComparer.Ordinal)
            .Select(r =>
            {
                var (size, type) = SizeType(types, r.EquipmentTypeCode);
                var port = r.FpdPortCode ?? r.PodPortCode;
                return new TabularRow([r.Agent, r.CustomerPartyCode is { } c ? parties.GetValueOrDefault(c)?.Name : null,
                    BookingBlNo(r.CarrierRef, r.CustomerRef), r.ContainerNo, size, type,
                    r.DeclaredSealNo ?? seals[r.GateTransactionId].FirstOrDefault(), Local(h.Branch, r.TransactionAt),
                    r.VesselCode is { } v ? vessels.GetValueOrDefault(v)?.VesselName : null, Voyage("EXPORT", r.VoyageIn, r.VoyageOut),
                    port is null ? null : ports.GetValueOrDefault(port)?.Name ?? port]);
            }).ToList();

        var vesselHeading = Clean(vesselCode) is { } asked ? vessels.GetValueOrDefault(asked)?.VesselName ?? asked : "";
        return new TabularReport(
            FileName: $"BookingContainerList_{Stamp(h.PrintedAt, "yyyyMMdd-HHmm")}",
            Page: ReportPage.A3Landscape,
            MarginCm: 2,
            Heading:
            [
                new(h.BranchName, 10, Bold: true),
                new($"Agent:{Clean(agentCode)}  Customer :{Clean(customerCode)}  Vessel:{vesselHeading}", 10, Bold: true),
                new($"Booking Type: EXPORT Booking No:{blNo}", 10, Bold: true),
            ],
            HeadingRight: [new($"Printed By: {h.PrintedBy}"), new($"Print Date: {Stamp(h.PrintedAt, "dd/MM/yyyy HH:mm")}")],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(2.26187), new(5.72067), new(2.5), new(2.5), new(0.82021), new(1.12417), new(2.5), new(2.97625, "dd/MM/yyyy HH:mm"),
                new(6.54186), new(1.73271), new(3.26729),
            ],
            HeaderRows:
            [
                [new("Agent"), new("Shipper"), new("Booking No."), new("Cntr No."), new("Size"), new("Type"), new("Seal No."),
                 new("EMPTY GATE OUT DATETIME"), new("Vessel Name"), new("Voy"), new(" Port")],
            ],
            Rows: grid,
            PageLabel: "Page No#");
    }
}
