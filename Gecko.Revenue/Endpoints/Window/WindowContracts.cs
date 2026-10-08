namespace Gecko.Revenue.Endpoints.Window;

// ── the quote ───────────────────────────────────────────────────────────────

/// <param name="PaidUntil">The storage date the quote was made to (null when no box carries storage).</param>
/// <param name="Today">The depot's calendar day now (branch time zone) — the earliest "paid until" the window accepts.</param>
/// <param name="VoidedReceipts">This booking's voided receipts that no receipt replaces yet — the ones a new payment may name.</param>
/// <param name="Subtotal">CASH due now (before VAT). <c>Total</c> is what is paid at the window.</param>
/// <param name="TruckCategoryCode">The truck category the quote was priced with (given, or the tenant default gate.default_truck_category).</param>
/// <param name="HaulierCode">The haulier whose charge terms applied (given, or the booking's).</param>
/// <param name="BilledLater">Credit lines (native, or cash moved to credit by the haulier's term): shown, not paid here.</param>
public sealed record WindowBookingResponse(
    Guid BookingId, Guid BranchId, string OrderNo, string Status, string OrderTypeCode,
    string? CustomerCode, string? AgentCode, string? LineCode, DateOnly? PaidUntil, DateOnly Today,
    IReadOnlyList<WindowBoxResponse> Boxes, decimal Subtotal, decimal Tax, decimal Total, string? CurrencyCode,
    IReadOnlyList<VoidedReceiptResponse>? VoidedReceipts = null,
    string? TruckCategoryCode = null, string? HaulierCode = null, BilledLaterResponse? BilledLater = null,
    // "ZZKU1234565 on BK-…": a box of another booking on the same truck (sameTruckAs) carries the truck's gate charge.
    string? GateChargeCarriedBy = null,
    // Set when the clerk MAY apply withholding tax (cash total over 1,000): what it would be, and what is then paid.
    WithholdingTaxResponse? WithholdingTax = null);

/// <summary>
/// Withholding tax the clerk may choose to apply (Vector: offered when the cash total is over 1,000; 3% of the
/// amount before VAT). <c>Nett</c> = <c>Total</c> − <c>Amount</c>: what the payments must add up to when it is applied.
/// </summary>
public sealed record WithholdingTaxResponse(decimal Rate, decimal Amount, decimal Nett);

/// <summary>What the boxes will be billed on credit, priced now for the clerk to see. Nothing in it is paid at the window.</summary>
public sealed record BilledLaterResponse(decimal Subtotal, decimal Tax, decimal Total, string? CurrencyCode);

public sealed record VoidedReceiptResponse(Guid ReceiptId, string ReceiptNo, DateTimeOffset? VoidedAt, string? VoidReason, decimal Total);

/// <param name="Note">Why nothing is due, when nothing is (not billable, nothing pending, already paid…), or why it cannot be paid yet.</param>
/// <param name="NoPrice">Cash charges of this movement that no tariff prices (contract or standard), at 0. They are NOT
/// charged (owner 2026-10-07, as Vector): the receipt takes the priced lines; show these to the clerk as "no tariff, not charged".</param>
public sealed record WindowBoxResponse(
    Guid BookingContainerId, string? ContainerNo, string? EquipmentTypeCode,
    string? NextMovementCode, string? Direction, bool IsBillable,
    DateTimeOffset? InAt, int? StayDays,
    IReadOnlyList<QuoteLineResponse> Due, IReadOnlyList<SettledChargeResponse> Settled,
    IReadOnlyList<TriedVariantResponse> Tried, decimal Total, string? Note,
    IReadOnlyList<QuoteLineResponse>? BilledLater = null, bool VasOffered = false,
    IReadOnlyList<QuoteLineResponse>? NoPrice = null,
    IReadOnlyList<VasOptionResponse>? VasMenu = null);

/// <summary>
/// One gate VAS the box's next movement offers, priced as if ticked (the clerk's VAS panel; Vector LoadGateInVASCharges).
/// Tick it by sending its code in <c>&amp;vas=</c>; <see cref="Ticked"/> says it is in this quote's <c>due</c>.
/// </summary>
/// <param name="Outcome">PRICED, PRICED_ZERO (free under the tariff), UNPRICED (no tariff prices it: ticking it blocks the receipt) or NO_VARIANT.</param>
public sealed record VasOptionResponse(
    string ChargeCode, string ChargeName, string BillTo, string PaymentTermCode, bool Ticked, string Outcome,
    decimal? UnitRate, decimal? Amount, decimal? TaxRate, decimal? TaxAmount, decimal? Total, string? CurrencyCode, string? ScheduleNo);

/// <param name="Kind">MOVEMENT, VAS, STORAGE or REEFER.</param>
/// <param name="BillingUnitCode">PER_TRIP = the once-per-truck gate charge.</param>
/// <param name="ByHaulierTerm">A cash line the haulier's charge term moved to credit.</param>
/// <param name="RateSource">TARIFF, or OVERRIDE: a supervisor's rate set on the booking statement, kept on every re-quote.</param>
/// <param name="UnitRateOriginal">With OVERRIDE: the tariff's rate it replaced.</param>
public sealed record QuoteLineResponse(
    string Kind, string ChargeCode, string ChargeName, string BillTo, string? PayerPartyCode,
    decimal Quantity, decimal? UnitRate, decimal Amount, string? TaxCode, decimal TaxRate, decimal TaxAmount, decimal Total,
    DateOnly? ServiceFrom, DateOnly? ServiceTo, string? ScheduleNo,
    string PaymentTermCode = "CASH", string? BillingUnitCode = null, bool ByHaulierTerm = false,
    string RateSource = "TARIFF", decimal? UnitRateOriginal = null, string? OverrideReason = null);

public sealed record SettledChargeResponse(
    Guid ChargeId, string ChargeCode, string Status, decimal Total, string? CouponRef, DateOnly? ServiceTo, string? WaiveReason);

/// <param name="Outcome">PRICED, UNPRICED, PRICED_ZERO, NO_VARIANT, SETTLED, PER_TRIP_ON_OTHER_BOX, GATE_CHARGE_ONLY, HAULIER_CREDIT.</param>
/// <param name="Note">Why, in a sentence, for the outcomes the gate rules decide (once per truck, gate charge only, haulier credit).</param>
public sealed record TriedVariantResponse(string ChargeCode, string BillTo, string Outcome, decimal? Amount,
    string PaymentTermCode = "CASH", string? Note = null);

// ── a truck visit, priced (gate-in-vector-parity §6.2) ──────────────────────

/// <summary>
/// What one truck visit was charged, per box and per charge line: the cash the
/// window took for the moves it made (<c>PaidNow</c>), and the credit the gate
/// event accrued (<c>BilledLater</c>). Vector's one-truck invoice plus its credit
/// statement lines, read from the charges Revenue already holds.
/// </summary>
public sealed record VisitQuoteResponse(
    Guid TruckVisitId, Guid BranchId, IReadOnlyList<VisitQuoteBoxResponse> Boxes,
    VisitMoneyResponse PaidNow, VisitMoneyResponse BilledLater, string? CurrencyCode);

/// <param name="Total">VAT-inclusive; <c>Amount</c> and <c>Vat</c> are kept apart (owner: VAT separate).</param>
public sealed record VisitMoneyResponse(decimal Amount, decimal Vat, decimal Total);

public sealed record VisitQuoteBoxResponse(
    string? ContainerNo, string? OrderNo, string? MovementCode, string? EirNo, Guid? GateTransactionId,
    IReadOnlyList<VisitQuoteLineResponse> Lines);

/// <param name="SellingAmount">Amount + VAT (Vector's SellingAmount is tax-inclusive).</param>
/// <param name="PaymentTerm">CASH (paid at the window, <c>ReceiptNo</c>) or CREDIT (billed later).</param>
/// <param name="PaymentTo">Who is billed: CUSTOMER in the pilot.</param>
/// <param name="IsGateCharge">The once-per-truck-visit gate charge (billing unit PER_TRIP).</param>
public sealed record VisitQuoteLineResponse(
    string ChargeCode, string? Description, decimal? SellRate, decimal Qty, decimal VatRate,
    decimal Amount, decimal TaxAmount, decimal SellingAmount,
    string PaymentTerm, string PaymentTo, string? PayerCode, string Status,
    string? BillingUnitCode, bool IsGateCharge, string? ReceiptNo);

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
/// <param name="BookingContainerIds">The boxes going on ONE truck: its PER_TRIP gate charge is charged once, on the first of them.</param>
/// <param name="TruckCategoryCode">As on the quote — the receipt is re-quoted with the same truck, haulier and VAS.</param>
/// <param name="SameTruckAs">As on the quote — order numbers of the truck's other bookings; one that carries the gate charge takes it off this receipt.</param>
public sealed record CreateReceiptRequest(
    Guid BookingId, IReadOnlyList<Guid> BookingContainerIds, DateOnly? PaidUntil,
    PayerRequest? Payer, IReadOnlyList<PaymentRequest> Payments, decimal ExpectedTotal, Guid? ReplacesReceiptId = null,
    string? TruckCategoryCode = null, string? HaulierCode = null, IReadOnlyList<string>? Vas = null,
    IReadOnlyList<string>? SameTruckAs = null,
    // The clerk applies withholding tax (only when the quote offers it): the payments then add up to the quote's withholdingTax.nett.
    bool WithholdingTax = false);

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
    string? ReplacesReceiptNo = null, string? ReplacedByReceiptNo = null,
    // Withholding tax the payer kept back (0 = none). Total is the tax invoice's; NettAmount is what was paid.
    decimal? WithholdingTaxRate = null, decimal WithholdingTaxAmount = 0, decimal NettAmount = 0,
    string? PayerPartyCode = null, string? Remarks = null);

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
