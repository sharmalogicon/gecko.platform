using Gecko.SharedKernel;

namespace Gecko.Notification.Domain.Entities.Config;

/// <summary>
/// config.client_preference — End client opt-in/out, quiet hours, frequency caps.
/// </summary>
public class ClientPreference : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public string ClientType { get; set; } = null!;
    public int ChannelId { get; set; }
    public bool IsOptedIn { get; set; }
    public TimeOnly? QuietHoursStart { get; set; }
    public TimeOnly? QuietHoursEnd { get; set; }
    public string? QuietHoursTimezone { get; set; }
    public int? FrequencyCapPerHour { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
