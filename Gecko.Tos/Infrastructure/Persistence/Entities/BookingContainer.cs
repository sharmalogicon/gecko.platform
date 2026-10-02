using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class BookingContainer
{
    public Guid BookingContainerId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BookingId { get; set; }

    public Guid EquipmentRequirementId { get; set; }

    public string ContainerNo { get; set; } = null!;

    public Guid? ContainerId { get; set; }

    public bool IsCheckDigitValid { get; set; }

    public DateTimeOffset AssignedAt { get; set; }

    public Guid? AssignedBy { get; set; }

    public string AssignmentSource { get; set; } = null!;

    public string? DeclaredSealNo { get; set; }

    public decimal? DeclaredVgmKg { get; set; }

    /// <summary>gecko_tos 18: Vector's P/U Mode / D/O Mode / repo mode — who collects or delivers the box. Null = not said.</summary>
    public string? HandoverModeCode { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public Guid? EndedBy { get; set; }

    public string? EndReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
