namespace Gecko.Notification.Domain.Enums;

public enum NotificationStatus
{
    CREATED,
    QUEUED,
    PROCESSING,
    SENT,
    DELIVERED,
    READ,
    FAILED,
    RETRY,
    BOUNCED,
    DLQ
}
