using Gecko.SharedKernel;
using Gecko.Notification.Domain.Enums;

namespace Gecko.Notification.Domain.Entities.Template;

/// <summary>
/// template.notification_template — Versioned Scriban templates.
/// Scoped per: Tenant + EventType + Channel + Locale.
/// Only ONE version can be ACTIVE per scope at a time.
/// </summary>
public class NotificationTemplate : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public int EventTypeId { get; set; }
    public int ChannelId { get; set; }
    public string Locale { get; set; } = null!;
    public int Version { get; set; }
    public TemplateStatus Status { get; set; }
    public string Name { get; set; } = null!;
    public string? SubjectTemplate { get; set; }
    public string BodyTemplate { get; set; } = null!;
    public string? SampleDataJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
}
