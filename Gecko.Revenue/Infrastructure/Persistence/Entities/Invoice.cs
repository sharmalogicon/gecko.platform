using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class Invoice
{
    public Guid InvoiceId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public string InvoiceNo { get; set; } = null!;

    public string InvoiceType { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string PaymentTermCode { get; set; } = null!;

    public string BillTo { get; set; } = null!;

    public string? PayerPartyCode { get; set; }

    public string? PayerName { get; set; }

    public string CurrencyCode { get; set; } = null!;

    public decimal SubtotalAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public string? Remarks { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    public Guid IssuedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual ICollection<InvoiceLine> InvoiceLines { get; set; } = new List<InvoiceLine>();
}
