namespace Gecko.Revenue.Endpoints.Window;

// ── the quote ───────────────────────────────────────────────────────────────

/// <param name="PaidUntil">The storage date the quote was made to (null when no box carries storage).</param>
/// <param name="Today">The depot's calendar day now (branch time zone) — the earliest "paid until" the window accepts.</param>
/// <param name="VoidedReceipts">This booking's voided receipts that no receipt replaces yet — the ones a new payment may name.</param>
public sealed record WindowBookingResponse(
    Guid BookingId, Guid BranchId, string OrderNo, string Status, string OrderTypeCode,
    string? CustomerCode, string? AgentCode, string? LineCode, DateOnly? PaidUntil, DateOnly Today,
    IReadOnlyList<WindowBoxResponse> Boxes, decimal Subtotal, decimal Tax, decimal Total, string? CurrencyCode,
    IReadOnlyList<VoidedReceiptResponse>? VoidedReceipts = null);

public sealed record VoidedReceiptResponse(Guid ReceiptId, string ReceiptNo, DateTimeOffset? VoidedAt, string? VoidReason, decimal Total);

/// <param name="Note">Why nothing is due, when nothing is (not billable, nothing pending, already paid…).</param>
public sealed record WindowBoxResponse(
    Guid BookingContainerId, string? ContainerNo, string? EquipmentTypeCode,
    string? NextMovementCode, string? Direction, bool IsBillable,
    DateTimeOffset? InAt, int? StayDays,
    IReadOnlyList<QuoteLineResponse> Due, IReadOnlyList<SettledChargeResponse> Settled,
    IReadOnlyList<TriedVariantResponse> Tried, decimal Total, string? Note);

public sealed record QuoteLineResponse(
    string Kind, string ChargeCode, string ChargeName, string BillTo, string? PayerPartyCode,
    decimal Quantity, decimal? UnitRate, decimal Amount, string? TaxCode, decimal TaxRate, decimal TaxAmount, decimal Total,
    DateOnly? ServiceFrom, DateOnly? ServiceTo, string? ScheduleNo);

public sealed record SettledChargeResponse(
    Guid ChargeId, string ChargeCode, string Status, decimal Total, string? CouponRef, DateOnly? ServiceTo, string? WaiveReason);

public sealed record TriedVariantResponse(string ChargeCode, string BillTo, string Outcome, decimal? Amount);

// ── the drawer ──────────────────────────────────────────────────────────────

public sealed record OpenShiftRequest(Guid BranchId, decimal OpeningFloat, string? CurrencyCode);

public sealed record CloseShiftRequest(IReadOnlyList<ShiftCountRequest> Counts, string? Note);

public sealed record ShiftCountRequest(string Channel, decimal CountedAmount);

public sealed record ShiftResponse(
    Guid ShiftId, Guid BranchId, Guid CashierUserId, string CurrencyCode, DateTimeOffset OpenedAt, decimal OpeningFloat,
    string Status, DateTimeOffset? ClosedAt, int Receipts,
    IReadOnlyList<ChannelAmount> Expected, IReadOnlyList<ShiftCountResponse> Counts);

public sealed record ChannelAmount(string Channel, decimal Amount);

public sealed record ShiftCountResponse(string Channel, decimal Expected, decimal Counted, decimal Variance);

/// <summary>One line of "receipts this shift" — enough to find it and reprint it.</summary>
public sealed record ShiftReceiptResponse(
    Guid ReceiptId, string ReceiptNo, DateTimeOffset ReceiptAt, string? OrderNo, string PayerName,
    decimal Total, string CurrencyCode, string Status);

// ── the receipt ─────────────────────────────────────────────────────────────

/// <param name="BookingContainerIds">The boxes being paid for; each is charged for its NEXT billable movement.</param>
/// <param name="PaidUntil">For a gate-out: the last day of storage paid for (defaults to today). The coupon expires at the end of it.</param>
/// <param name="ExpectedTotal">The total the cashier saw. Prices are recalculated here; a different answer is refused, never silently charged.</param>
/// <param name="ReplacesReceiptId">Paying again after a void: the VOIDED receipt of the same booking this one replaces (printed on it). Each is replaced once.</param>
public sealed record CreateReceiptRequest(
    Guid BookingId, IReadOnlyList<Guid> BookingContainerIds, DateOnly? PaidUntil,
    PayerRequest? Payer, IReadOnlyList<PaymentRequest> Payments, decimal ExpectedTotal, Guid? ReplacesReceiptId = null);

/// <summary>Why the receipt is wrong — kept on it and printed with VOID.</summary>
public sealed record VoidReceiptRequest(string Reason);

/// <summary>What the tax invoice prints for the buyer. Defaults to the booking's customer.</summary>
public sealed record PayerRequest(string? Name, string? TaxId, string? BranchNo, string? Address);

/// <param name="TenderedAmount">CASH only: what was handed over; the change is worked out.</param>
public sealed record PaymentRequest(string Channel, decimal Amount, decimal? TenderedAmount, string? ReferenceNo, string? BankName);

/// <param name="ReceiptAt">The instant, expressed in the depot's own offset (+07:00 in Laem Chabang).</param>
/// <param name="Seller">The invoicing company of the branch (MDM). Null when the branch has none recorded.</param>
public sealed record ReceiptResponse(
    Guid ReceiptId, string ReceiptNo, DateTimeOffset ReceiptAt, string Status, string OrderNo,
    string PayerName, string? PayerTaxId, decimal Subtotal, decimal Tax, decimal Total, decimal Change, string CurrencyCode,
    IReadOnlyList<ReceiptLineResponse> Lines, IReadOnlyList<ReceiptPaymentResponse> Payments,
    IReadOnlyList<CouponResponse> Coupons,
    Guid BranchId, string? BranchCode, string? PayerBranchNo, string? PayerAddress,
    Guid ShiftId, Guid CashierUserId, SellerResponse? Seller,
    DateTimeOffset? VoidedAt, string? VoidReason,
    string? ReplacesReceiptNo = null, string? ReplacedByReceiptNo = null);

/// <summary>
/// The seller block of the tax invoice, exactly as MDM holds it. A null field is
/// one MDM does not have (a new tenant with no tax id yet) — printed as "not set",
/// never filled in.
/// </summary>
/// <param name="TaxBranchNo">"00000" = head office; the receipt prints สำนักงานใหญ่ / สาขาที่ nnnnn.</param>
public sealed record SellerResponse(
    string CompanyCode, string NameEn, string? NameLocal, string? TaxId, string? TaxBranchNo, bool? IsHeadOffice,
    string? Address, string? Phone, string? Email);

/// <param name="BillingUnitCode">From the charge's price snapshot: PER_CONTAINER, PER_DAY…</param>
public sealed record ReceiptLineResponse(
    short LineNo, string ChargeCode, string Description, string? ContainerNo, string? MovementCode,
    decimal Quantity, decimal? UnitRate, decimal Amount, decimal TaxRate, decimal TaxAmount,
    string? BillingUnitCode = null, DateOnly? ServiceFrom = null, DateOnly? ServiceTo = null);

public sealed record ReceiptPaymentResponse(string Channel, decimal Amount, decimal? Tendered, decimal? Change, string? ReferenceNo, string? BankName = null);

/// <param name="ValidUntil">Known when the coupon is issued; not kept by Revenue afterwards (TOS holds the coupon).</param>
public sealed record CouponResponse(string CouponRef, string? ContainerNo, string MovementCode, DateTimeOffset? ValidUntil);

// ── waiving ─────────────────────────────────────────────────────────────────

public sealed record WaiveRequest(Guid BookingContainerId, string ChargeCode, string BillTo, DateOnly? PaidUntil, string Reason);

/// <param name="Coupon">Set when the waiver left nothing to pay — the box is released without a receipt.</param>
public sealed record WaiveResponse(Guid ChargeId, string ChargeCode, decimal Total, CouponResponse? Coupon);
