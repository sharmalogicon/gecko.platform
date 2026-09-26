using Gecko.SharedKernel;

namespace Gecko.Notification.Domain.Entities.Audit;

/// <summary>
/// audit.log — Immutable audit trail for all configuration changes.
/// Append-only: no updates, no deletes.
/// Retention: 90 days hot, 1 year warm, 7 years cold.
/// </summary>
public class AuditLog : ITenantScoped
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public string Action { get; set; } = null!;
    public string EntityType { get; set; } = null!;
    public string EntityId { get; set; } = null!;
    public Guid ActorId { get; set; }
    public string? ActorEmail { get; set; }
    public string? OldValueJson { get; set; }
    public string? NewValueJson { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public Guid? CorrelationId { get; set; }
    public DateTime CreatedAt { get; set; }
}
