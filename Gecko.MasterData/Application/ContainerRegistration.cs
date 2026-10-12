using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.MasterData.Endpoints;
using Gecko.MasterData.Infrastructure.Persistence;
using Gecko.MasterData.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Application;

/// <summary>
/// Registers the boxes the depot handles but the registry does not know (owner 2026-10-12, as Vector's gate filled
/// Master.Container): line-owned by the line it was handled for, of the type it was handled as, in service. Never changes
/// a registered box. Two gates registering the same box at once: the second finds it there (unique index) and moves on.
/// </summary>
internal sealed class ContainerRegistration(MasterDataDbContext db, ITenantContext caller)
{
    public async Task<IReadOnlyList<string>> EnsureRegisteredAsync(IReadOnlyCollection<UnregisteredBox> boxes, CancellationToken ct)
    {
        var wanted = boxes
            .Select(b => b with { ContainerNo = ContainerNumber.Normalise(b.ContainerNo) })
            .Where(b => ContainerNumber.IsWellFormed(b.ContainerNo))
            .DistinctBy(b => b.ContainerNo).ToList();
        if (wanted.Count == 0) return [];
        var numbers = wanted.Select(b => b.ContainerNo).ToList();
        var known = await db.Containers.AsNoTracking().Where(c => numbers.Contains(c.ContainerNo)).Select(c => c.ContainerNo).ToListAsync(ct);
        var missing = wanted.Where(b => !known.Contains(b.ContainerNo)).ToList();
        if (missing.Count == 0) return [];

        var typeCodes = missing.Select(b => b.EquipmentTypeCode).OfType<string>().Distinct().ToList();
        var types = await db.EquipmentTypes.AsNoTracking().Where(t => typeCodes.Contains(t.TypeCode))
            .ToDictionaryAsync(t => t.TypeCode, t => t.EquipmentTypeId, StringComparer.OrdinalIgnoreCase, ct);
        var lineCodes = missing.Select(b => b.LinePartyCode).OfType<string>().Distinct().ToList();
        var lines = await db.Parties.AsNoTracking().Where(p => lineCodes.Contains(p.PartyCode))
            .ToDictionaryAsync(p => p.PartyCode, p => p.PartyId, StringComparer.OrdinalIgnoreCase, ct);

        var registered = new List<string>();
        foreach (var box in missing)
        {
            db.Containers.Add(new Container
            {
                TenantId = caller.TenantId(),
                ContainerNo = box.ContainerNo,
                EquipmentTypeId = box.EquipmentTypeCode is { } t ? types.GetValueOrDefault(t) is { } id && id != Guid.Empty ? id : null : null,
                OwnerPartyId = box.LinePartyCode is { } l ? lines.GetValueOrDefault(l) is { } p && p != Guid.Empty ? p : null : null,
                OwnershipType = "LINE_OWNED",
                Status = "IN_SERVICE",
                StatusChangedAt = DateTimeOffset.UtcNow,
                IsCheckDigitValid = ContainerNumber.IsValid(box.ContainerNo),
                Remarks = "Registered automatically when the depot first handled it.",
            });
            try
            {
                await db.SaveChangesAsync(ct);
                registered.Add(box.ContainerNo);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();   // registered by another gate a moment ago: nothing to do
            }
        }
        return registered;
    }
}
