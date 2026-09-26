using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class TosRate
{
    public Guid TosRateId { get; set; }

    public Guid TenantId { get; set; }

    public Guid ScheduleId { get; set; }

    public Guid ChargeCodeId { get; set; }

    public string ChargeCode { get; set; } = null!;

    public string BillTo { get; set; } = null!;

    public string PaymentTermCode { get; set; } = null!;

    public short? CreditTermDays { get; set; }

    public Guid? OrderTypeId { get; set; }

    public string? OrderTypeCode { get; set; }

    public Guid? MovementId { get; set; }

    public string? MovementCode { get; set; }

    public Guid? EquipmentTypeId { get; set; }

    public string? EquipmentTypeCode { get; set; }

    public string? EquipmentSize { get; set; }

    public string? CargoCategoryCode { get; set; }

    public string? TruckCategoryCode { get; set; }

    public string BillingUnitCode { get; set; } = null!;

    public string PricingMethod { get; set; } = null!;

    public string? TierBasis { get; set; }

    public decimal? Rate { get; set; }

    public short? Specificity { get; set; }

    public string? AxisSignature { get; set; }

    public string Source { get; set; } = null!;

    public Guid? ImportRowId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
