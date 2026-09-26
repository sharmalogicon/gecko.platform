using Gecko.Notification.Domain.Enums;

namespace Gecko.Notification.Domain.Entities.Core;

/// <summary>
/// core.delivery_log — Every delivery attempt for a notification.
/// A single notification may have multiple attempts (retry on failure).
/// </summary>
public class DeliveryLog
{
    public long Id { get; set; }
    public Guid NotificationId { get; set; }
    public int AttemptNumber { get; set; }
    public DeliveryLogStatus Status { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? ProviderResponse { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int? HttpStatusCode { get; set; }
    public int? DurationMs { get; set; }
    public string? DispatcherInstance { get; set; }
    public DateTime AttemptedAt { get; set; }
}
