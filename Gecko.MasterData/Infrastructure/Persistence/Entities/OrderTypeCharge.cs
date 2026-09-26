using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class OrderTypeCharge
{
    public Guid OrderTypeChargeId { get; set; }

    public Guid TenantId { get; set; }

    public Guid OrderTypeId { get; set; }

    public Guid ChargeCodeId { get; set; }

    public Guid? MovementId { get; set; }

    public string PaymentTo { get; set; } = null!;

    public string? PaymentTermCode { get; set; }

    public string? TransportMode { get; set; }

    public bool IsDefault { get; set; }

    public bool IsOptional { get; set; }

    public bool IsCargoCharge { get; set; }

    public bool IsValueAddedService { get; set; }

    public bool RaiseAtGateIn { get; set; }

    public decimal? DefaultQty { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
