using Gecko.SharedKernel;
using Gecko.Notification.Domain.Enums;

namespace Gecko.Notification.Domain.Entities.Outbox;

/// <summary>
/// outbox.message — Transactional outbox for reliable Service Bus publishing.
/// Written in same SQL transaction as core.notification.
/// OutboxProcessor polls and publishes to Service Bus.
/// </summary>
public class OutboxMessage : ITenantScoped
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public string MessageType { get; set; } = null!;
    public string Destination { get; set; } = null!;
    public string Payload { get; set; } = null!;
    public string? HeadersJson { get; set; }
    public OutboxMessageStatus Status { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public DateTime? NextRetryAt { get; set; }
}
