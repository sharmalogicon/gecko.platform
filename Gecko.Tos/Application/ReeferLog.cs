using Gecko.Tos.Infrastructure.Persistence;
using Gecko.Tos.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Application;

/// <summary>
/// The reefer plug log's outbox contract and the two places the GATE touches it
/// (TIER3_DESIGN_NOTES §6): a gate-out closes an open session in the same §5.5
/// transaction, and a void of that gate-out re-opens exactly the sessions it closed.
///
/// OUTBOX CONTRACT — aggregate_type REEFER_SESSION, aggregate_id = the session id,
/// message_type ReeferPlugged | ReeferUnplugged | ReeferSessionCorrected | ReeferSessionVoided.
/// Every payload is the session's FULL current state (see <see cref="EnqueueAsync"/>),
/// so a consumer never has to merge deltas: the latest message by message_id wins.
/// </summary>
internal static class ReeferLog
{
    public const string AggregateType = "REEFER_SESSION";
    public const string Plugged = "ReeferPlugged";
    public const string Unplugged = "ReeferUnplugged";
    public const string Corrected = "ReeferSessionCorrected";
    public const string Voided = "ReeferSessionVoided";

    public const string ManualClose = "MANUAL";
    public const string GateOutClose = "GATE_OUT";

    public static Task EnqueueAsync(TosDbContext db, ReeferPowerSession s, ContainerVisit visit, string messageType,
        DateTimeOffset changedAt, CancellationToken ct) =>
        TosOutbox.EnqueueAsync(db, s.TenantId, AggregateType, s.ReeferPowerSessionId, messageType, new
        {
            sessionId = s.ReeferPowerSessionId,
            containerVisitId = s.ContainerVisitId,
            // Revenue's projection.container_stay is keyed by the gate-in transaction.
            gateInTransactionId = visit.GateInTransactionId,
            containerNo = s.ContainerNo,
            branchId = s.BranchId,
            equipmentTypeCode = visit.EquipmentTypeCode,
            pluggedInAt = s.PluggedInAt,
            pluggedInBy = s.PluggedInBy,
            pluggedOutAt = s.PluggedOutAt,
            pluggedOutBy = s.PluggedOutBy,
            closeReason = s.CloseReason,
            closeGateTransactionId = s.CloseGateTransactionId,
            isVoided = messageType == Voided,
            changedAt,
        }, ct);

    /// <summary>
    /// Gate-out: every open session of the visit ends at the gate time (never before
    /// it began), by the gate user, with the gate transaction that did it. Queued
    /// BEFORE the caller queues ContainerGatedOut, so Revenue sees Unplugged first.
    /// </summary>
    public static async Task CloseOnGateOutAsync(TosDbContext db, ContainerVisit visit, Guid gateTransactionId,
        DateTimeOffset at, Guid by, DateTimeOffset now, CancellationToken ct)
    {
        var open = await db.ReeferPowerSessions
            .Where(s => s.ContainerVisitId == visit.ContainerVisitId && s.PluggedOutAt == null).ToListAsync(ct);
        foreach (var s in open)
        {
            s.PluggedOutAt = at < s.PluggedInAt ? s.PluggedInAt : at;
            s.PluggedOutBy = by;
            s.CloseReason = GateOutClose;
            s.CloseGateTransactionId = gateTransactionId;
            VisitJournal.Add(db, visit, "PLUG_OUT", s.PlugPointCode, GateOutClose, s.PluggedOutAt.Value, by, s.ReeferPowerSessionId);
            await EnqueueAsync(db, s, visit, Unplugged, now, ct);
        }
    }

    /// <summary>
    /// Void of a gate-out: the box is back in the yard, still plugged in. Only the
    /// sessions THAT transaction closed re-open, and never into a second open one.
    /// Returns the re-opened sessions; the caller queues them after saving.
    /// </summary>
    public static async Task<IReadOnlyList<ReeferPowerSession>> ReopenOnVoidAsync(TosDbContext db, Guid gateTransactionId, CancellationToken ct)
    {
        var closed = await db.ReeferPowerSessions.Where(s => s.CloseGateTransactionId == gateTransactionId).ToListAsync(ct);
        var reopened = new List<ReeferPowerSession>();
        foreach (var s in closed)
        {
            var another = await db.ReeferPowerSessions.AnyAsync(o =>
                o.ContainerVisitId == s.ContainerVisitId && o.PluggedOutAt == null && o.ReeferPowerSessionId != s.ReeferPowerSessionId, ct);
            if (another || reopened.Any(r => r.ContainerVisitId == s.ContainerVisitId)) continue;

            s.PluggedOutAt = null;
            s.PluggedOutBy = null;
            s.CloseReason = null;
            s.CloseGateTransactionId = null;
            reopened.Add(s);
        }
        return reopened;
    }
}
