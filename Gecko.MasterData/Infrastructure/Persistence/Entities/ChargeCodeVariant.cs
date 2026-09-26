using System;
using System.Collections.Generic;

namespace Gecko.MasterData.Infrastructure.Persistence.Entities;

public partial class ChargeCodeVariant
{
    public Guid ChargeCodeVariantId { get; set; }

    public Guid TenantId { get; set; }

    public Guid ChargeCodeId { get; set; }

    public string BillTo { get; set; } = null!;

    public string PaymentTermCode { get; set; } = null!;

    public Guid? TaxCodeId { get; set; }

    public Guid? WithholdingTaxCodeId { get; set; }

    public short? CreditTermDays { get; set; }

    public string? RevenueGl { get; set; }

    public string? CostGl { get; set; }

    public string? LegacyChargeCode { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
