using Gecko.Tos.Contracts;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Application;

/// <summary><see cref="ITosTruckVisits"/>: read in bulk, under the caller's tenant (RLS).</summary>
internal sealed class TosTruckVisits(TosDbContext db) : ITosTruckVisits
{
    public async Task<IReadOnlyDictionary<Guid, TosTruckVisit>> VisitsAsync(IReadOnlyCollection<Guid> truckVisitIds, CancellationToken ct)
    {
        var ids = truckVisitIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, TosTruckVisit>();

        var visits = await db.TruckVisits.AsNoTracking().Where(v => ids.Contains(v.TruckVisitId))
            .Select(v => new { v.TruckVisitId, v.VisitNo, v.TruckPlate, v.HaulierPartyCode }).ToListAsync(ct);

        // A visit recorded without a haulier: the haulier named on a booking it moved a box for.
        var missing = visits.Where(v => v.HaulierPartyCode is null).Select(v => v.TruckVisitId).ToList();
        var fromBookings = missing.Count == 0 ? [] : (await (
                from g in db.GateTransactions.AsNoTracking().Where(g => missing.Contains(g.TruckVisitId))
                join b in db.Bookings on g.BookingId equals b.BookingId
                where b.HaulierPartyCode != null
                orderby g.TransactionAt
                select new { g.TruckVisitId, b.HaulierPartyCode }).ToListAsync(ct))
            .GroupBy(x => x.TruckVisitId).ToDictionary(g => g.Key, g => g.First().HaulierPartyCode);

        return visits.ToDictionary(v => v.TruckVisitId,
            v => new TosTruckVisit(v.TruckVisitId, v.VisitNo, v.TruckPlate, v.HaulierPartyCode ?? fromBookings.GetValueOrDefault(v.TruckVisitId)));
    }
}
