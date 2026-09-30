using System.Text.Json;
using Gecko.Tos.Infrastructure.Persistence;
using Gecko.Tos.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Application;

/// <summary>
/// The two writes every yard event makes besides its own row: the outbox message
/// and the visit journal. Shared by the gate and the reefer log so both queue the
/// same way — raw SQL, in the caller's transaction (06_outbox: gecko_app may
/// INSERT into the outbox and may not read it, so it is not an EF entity).
/// </summary>
internal static class TosOutbox
{
    /// <summary>Queued immediately (message_id is an IDENTITY, so the order of calls is the order Revenue sees).</summary>
    public static async Task EnqueueAsync(TosDbContext db, Guid tenantId, string aggregateType, Guid aggregateId,
        string messageType, object payload, CancellationToken ct) =>
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO outbox.message (tenant_id, aggregate_type, aggregate_id, message_type, payload_json) " +
            "VALUES ({0}, {1}, {2}, {3}, {4})",
            [tenantId, aggregateType, aggregateId, messageType, JsonSerializer.Serialize(payload)],
            ct);
}

internal static class VisitJournal
{
    public static void Add(TosDbContext db, ContainerVisit visit, string type, string? from, string? to,
        DateTimeOffset at, Guid? by, Guid? reference, string? remarks = null) =>
        db.VisitEvents.Add(new VisitEvent
        {
            TenantId = visit.TenantId,
            ContainerVisitId = visit.ContainerVisitId,
            EventType = type,
            FromValue = from,
            ToValue = to,
            EventAt = at,
            EventBy = by,
            ReferenceId = reference,
            Remarks = remarks,
        });
}
