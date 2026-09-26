using Gecko.Notification.Domain.Enums;

namespace Gecko.Notification.Domain.Entities.Lookup;

/// <summary>
/// lookup.channel_registry — System-level channel definitions.
/// Adding a new channel = INSERT 1 row + deploy 1 Azure Function.
/// </summary>
public class ChannelRegistry
{
    public int Id { get; set; }
    public string ChannelCode { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public ChannelCategory Category { get; set; }
    public ChannelTier Tier { get; set; }
    public string? Region { get; set; }
    public string ProviderName { get; set; } = null!;
    public string DispatcherType { get; set; } = null!;
    public string? CredentialSchema { get; set; }
    public string? IconUrl { get; set; }
    public int MaxRatePerSecond { get; set; }
    public bool SupportsFallback { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
