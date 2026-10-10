using Gecko.Tos.Contracts;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Application;

/// <summary><see cref="ITosBookingHeaders"/>: read in bulk, under the caller's tenant (RLS).</summary>
internal sealed class TosBookingHeaders(TosDbContext db) : ITosBookingHeaders
{
    public async Task<IReadOnlyDictionary<Guid, TosBookingHeader>> HeadersAsync(IReadOnlyCollection<Guid> bookingIds, CancellationToken ct)
    {
        var ids = bookingIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, TosBookingHeader>();

        var headers = await (
            from b in db.Bookings.AsNoTracking().Where(b => ids.Contains(b.BookingId))
            join c in db.VesselCalls on b.VesselCallId equals c.VesselCallId into calls
            from c in calls.DefaultIfEmpty()
            join l in db.VesselCallLines on b.VesselCallLineId equals l.VesselCallLineId into lines
            from l in lines.DefaultIfEmpty()
            select new
            {
                b.BookingId, b.OrderNo, b.CarrierRef, b.SubBlNo, b.CreatedAt, b.Status, b.BookingTypeCode, b.OrderTypeCode,
                b.LinePartyCode, b.AgentPartyCode, b.CustomerPartyCode, b.ForwarderPartyCode, b.TotalVolumeCbm,
                VesselCode = c == null ? null : c.VesselCode, CallRef = c == null ? null : c.CallRef,
                TerminalCode = c == null ? null : c.TerminalCode,
                Voyage = l == null ? null : (l.VoyageOut ?? l.VoyageIn),
            }).ToListAsync(ct);

        var steps = await (
            from x in db.BookingContainers.AsNoTracking().Where(x => ids.Contains(x.BookingId))
            join p in db.MovementPlans on x.BookingContainerId equals p.BookingContainerId
            where p.Status != "CANCELLED"
            select new { x.BookingId, x.BookingContainerId, Done = p.Status == "DONE" || p.Status == "SKIPPED" }).ToListAsync(ct);
        var byBooking = steps.GroupBy(s => s.BookingId).ToDictionary(g => g.Key, g => g.ToList());

        return headers.ToDictionary(h => h.BookingId, h =>
        {
            var own = byBooking.GetValueOrDefault(h.BookingId) ?? [];
            var completed = own.GroupBy(s => s.BookingContainerId).Where(g => g.All(s => s.Done)).Select(g => g.Key).ToList();
            return new TosBookingHeader(h.BookingId, h.OrderNo, h.CarrierRef, h.SubBlNo, h.CreatedAt, h.Status,
                h.BookingTypeCode, h.OrderTypeCode, h.LinePartyCode, h.AgentPartyCode, h.CustomerPartyCode, h.ForwarderPartyCode,
                h.VesselCode, h.CallRef, h.Voyage, h.TerminalCode, own.Count, own.Count(s => s.Done), completed, h.TotalVolumeCbm);
        });
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ContainerTypesAsync(IReadOnlyCollection<Guid> bookingContainerIds, CancellationToken ct)
    {
        var ids = bookingContainerIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        return await (
            from x in db.BookingContainers.AsNoTracking().Where(x => ids.Contains(x.BookingContainerId))
            join r in db.EquipmentRequirements on x.EquipmentRequirementId equals r.EquipmentRequirementId
            select new { x.BookingContainerId, r.EquipmentTypeCode }).ToDictionaryAsync(x => x.BookingContainerId, x => x.EquipmentTypeCode, ct);
    }

    public async Task<IReadOnlyList<TosBookingHeader>> SearchAsync(string text, IReadOnlyCollection<Guid>? branchIds, int take, CancellationToken ct)
    {
        var q = text.Trim().ToUpperInvariant();
        if (q.Length == 0) return [];
        var rows = db.Bookings.AsNoTracking()
            .Where(b => b.OrderNo.Contains(q) || (b.CarrierRef != null && b.CarrierRef.Contains(q)) || (b.SubBlNo != null && b.SubBlNo.Contains(q)));
        if (branchIds is not null)
        {
            var allowed = branchIds.ToList();
            rows = rows.Where(b => allowed.Contains(b.BranchId));
        }
        var ids = await rows
            .OrderByDescending(b => b.OrderNo == q || b.CarrierRef == q || b.SubBlNo == q)
            .ThenByDescending(b => b.CreatedAt)
            .Select(b => b.BookingId).Take(Math.Clamp(take, 1, 50)).ToListAsync(ct);
        var headers = await HeadersAsync(ids, ct);
        return ids.Where(headers.ContainsKey).Select(id => headers[id]).ToList();
    }
}
