using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class MovementPricing
{
    public Guid MovementPricingId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public string Clock { get; set; } = null!;

    public Guid? BookingId { get; set; }

    public Guid? BookingContainerId { get; set; }

    public string? ContainerNo { get; set; }

    public string MovementCode { get; set; } = null!;

    public Guid? GateTransactionId { get; set; }

    public string? OrderTypeCode { get; set; }

    public int VariantsTried { get; set; }

    public int VariantsPriced { get; set; }

    public decimal TotalAmount { get; set; }

    public string? CurrencyCode { get; set; }

    public bool? IsNoCharge { get; set; }

    public string TrailJson { get; set; } = null!;

    public long? SourceMessageId { get; set; }

    public DateTimeOffset PricedAt { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public Guid? ReviewedBy { get; set; }

    public string? ReviewNote { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
