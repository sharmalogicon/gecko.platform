using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class OrderType
{
    public Guid OrderTypeId { get; set; }

    public Guid TenantId { get; set; }

    public string OrderTypeCode { get; set; } = null!;

    public string DescriptionEn { get; set; } = null!;

    public string? DescriptionLocal { get; set; }

    public string DirectionCode { get; set; } = null!;

    public Guid? ServiceTypeId { get; set; }

    public string CargoClassCode { get; set; } = null!;

    public string? BookingTypeCode { get; set; }

    /// <summary>gecko_master 27: a booking of this order type is tied to a vessel (call, ports validated).</summary>
    public bool RequiresVesselSchedule { get; set; }

    /// <summary>gecko_master 34 (R1): a truck may come with no paperwork; the gate raises one booking of this order type for it.</summary>
    public bool IsWalkIn { get; set; }

    /// <summary>gecko_master 34 (R2): CASH_ONLY, CREDIT_FOR_CREDIT_CUSTOMERS or CREDIT_WITHIN_LIMIT; null = the charge rows decide.</summary>
    public string? PaymentRule { get; set; }

    /// <summary>gecko_master 34 (R3): comma list of the grades a pick-up may take; null = any.</summary>
    public string? ReleaseGradeCodes { get; set; }

    /// <summary>gecko_master 34 (R4): REFUSE or ALLOW a box of another owner; null = the tenant setting.</summary>
    public string? OwnerMatch { get; set; }

    /// <summary>gecko_master 34 (R5): walk-in boxes of this order type on one truck; null = no cap.</summary>
    public short? MaxBoxesPerTruck { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
