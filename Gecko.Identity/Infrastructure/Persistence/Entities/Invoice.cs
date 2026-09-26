using System;
using System.Collections.Generic;

namespace Gecko.Identity.Infrastructure.Persistence.Entities;

public partial class Invoice
{
    public Guid InvoiceId { get; set; }

    public Guid TenantId { get; set; }

    public string InvoiceNumber { get; set; } = null!;

    public DateOnly PeriodStart { get; set; }

    public DateOnly PeriodEnd { get; set; }

    public decimal Subtotal { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public string Currency { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateTimeOffset? IssuedAt { get; set; }

    public DateTimeOffset? DueAt { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    public string? PaymentReference { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}
