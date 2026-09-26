using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class BookingPlanContainer
{
    public Guid BookingContainerId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BookingId { get; set; }

    public Guid? EquipmentRequirementId { get; set; }

    public string? ContainerNo { get; set; }

    public string? EquipmentTypeCode { get; set; }

    public bool IsDangerousGoods { get; set; }

    public bool IsReefer { get; set; }

    public decimal? DeclaredGrossWeightKg { get; set; }

    public string? EndReason { get; set; }

    public string StepsJson { get; set; } = null!;

    public bool IsCurrent { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
