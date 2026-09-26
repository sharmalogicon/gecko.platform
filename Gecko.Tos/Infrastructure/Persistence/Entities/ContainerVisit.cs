using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class ContainerVisit
{
    public Guid ContainerVisitId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public string ContainerNo { get; set; } = null!;

    public Guid? ContainerId { get; set; }

    public Guid? EquipmentTypeId { get; set; }

    public string? EquipmentTypeCode { get; set; }

    public Guid LinePartyId { get; set; }

    public string LinePartyCode { get; set; } = null!;

    public Guid GateInTransactionId { get; set; }

    public Guid? GateOutTransactionId { get; set; }

    public string FullEmpty { get; set; } = null!;

    public string? ConditionCode { get; set; }

    public string? GradeCode { get; set; }

    public Guid? YardId { get; set; }

    public Guid? YardSlotId { get; set; }

    public string? PositionText { get; set; }

    public Guid? CurrentBookingContainerId { get; set; }

    public DateTimeOffset LastEventAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
