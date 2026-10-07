using System;
using System.Collections.Generic;

namespace Gecko.Revenue.Infrastructure.Persistence.Entities;

public partial class Charge
{
    public Guid ChargeId { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public string Source { get; set; } = null!;

    public Guid? BookingId { get; set; }

    public string? OrderNo { get; set; }

    public Guid? BookingContainerId { get; set; }

    public string? ContainerNo { get; set; }

    public string? MovementCode { get; set; }

    public Guid? GateTransactionId { get; set; }

    public string? EirNo { get; set; }

    public Guid? ContainerStayId { get; set; }

    public string? BillingPeriod { get; set; }

    public DateOnly? ServiceFrom { get; set; }

    public DateOnly? ServiceTo { get; set; }

    public Guid? ChargeCodeId { get; set; }

    public string ChargeCode { get; set; } = null!;

    public string? ChargeName { get; set; }

    public string BillTo { get; set; } = null!;

    public string PaymentTermCode { get; set; } = null!;

    public string? PayerPartyCode { get; set; }

    public decimal Quantity { get; set; }

    public decimal? UnitRate { get; set; }

    public decimal Amount { get; set; }

    public string CurrencyCode { get; set; } = null!;

    public string? TaxCode { get; set; }

    public decimal TaxRate { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal? TotalAmount { get; set; }

    public DateOnly? PricedForDate { get; set; }

    public Guid? ScheduleId { get; set; }

    public string? ScheduleNo { get; set; }

    public short? ScheduleVersionNo { get; set; }

    public string? ScheduleType { get; set; }

    public byte? ScopeRank { get; set; }

    public Guid? TosRateId { get; set; }

    public string? RateRowVersion { get; set; }

    public int? Specificity { get; set; }

    public string? PricingMethod { get; set; }

    public string? BillingUnitCode { get; set; }

    public bool? PricesIncludeTax { get; set; }

    public decimal? BaseRate { get; set; }

    public decimal? FreeUnits { get; set; }

    public decimal? ChargeableQuantity { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public string? PriceSnapshotJson { get; set; }

    public string Status { get; set; } = null!;

    public Guid? ReceiptId { get; set; }

    public Guid? ReceiptLineId { get; set; }

    public string? CouponRef { get; set; }

    public Guid? EarnedGateTransactionId { get; set; }

    public DateTimeOffset? EarnedAt { get; set; }

    public Guid? InvoiceId { get; set; }

    public Guid? InvoiceLineId { get; set; }

    public DateTimeOffset? WaivedAt { get; set; }

    public Guid? WaivedBy { get; set; }

    public string? WaiveReason { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public Guid? CancelledBy { get; set; }

    public string? CancelReason { get; set; }

    public bool CreditNoteRequired { get; set; }

    public long? SourceMessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public Guid? TruckVisitId { get; set; }

    public bool IsTripCharge { get; set; }

    public decimal? UnitRateOriginal { get; set; }

    public bool IsRateOverridden { get; set; }

    public string? OverrideReason { get; set; }

    public Guid? OverriddenBy { get; set; }

    public DateTimeOffset? OverriddenAt { get; set; }

    public string? DiscountType { get; set; }

    public decimal? DiscountRate { get; set; }

    public string? WaiveReasonCode { get; set; }

    public bool IsLocked { get; set; }
}
