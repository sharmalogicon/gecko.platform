using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class OrderTypeMovement
{
    public Guid OrderTypeMovementId { get; set; }

    public Guid TenantId { get; set; }

    public Guid OrderTypeId { get; set; }

    public Guid MovementId { get; set; }

    public short SequenceNo { get; set; }

    public bool IsRequired { get; set; }

    public bool IsBillable { get; set; }

    public bool CheckSealNo { get; set; }

    public bool CheckGrossWeight { get; set; }

    public bool RequireVesselVoyage { get; set; }

    public bool AllowDamagedRelease { get; set; }

    public bool SkipEdi { get; set; }

    /// <summary>gecko_master 34 (R6): the gate-in needs a damage survey of the box on the same truck visit.</summary>
    public bool RequireSurvey { get; set; }

    /// <summary>gecko_master 34 (R12): the seal keyed at the gate must be one of the box's booked seals.</summary>
    public bool SealMustMatch { get; set; }

    public string? PudoMode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
