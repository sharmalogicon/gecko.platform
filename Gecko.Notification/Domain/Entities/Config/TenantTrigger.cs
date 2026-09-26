using Gecko.SharedKernel;
using Gecko.Notification.Domain.Enums;

namespace Gecko.Notification.Domain.Entities.Config;

/// <summary>
/// config.tenant_trigger — Which events fire notifications for this tenant.
/// Tenant can override default priority per trigger.
/// </summary>
public class TenantTrigger : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public int EventTypeId { get; set; }
    public bool IsActive { get; set; }
    public Priority Priority { get; set; }
    public string? ConditionsJson { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
}
