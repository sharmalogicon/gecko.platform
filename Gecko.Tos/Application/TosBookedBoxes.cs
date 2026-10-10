using Gecko.Tos.Contracts;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Application;

/// <summary><see cref="ITosBookedBoxes"/>: read under the caller's tenant (RLS).</summary>
internal sealed class TosBookedBoxes(TosDbContext db) : ITosBookedBoxes
{
    public async Task<IReadOnlyDictionary<Guid, TosBookedBox>> BoxesByIdAsync(IReadOnlyCollection<Guid> bookingContainerIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, TosBookedBox>();
        foreach (var slice in bookingContainerIds.Distinct().Chunk(2000))
        {
            var rows = await Rows(db.Bookings.AsNoTracking(), x => slice.Contains(x.BookingContainerId)).ToListAsync(ct);
            foreach (var box in await ShapeAsync(rows, ct)) result[box.BookingContainerId] = box;
        }
        return result;
    }

    /// <summary>A member-initialised class, not a positional record: EF can filter on what it projects this way.</summary>
    private sealed class Row
    {
        public Guid BookingContainerId { get; init; }
        public Guid BookingId { get; init; }
        public string ContainerNo { get; init; } = "";
        public string EquipmentTypeCode { get; init; } = "";
        public string OrderTypeCode { get; init; } = "";
        public string BookingTypeCode { get; init; } = "";
        public string LinePartyCode { get; init; } = "";
        public string? VesselCode { get; init; }
        public DateTimeOffset? Eta { get; init; }
        public string? VoyageIn { get; init; }
        public string? VoyageOut { get; init; }
        public DateOnly? RequiredDate { get; init; }
    }

    private IQueryable<Row> Rows(IQueryable<Infrastructure.Persistence.Entities.Booking> bookings,
        System.Linq.Expressions.Expression<Func<Infrastructure.Persistence.Entities.BookingContainer, bool>> box) =>
        from b in bookings
        join x in db.BookingContainers.Where(box) on b.BookingId equals x.BookingId
        join r in db.EquipmentRequirements on x.EquipmentRequirementId equals r.EquipmentRequirementId
        join c in db.VesselCalls on b.VesselCallId equals c.VesselCallId into calls
        from c in calls.DefaultIfEmpty()
        join l in db.VesselCallLines on b.VesselCallLineId equals l.VesselCallLineId into lines
        from l in lines.DefaultIfEmpty()
        where x.ContainerNo != null && x.ContainerNo != ""
        select new Row
        {
            BookingContainerId = x.BookingContainerId, BookingId = b.BookingId, ContainerNo = x.ContainerNo!,
            EquipmentTypeCode = r.EquipmentTypeCode, OrderTypeCode = b.OrderTypeCode, BookingTypeCode = b.BookingTypeCode,
            LinePartyCode = b.LinePartyCode, VesselCode = c == null ? null : c.VesselCode, Eta = c == null ? (DateTimeOffset?)null : c.Eta,
            VoyageIn = l != null && l.VoyageIn != null ? l.VoyageIn : c == null ? null : c.OperatorVoyageIn,
            VoyageOut = l != null && l.VoyageOut != null ? l.VoyageOut : c == null ? null : c.OperatorVoyageOut,
            RequiredDate = x.RequiredDate,
        };

    /// <summary>The boxes' gate dates (first empty/laden in, last empty/laden out), fetched in slices SQL Server accepts.</summary>
    private async Task<List<TosBookedBox>> ShapeAsync(IReadOnlyList<Row> rows, CancellationToken ct)
    {
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

        return rows.Select(r => new TosBookedBox(r.BookingContainerId, r.BookingId, r.ContainerNo, r.EquipmentTypeCode, r.OrderTypeCode,
                r.BookingTypeCode, r.LinePartyCode, r.VesselCode, r.VoyageOut ?? r.VoyageIn, r.Eta,
                First(r.BookingContainerId, "IN", "EMPTY"), Last(r.BookingContainerId, "OUT", "EMPTY"),
                First(r.BookingContainerId, "IN", "FULL"), Last(r.BookingContainerId, "OUT", "FULL"), r.RequiredDate))
            .ToList();
    }

    public async Task<IReadOnlyList<TosBookedBox>> BoxesAsync(
        Guid branchId, DateTimeOffset from, DateTimeOffset to, TosBookedBoxFilter f, CancellationToken ct)
    {
        var bookings = db.Bookings.AsNoTracking().Where(b => b.BranchId == branchId && b.Status != "CANCELLED");
        if (f.LineCode is { } line) bookings = bookings.Where(b => b.LinePartyCode == line);
        if (f.BookingTypeCode is { } type) bookings = bookings.Where(b => b.BookingTypeCode == type);
        if (f.OrderTypeCode is { } order) bookings = bookings.Where(b => b.OrderTypeCode == order);
        if (f.CarrierRef is { } carrierRef) bookings = bookings.Where(b => b.CarrierRef == carrierRef);

        // In SQL: an ETA in the window, or (no call) a gate-in in it; the box's FIRST gate-in is checked below.
        var rows = await Rows(bookings, x => true)
            .Where(r => r.Eta != null
                ? r.Eta >= from && r.Eta < to
                : db.GateTransactions.Any(g => g.BookingContainerId == r.BookingContainerId && g.Direction == "IN"
                                               && g.Status == "COMPLETED" && g.TransactionAt >= from && g.TransactionAt < to))
            .ToListAsync(ct);
        if (f.VesselCode is { } vessel) rows = rows.Where(r => r.VesselCode == vessel).ToList();
        if (f.Voyage is { } voyage) rows = rows.Where(r => r.VoyageIn == voyage || r.VoyageOut == voyage).ToList();

        var boxes = await ShapeAsync(rows, ct);
        var firstIn = (await FirstGateInsAsync(rows.Where(r => r.Eta is null).Select(r => r.BookingContainerId).ToList(), ct));
        return boxes
            .Where(b => (b.Eta ?? firstIn.GetValueOrDefault(b.BookingContainerId)) is { } anchor && anchor >= from && anchor < to)
            .OrderBy(b => b.ContainerNo, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<Dictionary<Guid, DateTimeOffset>> FirstGateInsAsync(IReadOnlyList<Guid> boxes, CancellationToken ct)
    {
        var result = new Dictionary<Guid, DateTimeOffset>();
        foreach (var slice in boxes.Chunk(2000))
            foreach (var x in await db.GateTransactions.AsNoTracking()
                         .Where(g => slice.Contains(g.BookingContainerId) && g.Direction == "IN" && g.Status == "COMPLETED" && g.DeletedAt == null)
                         .GroupBy(g => g.BookingContainerId)
                         .Select(g => new { g.Key, At = g.Min(m => m.TransactionAt) }).ToListAsync(ct))
                result[x.Key] = x.At;
        return result;
    }
}
