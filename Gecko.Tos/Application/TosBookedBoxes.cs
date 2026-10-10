using Gecko.Tos.Contracts;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Application;

/// <summary><see cref="ITosBookedBoxes"/>: read under the caller's tenant (RLS).</summary>
internal sealed class TosBookedBoxes(TosDbContext db) : ITosBookedBoxes
{
    public async Task<IReadOnlyList<TosBookedBox>> BoxesAsync(
        Guid branchId, DateTimeOffset from, DateTimeOffset to, TosBookedBoxFilter f, CancellationToken ct)
    {
        var bookings = db.Bookings.AsNoTracking().Where(b => b.BranchId == branchId && b.Status != "CANCELLED");
        if (f.LineCode is { } line) bookings = bookings.Where(b => b.LinePartyCode == line);
        if (f.BookingTypeCode is { } type) bookings = bookings.Where(b => b.BookingTypeCode == type);
        if (f.OrderTypeCode is { } order) bookings = bookings.Where(b => b.OrderTypeCode == order);
        if (f.CarrierRef is { } carrierRef) bookings = bookings.Where(b => b.CarrierRef == carrierRef);

        var rows = await (
            from b in bookings
            join x in db.BookingContainers on b.BookingId equals x.BookingId
            join r in db.EquipmentRequirements on x.EquipmentRequirementId equals r.EquipmentRequirementId
            join c in db.VesselCalls on b.VesselCallId equals c.VesselCallId into calls
            from c in calls.DefaultIfEmpty()
            join l in db.VesselCallLines on b.VesselCallLineId equals l.VesselCallLineId into lines
            from l in lines.DefaultIfEmpty()
            where x.ContainerNo != null && x.ContainerNo != ""
                  // In SQL: an ETA in the window, or (no call) a gate-in in it; the box's FIRST gate-in is checked below.
                  && (c != null
                      ? c.Eta >= @from && c.Eta < to
                      : db.GateTransactions.Any(g => g.BookingContainerId == x.BookingContainerId && g.Direction == "IN"
                                                     && g.Status == "COMPLETED" && g.TransactionAt >= @from && g.TransactionAt < to))
            select new
            {
                x.BookingContainerId, b.BookingId, ContainerNo = x.ContainerNo!, r.EquipmentTypeCode, b.OrderTypeCode, b.BookingTypeCode,
                b.LinePartyCode, VesselCode = c == null ? null : c.VesselCode, Eta = c == null ? (DateTimeOffset?)null : c.Eta,
                VoyageIn = l != null && l.VoyageIn != null ? l.VoyageIn : c == null ? null : c.OperatorVoyageIn,
                VoyageOut = l != null && l.VoyageOut != null ? l.VoyageOut : c == null ? null : c.OperatorVoyageOut,
            }).ToListAsync(ct);
        if (f.VesselCode is { } vessel) rows = rows.Where(r => r.VesselCode == vessel).ToList();
        if (f.Voyage is { } voyage) rows = rows.Where(r => r.VoyageIn == voyage || r.VoyageOut == voyage).ToList();

        // The boxes' gate dates, fetched in slices (an IN list SQL Server accepts).
        var moves = new List<(Guid Box, string Direction, string FullEmpty, DateTimeOffset At)>();
        foreach (var slice in rows.Select(r => r.BookingContainerId).Distinct().Chunk(2000))
            moves.AddRange((await db.GateTransactions.AsNoTracking()
                    .Where(g => slice.Contains(g.BookingContainerId) && g.Status == "COMPLETED" && g.DeletedAt == null)
                    .Select(g => new { g.BookingContainerId, g.Direction, g.FullEmpty, g.TransactionAt }).ToListAsync(ct))
                .Select(g => (g.BookingContainerId, g.Direction, g.FullEmpty, g.TransactionAt)));
        var byBox = moves.ToLookup(m => m.Box);
        DateTimeOffset? First(Guid box, string dir, string fe) =>
            byBox[box].Where(m => m.Direction == dir && m.FullEmpty == fe).Select(m => (DateTimeOffset?)m.At).Min();
        DateTimeOffset? Last(Guid box, string dir, string fe) =>
            byBox[box].Where(m => m.Direction == dir && m.FullEmpty == fe).Select(m => (DateTimeOffset?)m.At).Max();

        return rows
            .Select(r =>
            {
                var firstIn = byBox[r.BookingContainerId].Where(m => m.Direction == "IN").Select(m => (DateTimeOffset?)m.At).Min();
                return (r, Anchor: r.Eta ?? firstIn);
            })
            .Where(x => x.Anchor >= from && x.Anchor < to)
            .Select(x => new TosBookedBox(x.r.BookingContainerId, x.r.BookingId, x.r.ContainerNo, x.r.EquipmentTypeCode, x.r.OrderTypeCode,
                x.r.BookingTypeCode, x.r.LinePartyCode, x.r.VesselCode, x.r.VoyageOut ?? x.r.VoyageIn, x.r.Eta,
                First(x.r.BookingContainerId, "IN", "EMPTY"), Last(x.r.BookingContainerId, "OUT", "EMPTY"),
                First(x.r.BookingContainerId, "IN", "FULL"), Last(x.r.BookingContainerId, "OUT", "FULL")))
            .OrderBy(b => b.ContainerNo, StringComparer.Ordinal)
            .ToList();
    }
}
