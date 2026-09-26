using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class MovementPlan
{
    public Guid MovementPlanId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BookingContainerId { get; set; }

    public short SequenceNo { get; set; }

    public Guid MovementId { get; set; }

    public string MovementCode { get; set; } = null!;

    public Guid OrderTypeMovementId { get; set; }

    public bool IsRequired { get; set; }

    public string Status { get; set; } = null!;

    public Guid? GateTransactionId { get; set; }

    public DateTimeOffset? SkippedAt { get; set; }

    public Guid? SkippedBy { get; set; }

    public string? SkipReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
