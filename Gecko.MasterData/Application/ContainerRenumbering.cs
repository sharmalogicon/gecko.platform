using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.MasterData.Endpoints;
using Gecko.MasterData.Infrastructure.Persistence;
using Gecko.MasterData.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Application;

/// <summary>
/// The registry's half of a container renumber (gecko_master 35): the box keeps its id and takes the new number, and an
/// audit row says who corrected what, when and why. Its temporal history keeps the old number too.
/// </summary>
internal sealed class ContainerRenumbering(MasterDataDbContext db, ITenantContext caller) : IContainerRenumbering
{
    public async Task<ContainerRenumberResult> RenumberAsync(string oldContainerNo, string newContainerNo, string reason, string? rowVersion,
        CancellationToken ct)
    {
        var (from, to) = (ContainerNumber.Normalise(oldContainerNo), ContainerNumber.Normalise(newContainerNo));
        var container = await db.Containers.SingleOrDefaultAsync(c => c.ContainerNo == from, ct);
        if (container is null) return new(ContainerRenumberOutcome.NotFound);
        if (await db.Containers.AnyAsync(c => c.ContainerNo == to, ct)) return new(ContainerRenumberOutcome.NewNumberTaken);
        if (rowVersion is not null && !db.TrySetExpectedVersion(container, rowVersion)) return new(ContainerRenumberOutcome.Stale);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        container.ContainerNo = to;
        container.IsCheckDigitValid = ContainerNumber.IsValid(to);
        db.ContainerRenumbers.Add(new ContainerRenumber
        {
            TenantId = caller.TenantId(),
            ContainerId = container.ContainerId,
            OldContainerNo = from,
            NewContainerNo = to,
            Reason = reason.Trim(),
            RenumberedBy = caller.UserId,
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return new(ContainerRenumberOutcome.Stale); }
        catch (DbUpdateException e) when (e.InnerException?.Message.Contains("uq_container") == true)
        {
            return new(ContainerRenumberOutcome.NewNumberTaken);
        }
        await tx.CommitAsync(ct);
        return new(ContainerRenumberOutcome.Renumbered, container.ContainerId, Convert.ToBase64String(container.RowVersion));
    }
}
