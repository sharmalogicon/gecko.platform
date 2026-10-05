using Gecko.Tos.Contracts;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Application;

/// <summary><see cref="ITosGateCoupons"/>: read in bulk, under the caller's tenant (RLS).</summary>
internal sealed class TosGateCoupons(TosDbContext db) : ITosGateCoupons
{
    public async Task<IReadOnlyDictionary<string, TosGateCoupon>> CouponsAsync(IReadOnlyCollection<string> couponRefs, CancellationToken ct)
    {
        var refs = couponRefs.Distinct().ToList();
        if (refs.Count == 0) return new Dictionary<string, TosGateCoupon>();

        var rows = await (
            from a in db.GateAuthorizations.AsNoTracking().Where(a => refs.Contains(a.CouponRef))
            join b in db.Bookings on a.BookingId equals b.BookingId into bookings
            from b in bookings.DefaultIfEmpty()
            join g in db.GateTransactions on a.ConsumedByGateTransactionId equals g.GateTransactionId into used
            from g in used.DefaultIfEmpty()
            select new TosGateCoupon(a.CouponRef, b == null ? null : b.OrderNo, a.ContainerNo, a.MovementCode, a.ValidFrom, a.ValidUntil,
                a.ConsumedAt, g == null ? null : g.EirNo, a.RevokedAt)).ToListAsync(ct);
        return rows.GroupBy(r => r.CouponRef).ToDictionary(g => g.Key, g => g.First());
    }
}
