using System.Text.Json;
using Gecko.Data;
using Gecko.Revenue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Application;

/// <summary>
/// TOS <c>ContainerRenumbered</c> (CONTAINER_MASTER_FOR_API §3, owner 2026-10-12): a container number was corrected, and
/// everything moves to the new number — Revenue's copy of the box, its stays and reefer sessions, its charges and their
/// pricing records, and the lines of receipts and invoices already issued (owner: documents too; gecko_revenue 43 lets
/// the app update a receipt line's container_no only). Idempotent through the inbox.
/// </summary>
internal sealed class ContainerRenumberedHandler(RevenueDbContext db) : IOutboxHandler
{
    public const string ContainerRenumbered = "ContainerRenumbered";

    public bool CanHandle(string messageType) => messageType == ContainerRenumbered;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        if (await Inbox.AlreadyHandledAsync(db, message, ct)) return;

        var renumber = JsonSerializer.Deserialize<RenumberedPayload>(message.PayloadJson, Inbox.Json)
                       ?? throw new InvalidOperationException($"Outbox message {message.MessageId} carries no renumber.");
        var (from, to) = (renumber.OldContainerNo, renumber.NewContainerNo);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var moved =
            await db.BookingPlanContainers.Where(x => x.ContainerNo == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContainerNo, to), ct)
            + await db.ContainerStays.Where(x => x.ContainerNo == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContainerNo, to), ct)
            + await db.ReeferSessions.Where(x => x.ContainerNo == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContainerNo, to), ct)
            + await db.Charges.Where(x => x.ContainerNo == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContainerNo, to), ct)
            + await db.MovementPricings.Where(x => x.ContainerNo == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContainerNo, to), ct)
            + await db.ReceiptLines.Where(x => x.ContainerNo == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContainerNo, to), ct)
            + await db.InvoiceLines.Where(x => x.ContainerNo == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContainerNo, to), ct);

        Inbox.Record(db, message, note: $"{from} -> {to}: {moved} row(s) moved.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private sealed record RenumberedPayload(string OldContainerNo, string NewContainerNo, string? Reason);
}
