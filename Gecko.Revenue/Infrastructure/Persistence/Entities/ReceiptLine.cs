using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class ReceiptLine
{
    public Guid ReceiptLineId { get; set; }

    public Guid TenantId { get; set; }

    public Guid ReceiptId { get; set; }

    public short LineNo { get; set; }

    public Guid ChargeId { get; set; }

    public string ChargeCode { get; set; } = null!;

    public string Description { get; set; } = null!;

    public string? ContainerNo { get; set; }

    public string? MovementCode { get; set; }

    public decimal Quantity { get; set; }

    public decimal? UnitRate { get; set; }

    public decimal Amount { get; set; }

    public string? TaxCode { get; set; }

    public decimal TaxRate { get; set; }

    public decimal TaxAmount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
