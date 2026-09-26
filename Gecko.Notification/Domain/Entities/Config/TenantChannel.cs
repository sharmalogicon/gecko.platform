using Gecko.SharedKernel;

namespace Gecko.Notification.Domain.Entities.Config;

/// <summary>
/// config.tenant_channel — Which channels a tenant has enabled.
/// Credentials stored in Azure Key Vault; only vault URI persisted here.
/// </summary>
public class TenantChannel : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public int ChannelId { get; set; }
    public bool IsActive { get; set; }
    public string? VaultSecretUri { get; set; }
    public string? ConfigJson { get; set; }
    public bool ConnectionVerified { get; set; }
    public DateTime? LastVerifiedAt { get; set; }
    public int? FallbackChannelId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
}
