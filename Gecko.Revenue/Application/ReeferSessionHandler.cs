using System.Text.Json;
using Gecko.Data;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gecko.Revenue.Application;

/// <summary>
/// TOS reefer plug log → Revenue's copy (projection.reefer_session, gecko_revenue 17).
///
/// Contract (gecko_tos ReeferLog): aggregate REEFER_SESSION, aggregate id = the
/// session; every payload is the session's FULL current state, so a message
/// replaces the row. The newest TOS change wins — a message older than the
/// stored one (by changedAt, then message id) is recorded STALE and changes
/// nothing, so a late redelivery cannot rewind a correction. Once voided, a
/// session stays voided here: it is never billed.
///
/// On a gate-out TOS queues ReeferUnplugged BEFORE ContainerGatedOut, so the
/// projection is complete by the time the gate event earns the window charges.
/// </summary>
internal sealed class ReeferSessionHandler(RevenueDbContext db, TimeProvider clock, ILogger<ReeferSessionHandler> log) : IOutboxHandler
{
    public const string AggregateType = "REEFER_SESSION";
    public const string Plugged = "ReeferPlugged";
    public const string Unplugged = "ReeferUnplugged";
    public const string Corrected = "ReeferSessionCorrected";
    public const string Voided = "ReeferSessionVoided";

    public bool CanHandle(string messageType) => messageType is Plugged or Unplugged or Corrected or Voided;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        if (await Inbox.AlreadyHandledAsync(db, message, ct)) return;

        var sent = JsonSerializer.Deserialize<SessionPayload>(message.PayloadJson, Inbox.Json)
                   ?? throw new InvalidOperationException($"Outbox message {message.MessageId} carries no reefer session.");
        if (sent.SessionId == Guid.Empty || sent.ContainerVisitId == Guid.Empty || string.IsNullOrWhiteSpace(sent.ContainerNo))
            throw new InvalidOperationException($"Outbox message {message.MessageId}: a reefer session needs its id, visit and container.");

        var now = clock.GetUtcNow();
        var row = await db.ReeferSessions.SingleOrDefaultAsync(s => s.SessionId == sent.SessionId, ct);

        if (row is not null && (sent.ChangedAt < row.LastChangedAt
                                || (sent.ChangedAt == row.LastChangedAt && message.MessageId <= row.LastMessageId)))
        {
            var stale = $"Session already at {row.LastMessageType} of {row.LastChangedAt:O} (message {row.LastMessageId}).";
            log.LogInformation("Reefer message {MessageId} ({Box}) is older than the stored session: {Note}", message.MessageId, row.ContainerNo, stale);
            Inbox.Record(db, message, "STALE", stale);
            await db.SaveChangesAsync(ct);
            return;
        }

        if (row is null)
        {
            row = new ReeferSession { SessionId = sent.SessionId, TenantId = message.TenantId, CreatedAt = now };
            db.ReeferSessions.Add(row);
        }

        var pluggedOut = sent.PluggedOutAt is { } o && o < sent.PluggedInAt ? sent.PluggedInAt : sent.PluggedOutAt;
        row.ContainerVisitId = sent.ContainerVisitId;
        row.InGateTransactionId = sent.GateInTransactionId;
        row.ContainerNo = sent.ContainerNo.Trim().ToUpperInvariant();
        row.BranchId = sent.BranchId;
        row.EquipmentTypeCode = sent.EquipmentTypeCode;
        row.PluggedInAt = sent.PluggedInAt;
        row.PluggedInBy = sent.PluggedInBy;
        row.PluggedOutAt = pluggedOut;
        row.PluggedOutBy = pluggedOut is null ? null : sent.PluggedOutBy;
        row.CloseReason = pluggedOut is null ? null : sent.CloseReason;
        row.CloseGateTransactionId = pluggedOut is null ? null : sent.CloseGateTransactionId;
        row.IsVoided = row.IsVoided || sent.IsVoided || message.MessageType == Voided;
        row.LastMessageId = message.MessageId;
        row.LastMessageType = message.MessageType;
        row.LastChangedAt = sent.ChangedAt;
        row.PayloadJson = message.PayloadJson;
        row.UpdatedAt = now;

        Inbox.Record(db, message);
        await db.SaveChangesAsync(ct);
    }

    private sealed record SessionPayload(
        Guid SessionId, Guid ContainerVisitId, Guid? GateInTransactionId, string ContainerNo, Guid BranchId,
        string? EquipmentTypeCode, DateTimeOffset PluggedInAt, Guid? PluggedInBy, DateTimeOffset? PluggedOutAt,
        Guid? PluggedOutBy, string? CloseReason, Guid? CloseGateTransactionId, bool IsVoided, DateTimeOffset ChangedAt);
}
