using Gecko.SharedKernel;
using Gecko.Notification.Domain.Enums;

namespace Gecko.Notification.Domain.Entities.Core;

/// <summary>
/// core.notification_event — Append-only event store.
/// Every domain event from any GECKO module is recorded here first.
/// </summary>
public class NotificationEvent : ITenantScoped
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid EventId { get; set; }
    public string EventTypeCode { get; set; } = null!;
    public SourceModule SourceModule { get; set; }
    public string EventDataJson { get; set; } = null!;
    public Guid? CorrelationId { get; set; }
    public DateTime ReceivedAt { get; set; }
}
