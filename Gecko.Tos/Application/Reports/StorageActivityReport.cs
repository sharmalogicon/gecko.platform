using Gecko.Data.Documents;
using Gecko.MasterData.Contracts;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static Gecko.Tos.Application.Reports.GateInOutReport;
using static Gecko.Tos.Application.Reports.OperationReportKit;

namespace Gecko.Tos.Application.Reports;

internal sealed record StorageActivityFilter(
    DateOnly From, DateOnly To, string? AgentCode = null, string? VesselCode = null, string? VoyageNo = null,
    string? OrderType = null, string? BookingType = null);

/// <summary>
/// Vector's TMS.Operation.OutBound.ContainerStorageActivity (Report.usp_OutBound_ContainerStorageActivity) — OUTBOUND
/// CONTAINER STORAGE ACTIVITY, no money (that is the accounting report): the EXPORT bookings whose vessel's ETA falls in the
/// depot days asked for, every booked box that has moved through the gate, grouped by that ETA, with the box's first empty /
/// laden in and last empty / laden out and the days between (inclusive, as Vector counted), then the Service (order type) ×
/// size/type count. Kept as the RDL heads them: "ETD" over the ETA, "Booking Agent" over the booking no. The ETA groups run
/// in date order (the RDL sorted the dd/MM/yyyy text). A booking type other than EXPORT selects nothing, as on Vector.
/// </summary>
internal static class StorageActivityReport
{
    private sealed class Row
    {
        public Guid BookingContainerId { get; init; }
        public string OrderNo { get; init; } = "";
        public string ContainerNo { get; init; } = "";
        public string EquipmentTypeCode { get; init; } = "";
        public string OrderTypeCode { get; init; } = "";
        public string? CustomerCode { get; init; }
        public string? CarrierRef { get; init; }
        public string? CustomerRef { get; init; }
        public string? Remarks { get; init; }
        public string VesselCode { get; init; } = "";
        public DateTimeOffset Eta { get; init; }
        public string? VoyageIn { get; init; }
        public string? VoyageOut { get; init; }
    }

    public static async Task<TabularReport> BuildAsync(
        TosDbContext db, IMasterDataReferences master, BranchClock.Branch branch, string branchName, string printedBy,
        DateTimeOffset printedAt, StorageActivityFilter f, CancellationToken ct)
    {
        var start = BranchClock.StartOf(branch, f.From);
        var end = BranchClock.StartOf(branch, f.To.AddDays(1));
        var asked = Clean(f.BookingType);

        var rows = new List<Row>();
        if (asked is null or "EXPORT")
        {
            var bookings = db.Bookings.AsNoTracking().Where(b => b.BranchId == branch.BranchId && b.Status != "CANCELLED" && b.BookingTypeCode == "EXPORT");
            if (Clean(f.AgentCode) is { } agent) bookings = bookings.Where(b => b.LinePartyCode == agent);
            if (Clean(f.OrderType) is { } orderType) bookings = bookings.Where(b => b.OrderTypeCode == orderType);
            var query =
                from b in bookings
                join x in db.BookingContainers on b.BookingId equals x.BookingId
                join r in db.EquipmentRequirements on x.EquipmentRequirementId equals r.EquipmentRequirementId
                join c in db.VesselCalls on b.VesselCallId equals c.VesselCallId
                join l in db.VesselCallLines on b.VesselCallLineId equals l.VesselCallLineId into lines
                from l in lines.DefaultIfEmpty()
                where x.ContainerNo != null && x.ContainerNo != "" && x.DeletedAt == null && c.Eta >= start && c.Eta < end
                      && db.GateTransactions.Any(g => g.BookingContainerId == x.BookingContainerId && g.Status == "COMPLETED" && g.DeletedAt == null)
                select new Row
                {
                    BookingContainerId = x.BookingContainerId, OrderNo = b.OrderNo, ContainerNo = x.ContainerNo!,
                    EquipmentTypeCode = r.EquipmentTypeCode, OrderTypeCode = b.OrderTypeCode, CustomerCode = b.CustomerPartyCode,
                    CarrierRef = b.CarrierRef, CustomerRef = b.CustomerRef, Remarks = x.Remarks, VesselCode = c.VesselCode, Eta = c.Eta,
                    VoyageIn = l != null && l.VoyageIn != null ? l.VoyageIn : c.OperatorVoyageIn,
                    VoyageOut = l != null && l.VoyageOut != null ? l.VoyageOut : c.OperatorVoyageOut,
                };
            if (Clean(f.VesselCode) is { } vessel) query = query.Where(r => r.VesselCode == vessel);
            if (Clean(f.VoyageNo) is { } voyage) query = query.Where(r => r.VoyageIn == voyage || r.VoyageOut == voyage);
            rows = await query.ToListAsync(ct);
        }

        // The box's gate dates on this booking: first empty / laden in, last empty / laden out.
        var ids = rows.Select(r => r.BookingContainerId).ToList();
        var moves = new List<(Guid Box, string Direction, string FullEmpty, DateTimeOffset At)>();
        foreach (var slice in ids.Chunk(2000))
            moves.AddRange((await db.GateTransactions.AsNoTracking()
                    .Where(g => slice.Contains(g.BookingContainerId) && g.Status == "COMPLETED" && g.DeletedAt == null)
                    .Select(g => new { g.BookingContainerId, g.Direction, g.FullEmpty, g.TransactionAt }).ToListAsync(ct))
                .Select(g => (g.BookingContainerId, g.Direction, g.FullEmpty, g.TransactionAt)));
        var byBox = moves.ToLookup(m => m.Box);
        DateOnly? When(Guid box, string direction, string load, bool first)
        {
            var set = byBox[box].Where(m => m.Direction == direction && m.FullEmpty == load).Select(m => m.At).ToList();
            return set.Count == 0 ? null : LocalDay(branch, first ? set.Min() : set.Max());
        }

        var parties = await master.PartiesAsync(rows.Select(r => r.CustomerCode).OfType<string>().Distinct(), ct);
        var ordered = rows.OrderBy(r => LocalDay(branch, r.Eta)).ThenBy(r => r.OrderNo, StringComparer.Ordinal)
            .ThenBy(r => r.ContainerNo, StringComparer.Ordinal).ToList();
        DateOnly? previous = null;
        var grid = ordered.Select(r =>
        {
            var eta = LocalDay(branch, r.Eta);
            var group = eta == previous ? null : Day(eta, "dd/MM/yyyy");
            previous = eta;
            var (emptyIn, emptyOut) = (When(r.BookingContainerId, "IN", "EMPTY", true), When(r.BookingContainerId, "OUT", "EMPTY", false));
            var (ladenIn, ladenOut) = (When(r.BookingContainerId, "IN", "FULL", true), When(r.BookingContainerId, "OUT", "FULL", false));
            return new TabularRow([group, r.ContainerNo, r.EquipmentTypeCode,
                emptyIn, emptyOut, InclusiveDays(emptyIn, emptyOut), ladenIn, ladenOut, InclusiveDays(ladenIn, ladenOut),
                r.OrderTypeCode, r.CustomerCode is { } c ? parties.GetValueOrDefault(c)?.Name : null,
                BookingBlNo(r.CarrierRef, r.CustomerRef), r.Remarks]);
        }).ToList();

        var firstVessel = ordered.FirstOrDefault()?.VesselCode;
        var vesselName = firstVessel is null ? "" : (await master.VesselsAsync([firstVessel], ct)).GetValueOrDefault(firstVessel)?.VesselName ?? firstVessel;
        TabularColumn Mid(double mm, string? format = null) => new(mm / 10, format, CellAlign.Center);
        return new TabularReport(
            FileName: $"OutboundContainerStorageActivity_{f.From:yyyyMMdd}-{f.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 0.3,
            Heading: [new(branchName, 10, Bold: true), new("<< OUTBOUND CONTAINER STORAGE ACTIVITY >>", 10, Bold: true)],
            HeadingRight: [new($"Printed By: {printedBy}"), new($"Printed On: {printedAt:dd/MM/yyyy HH:mm}")],
            Preamble:
            [
                new($"From ETD: {Day(f.From)} To ETD: {Day(f.To)}"),
                new($"Agent: {Clean(f.AgentCode)}   Vessel Name: {vesselName}   Voyage No.: {f.VoyageNo?.Trim()}"),
            ],
            PreambleRight: [],
            Columns:
            [
                Mid(21.56), new(2.5), Mid(11.38), Mid(22.09, "dd/MM/yyyy"), Mid(22.09, "dd/MM/yyyy"), Mid(11.01),
                Mid(22.09, "dd/MM/yyyy"), Mid(22.09, "dd/MM/yyyy"), Mid(11.01), Mid(22.92), new(4.564), Mid(25), new(2.5),
            ],
            HeaderRows:
            [
                [new("ETD"), new("Container No"), new("Size"), new("Empty IN"), new("Empty Out"), new("Days"), new("Laden IN"),
                 new("Laden Out"), new("Days"), new("Service"), new("Shipper"), new("Booking Agent"), new("Remarks")],
            ],
            Rows: grid,
            PageLabel: "Page:",
            After: [CountMatrix("Summary", "Service", ordered.Select(r => ((string?)r.OrderTypeCode, (string?)r.EquipmentTypeCode)))]);
    }
}
