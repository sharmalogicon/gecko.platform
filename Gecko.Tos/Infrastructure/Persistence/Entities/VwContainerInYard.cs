using System;
using System.Collections.Generic;

namespace Gecko.Tos.Infrastructure.Persistence.Entities;

public partial class VwContainerInYard
{
    public Guid TenantId { get; set; }

    public Guid ContainerVisitId { get; set; }

    public Guid BranchId { get; set; }

    public string ContainerNo { get; set; } = null!;

    public string? EquipmentTypeCode { get; set; }

    public Guid LinePartyId { get; set; }

    public string LinePartyCode { get; set; } = null!;

    public string FullEmpty { get; set; } = null!;

    public string? ConditionCode { get; set; }

    public string? GradeCode { get; set; }

    public Guid? YardId { get; set; }

    public Guid? YardSlotId { get; set; }

    public string? PositionText { get; set; }

    public Guid? CurrentBookingContainerId { get; set; }

    public DateTimeOffset GateInAt { get; set; }

    public string GateInEirNo { get; set; } = null!;

    public string GateInMovementCode { get; set; } = null!;

    public int? DaysInYard { get; set; }

    public DateTimeOffset LastEventAt { get; set; }

    public bool? IsHeld { get; set; }
}
