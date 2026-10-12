namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

/// <summary>gecko_master 35: one container number corrected — who, when, why, from and to. Written once, never changed.</summary>
public partial class ContainerRenumber
{
    public Guid ContainerRenumberId { get; set; }

    public Guid TenantId { get; set; }

    public Guid ContainerId { get; set; }

    public string OldContainerNo { get; set; } = null!;

    public string NewContainerNo { get; set; } = null!;

    public string Reason { get; set; } = null!;

    public DateTimeOffset RenumberedAt { get; set; }

    public Guid? RenumberedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }
}
