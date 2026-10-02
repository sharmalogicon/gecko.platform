using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class Receipt
{
    public Guid ReceiptId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public string ReceiptNo { get; set; } = null!;

    public DateTimeOffset ReceiptAt { get; set; }

    public Guid ShiftId { get; set; }

    public Guid CashierUserId { get; set; }

    public Guid? BookingId { get; set; }

    public string? OrderNo { get; set; }

    public string? PayerPartyCode { get; set; }

    public string PayerName { get; set; } = null!;

    public string? PayerTaxId { get; set; }

    public string? PayerBranchNo { get; set; }

    public string? PayerAddress { get; set; }

    public string CurrencyCode { get; set; } = null!;

    public decimal SubtotalAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public string Status { get; set; } = null!;

    public DateTimeOffset? VoidedAt { get; set; }

    public Guid? VoidedBy { get; set; }

    public string? VoidReason { get; set; }

    public Guid? ReplacesReceiptId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    /// <summary>gecko_revenue 20: the withholding tax % the clerk applied; null = none.</summary>
    public decimal? WithholdingTaxRate { get; set; }

    /// <summary>gecko_revenue 20: what the payer withheld. The receipt's total is unchanged; the drawer received total − this.</summary>
    public decimal WithholdingTaxAmount { get; set; }
}
