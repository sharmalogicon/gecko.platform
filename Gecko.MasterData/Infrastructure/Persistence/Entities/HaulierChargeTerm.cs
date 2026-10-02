using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class HaulierChargeTerm
{
    public Guid HaulierChargeTermId { get; set; }

    public Guid TenantId { get; set; }

    public Guid HaulierPartyId { get; set; }

    public string HaulierPartyCode { get; set; } = null!;

    public Guid OrderTypeId { get; set; }

    public string OrderTypeCode { get; set; } = null!;

    public Guid MovementId { get; set; }

    public string MovementCode { get; set; } = null!;

    public Guid ChargeCodeId { get; set; }

    public string ChargeCode { get; set; } = null!;

    public string PaymentTermCode { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
