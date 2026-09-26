using Gecko.SharedKernel;
using Gecko.Notification.Domain.Enums;

namespace Gecko.Notification.Domain.Entities.Config;

/// <summary>
/// config.tenant_subscription — Tenant's identity in this module.
/// Controls plan (FREE/ENTERPRISE) which gates channel access.
/// </summary>
public class TenantSubscription : ITenantScoped
{
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = null!;
    public SubscriptionPlan Plan { get; set; }
    public long MaxNotificationsPerMonth { get; set; }
    public int MaxTemplates { get; set; }
    public bool IsActive { get; set; }
    public DateTime ActivatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string Timezone { get; set; } = null!;
    public string DefaultLocale { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
