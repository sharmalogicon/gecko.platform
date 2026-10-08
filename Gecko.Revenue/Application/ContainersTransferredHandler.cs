using System.Text.Json;
using Gecko.Data;
using Gecko.Revenue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Application;

/// <summary>
/// TOS <c>ContainersTransferred</c>: boxes moved to another booking take their statement with
/// them — every charge line of the box (quoted, paid, billed, invoiced: Vector's
/// Operation.ContainerTransfer moved BookingStatement and InvoiceDetail alike) and Revenue's copy
/// of the box. Arrives before the two bookings' BookingChanged, so the source's re-quote no
/// longer sees the box's lines and does not cancel them; the target's re-quote finds them.
/// </summary>
internal sealed class ContainersTransferredHandler(RevenueDbContext db, BranchCalendar calendar) : IOutboxHandler
{
    public const string ContainersTransferred = "ContainersTransferred";

    public bool CanHandle(string messageType) => messageType == ContainersTransferred;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        if (await Inbox.AlreadyHandledAsync(db, message, ct)) return;

        var moved = JsonSerializer.Deserialize<TransferredPayload>(message.PayloadJson, Inbox.Json)
                    ?? throw new InvalidOperationException($"Outbox message {message.MessageId} carries no transfer.");
        var now = calendar.Now;

        var charges = await db.Charges
            .Where(c => c.BookingId == moved.SourceBookingId && c.BookingContainerId != null && moved.BookingContainerIds.Contains(c.BookingContainerId.Value))
            .ToListAsync(ct);
        foreach (var c in charges)
        {
            c.BookingId = moved.TargetBookingId;
            c.OrderNo = moved.TargetOrderNo;
            c.UpdatedAt = now;
        }

        var boxes = await db.BookingPlanContainers.Where(b => moved.BookingContainerIds.Contains(b.BookingContainerId)).ToListAsync(ct);
        foreach (var b in boxes)
        {
            b.BookingId = moved.TargetBookingId;
            b.UpdatedAt = now;
        }

        Inbox.Record(db, message, note: $"{charges.Count} line(s) of {moved.BookingContainerIds.Count} box(es) moved to {moved.TargetOrderNo}.");
        await db.SaveChangesAsync(ct);
    }

    private sealed record TransferredPayload(Guid SourceBookingId, Guid TargetBookingId, string TargetOrderNo, List<Guid> BookingContainerIds);
}
