using System.ComponentModel.DataAnnotations;
using Gecko.Revenue.Contracts;

namespace Gecko.Tos.Endpoints.Gate;

// ── the barrier read ────────────────────────────────────────────────────────

/// <summary>
/// What the barrier knows before the boom lifts (PLAN §5.4). <c>Decision</c> is
/// ALLOWED, NEEDS_OVERRIDE or BLOCKED, and <c>Findings</c> always says why — a
/// refusal a clerk cannot explain to a driver is a refusal that gets worked around.
/// </summary>
public sealed record GatePreflightResponse(
    string ContainerNo, string Direction, DateTimeOffset At, string Decision,
    IReadOnlyList<GateFindingResponse> Findings,
    GateBookingResponse? Booking,
    GateStepResponse? NextStep,
    IReadOnlyList<GateHoldResponse> Holds,
    GateYardResponse? InYard,
    GateCutoffResponse? Cutoff,
    GateCouponResponse? Coupon,
    bool IsCheckDigitValid, bool IsInRegistry);

public sealed record GateFindingResponse(string Code, string Message, string Severity);

public sealed record GateBookingResponse(
    Guid BookingId, string OrderNo, Guid BranchId, string OrderTypeCode, string DirectionCode,
    string LineCode, string? CustomerCode, Guid? VesselCallId, string? CallRef, DateOnly? ValidTo,
    Guid BookingContainerId, string? DeclaredSealNo, decimal? DeclaredVgmKg, string? EquipmentTypeCode);

/// <summary>The step this move would complete, and the checks its MDM rules switch on.</summary>
public sealed record GateStepResponse(
    Guid MovementPlanId, short SequenceNo, string MovementCode, string Direction, string FullEmpty,
    bool IsRequired, bool CheckSealNo, bool CheckGrossWeight, bool RequireVesselVoyage,
    bool AllowDamagedRelease, bool RequiresSurvey, IReadOnlyList<string> StepsSkipped);

public sealed record GateHoldResponse(
    Guid ContainerHoldId, string HoldCode, string? Description, string? BlockingScope,
    string? ReleaseAuthority, string HeldVia, bool BlocksThisMove);

public sealed record GateYardResponse(
    Guid ContainerVisitId, Guid BranchId, string FullEmpty, string? PositionText, DateTimeOffset LastEventAt);

public sealed record GateCutoffResponse(string Kind, DateTimeOffset At, bool IsLate, Guid? CoveredByExceptionId);

public sealed record GateCouponResponse(
    Guid GateAuthorizationId, string CouponRef, string PaymentChannel, DateTimeOffset ValidUntil);

// ── the EIR ─────────────────────────────────────────────────────────────────

public sealed record GateTransactionResponse(
    Guid GateTransactionId, string EirNo, Guid BranchId, string Direction, byte PositionNo,
    string MovementCode, string FullEmpty, string ContainerNo, bool IsCheckDigitValid,
    string? EquipmentTypeCode, Guid BookingId, string OrderNo, string LineCode,
    Guid TruckVisitId, string VisitNo, string TruckPlate,
    decimal? GrossWeightKg, decimal? VgmKg, string? VgmMethod, string? WeightSource,
    string? ConditionCode, string? GradeCode, bool SealMismatch, IReadOnlyList<GateSealResponse> Seals,
    string? CutoffKindApplied, DateTimeOffset? CutoffAtApplied, bool IsLate,
    Guid? CutoffExceptionId, string? LateOverrideReason, string? CheckDigitOverrideReason,
    Guid? GateAuthorizationId,
    DateTimeOffset TransactionAt, DateTimeOffset RecordedAt, string Status,
    Guid? ContainerVisitId, bool BookingContainerCompleted, string RowVersion,
    // What the register and the EIR detail show besides the move itself; the
    // void fields stay null on a live EIR. Appended so existing readers are unaffected.
    decimal? TareWeightKg = null, decimal? TempObservedC = null, string? IsoCode = null,
    string? PositionText = null, Guid? SurveyId = null, string? Remarks = null,
    DateTimeOffset? VoidedAt = null, Guid? VoidedBy = null, string? VoidReason = null,
    Guid? ReplacesGateTransactionId = null,
    // Vector Gate In parity (gate-in-vector-parity.md §5). Appended, so existing readers are unaffected.
    string? TruckCategoryCode = null, string? TripType = null,
    string? MaterialCode = null,
    decimal? MaxGrossWeightKg = null, decimal? CargoWeightKg = null,
    string? VentSetting = null, decimal? HumidityPct = null,
    string? GensetNo = null, string? ClipOnNo = null,
    string? CustomsPermitNo = null, string? PaperlessCode = null, string? NextLocationCode = null,
    // What the barrier said as the move was recorded (warnings and notes — a refusal never gets
    // this far), e.g. a truck that is not the one paid for. Not stored: null when an EIR is read back.
    IReadOnlyList<GateFindingResponse>? Findings = null,
    // gecko_tos 26 (A5): the height stored on the move. Appended, so existing readers are unaffected.
    string? HeightCode = null);

public sealed record GateSealResponse(string SealNo, string SealType, bool IsIntact, bool? MatchesDeclared);

public sealed record GateTransactionSummaryResponse(
    Guid GateTransactionId, string EirNo, string Direction, string MovementCode, string FullEmpty,
    string ContainerNo, string OrderNo, string LineCode, string TruckPlate,
    DateTimeOffset TransactionAt, bool IsLate, string Status);

// ── requests ────────────────────────────────────────────────────────────────

/// <summary>
/// The truck the box arrived on. Either name an open visit (the second box of a
/// drop-one-take-one) or describe the truck and one is opened.
///
/// The driver's licence is HASHED on the way in (PDPA): enough to recognise a
/// repeat driver, never enough to leak one.
/// </summary>
public sealed record TruckRequest(
    [property: Required, MaxLength(20)] string Plate,
    [property: MaxLength(20)] string? TrailerPlate = null,
    [property: MaxLength(30)] string? HaulierCode = null,
    [property: MaxLength(100)] string? DriverName = null,
    [property: MaxLength(30)] string? DriverLicence = null,
    [property: MaxLength(10)] string? LaneCode = null,
    DateTimeOffset? ArrivedAt = null,
    // Vector parity (gate-in-vector-parity.md §5): a tariff axis — MDM code list TRUCK_CATEGORY.
    [property: MaxLength(20)] string? TruckCategoryCode = null);

/// <summary>AGENT and CUSTOMER are Vector's "Agent Seal" / "Cust. Seal" (gecko_tos 15).</summary>
public sealed record SealRequest(
    [property: Required, MaxLength(20)] string SealNo,
    [property: AllowedValues("LINE", "CUSTOMS", "SHIPPER", "TERMINAL", "OTHER", "AGENT", "CUSTOMER")] string SealType = "LINE",
    bool IsIntact = true);

public sealed record GateTransactionRequest(
    [property: Required] Guid? BranchId,
    [property: Required, MaxLength(11)] string ContainerNo,
    [property: Required, AllowedValues("IN", "OUT")] string Direction,
    Guid? TruckVisitId = null,
    TruckRequest? Truck = null,
    DateTimeOffset? TransactionAt = null,
    decimal? GrossWeightKg = null,
    decimal? TareWeightKg = null,
    decimal? VgmKg = null,
    [property: AllowedValues(null, "SM1", "SM2")] string? VgmMethod = null,
    [property: AllowedValues(null, "WEIGHBRIDGE", "DECLARED", "EDI")] string? WeightSource = null,
    [property: MaxLength(20)] string? ConditionCode = null,
    [property: MaxLength(20)] string? GradeCode = null,
    decimal? TemperatureC = null,
    [property: MaxLength(4)] string? IsoCode = null,
    IReadOnlyList<SealRequest>? Seals = null,
    Guid? YardId = null,
    Guid? YardSlotId = null,
    [property: MaxLength(30)] string? PositionText = null,
    [property: MaxLength(300)] string? CheckDigitOverrideReason = null,
    [property: MaxLength(300)] string? LateOverrideReason = null,
    [property: MaxLength(500)] string? Remarks = null,
    // ── Vector Gate In parity (gate-in-vector-parity.md §5) ──
    // TripType is REQUIRED (owner 2026-10-01): DROP_OFF_CONT is an IN and PICK_UP_CONT
    // an OUT (GateRules.TripTypeDirections), and it drives the §2.1 mandatory-field
    // matrix (GateRules.MissingForTrip) on every transaction. "Container class" is
    // grade_code (Vector fills it from ContainerGrade) — there is no separate field.
    [property: Required, AllowedValues("DROP_OFF_CONT", "PICK_UP_CONT")] string? TripType = null,
    [property: MaxLength(20)] string? MaterialCode = null,
    [property: Range(0.01, 99_999_999)] decimal? MaxGrossWeightKg = null,
    [property: Range(0, 99_999_999)] decimal? CargoWeightKg = null,
    [property: MaxLength(20)] string? VentSetting = null,
    [property: Range(0, 100)] decimal? HumidityPct = null,
    [property: MaxLength(20)] string? GensetNo = null,
    [property: MaxLength(20)] string? ClipOnNo = null,
    [property: MaxLength(40)] string? CustomsPermitNo = null,
    [property: MaxLength(40)] string? PaperlessCode = null,
    [property: MaxLength(20)] string? NextLocationCode = null,
    // The screen's draft (GATE_IN_BIG_SAVE §1): a box another draft holds is refused (BOX_RESERVED);
    // this draft's own hold is consumed by the move.
    Guid? DraftId = null,
    // GATE_IN_COMPLETION_PLAN A5 (Vector GateIn.cs:3352): null = the equipment type's height class.
    [property: AllowedValues(null, "STANDARD", "HIGH_CUBE", "HALF")] string? HeightCode = null);

/// <summary>
/// A box that arrives with no order (owner 2026-10-04, GATE_IN_VECTOR_PARITY_FOR_API §1): the gate
/// raises a BLIND GATE IN order for it, the window prices and takes the cash, then the box gates in
/// as usual. <see cref="EquipmentTypeCode"/> null = the registry's type for the box. The B/L is
/// optional on BLIND GATE IN (Vector GateIn.cs:146); the customer is not (owner 2026-10-04).
/// </summary>
public sealed record BlindOrderRequest(
    [property: Required] Guid? BranchId,
    [property: Required, MaxLength(11)] string ContainerNo,
    [property: Required, MaxLength(30)] string LineCode,
    [property: MaxLength(30)] string? CustomerCode = null,
    [property: MaxLength(20)] string? EquipmentTypeCode = null,
    [property: MaxLength(40)] string? CarrierRef = null,
    [property: MaxLength(30)] string? AgentCode = null,
    [property: MaxLength(30)] string? HaulierCode = null,
    [property: MaxLength(1000)] string? Remarks = null,
    // The screen's draft (GATE_IN_BIG_SAVE §1): a box another draft holds is refused (BOX_RESERVED).
    Guid? DraftId = null);

/// <summary>
/// Record (GATE_IN_BIG_SAVE §1): hold a box for the truck being keyed. <see cref="DraftId"/> is the
/// screen's id for that truck — the same draft holding the same box again is the same hold.
/// Name a booked box by <see cref="BookingContainerId"/>; a box on no order yet by <see cref="ContainerNo"/>.
/// </summary>
public sealed record ReserveBoxRequest(
    [property: Required] Guid? BranchId,
    [property: Required] Guid? DraftId,
    Guid? BookingContainerId = null,
    [property: MaxLength(11)] string? ContainerNo = null);

public sealed record BoxReservationResponse(
    Guid BoxReservationId, Guid BranchId, Guid DraftId, Guid? BookingContainerId, string? ContainerNo,
    Guid ReservedBy, string? ReservedByName, DateTimeOffset ReservedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// One box the gate clerk can pick (GATE_IN_VECTOR_PARITY_FOR_API §2): the booking it is on and the
/// step it would do next. <see cref="ContainerNo"/> null = a booked place not nominated yet.
/// </summary>
public sealed record BookableBoxResponse(
    Guid BookingContainerId, Guid BookingId, string OrderNo, string? CarrierRef,
    string BookingTypeCode, string OrderTypeCode, string LineCode, string? AgentCode, string? CustomerCode,
    string? ContainerNo, string? EquipmentTypeCode, BookableStepResponse NextStep);

public sealed record BookableStepResponse(Guid MovementPlanId, short SequenceNo, string MovementCode, string Direction, string FullEmpty);

/// <summary>
/// One gate VAS an order type offers (GATE_IN_COMPLETION_PLAN A3): the clerk ticks it on an EMPTY drop-off or a
/// pick-up, and the Save prices it (<c>vas</c>). <see cref="PaymentTermCode"/> null = CASH or CREDIT, as the
/// truck's terms say. <see cref="OfferedOnMovements"/>: this order type's steps where it can be ticked.
/// </summary>
public sealed record GateVasResponse(
    string ChargeCode, string Description, string? DescriptionLocal, string BillTo, string? PaymentTermCode,
    string OfferedOn, IReadOnlyList<string> OfferedOnMovements);

/// <summary>The gate's damage panel (GATE_IN_COMPLETION_PLAN A4): MDM's CEDEX codes, read under tos.gate.view.</summary>
public sealed record GateDamageCodesResponse(
    IReadOnlyList<GateDamageCodeResponse> DamageCodes, IReadOnlyList<string> Locations, IReadOnlyList<string> Components);

/// <param name="MakesUnserviceable">Ticking it makes the box unserviceable and puts on the depot's damage hold.</param>
public sealed record GateDamageCodeResponse(string DamageCode, string Description, byte Severity, bool MakesUnserviceable);

// ── the big Save (GATE_IN_BIG_SAVE §2) ──────────────────────────────────────

/// <summary>
/// The whole truck in one Save (Vector GateIn.cs btnSave_Click). Header <c>Idempotency-Key</c> required.
/// <see cref="Payment"/> null = take no money (a truck whose boxes owe no cash, or are on credit).
/// <see cref="Vas"/>: the gate VAS the clerk ticked; each booking takes the codes its order type offers.
/// </summary>
public sealed record TripSaveRequest(
    [property: Required] Guid? BranchId,
    [property: Required] Guid? DraftId,
    [property: Required] TruckRequest? Truck,
    [property: Required, MinLength(1), MaxLength(4)] IReadOnlyList<TripRowRequest>? Rows,
    IReadOnlyList<string>? Vas = null,
    TripPaymentRequest? Payment = null);

/// <summary>
/// One box: a booked one (<see cref="BookingContainerId"/>) or one on no order (<see cref="Blind"/>, a BLIND GATE IN),
/// and its gate fields as on <c>POST /gate/transactions</c> (<see cref="Move"/>; its branch, draft and truck are the Save's).
/// <see cref="Damages"/> (A4, a drop-off only): saved with the move as its GATE_IN survey, with the move's condition and
/// grade (Vector GateIn.cs:1091, 2079); an unserviceable code puts on the depot's damage hold.
/// <see cref="EquipmentTypeCode"/> (A11, a booked box): the type the clerk sees. Another type than booked is 409
/// TYPE_MISMATCH; on an IMPORT FULL drop-off, <see cref="AcceptTypeChange"/> moves the box to the booking's line of
/// that type first (Vector GateIn.cs:1123, owner D3).
/// </summary>
public sealed record TripRowRequest(
    Guid? BookingContainerId = null,
    TripBlindRequest? Blind = null,
    GateTransactionRequest? Move = null,
    IReadOnlyList<SurveyDamageRequest>? Damages = null,
    [property: MaxLength(20)] string? EquipmentTypeCode = null,
    bool AcceptTypeChange = false);

public sealed record TripBlindRequest(
    [property: Required, MaxLength(11)] string? ContainerNo,
    [property: Required, MaxLength(30)] string? LineCode,
    [property: MaxLength(30)] string? CustomerCode = null,
    [property: MaxLength(20)] string? EquipmentTypeCode = null,
    [property: MaxLength(40)] string? CarrierRef = null,
    [property: MaxLength(30)] string? AgentCode = null,
    [property: MaxLength(1000)] string? Remarks = null);

/// <param name="ExpectedTotal">The cash total the card showed (VAT included); a different total now is 409 "The price changed".</param>
public sealed record TripPaymentRequest(
    TruckPayer? Payer, IReadOnlyList<TruckPaymentLine>? Payments, decimal ExpectedTotal, bool WithholdingTax = false);

/// <summary>What the clerk prints: the receipt (one per truck) and an EIR per box that went through.</summary>
/// <param name="TruckInPdfUrl">The truck-in form (A10), once a box went through.</param>
public sealed record TripSaveResponse(
    Guid TripSaveId, Guid? TruckVisitId, string? VisitNo, TripReceiptResponse? Receipt, IReadOnlyList<TripRowResponse> Rows,
    string? TruckInPdfUrl = null);

/// <param name="Nett">What was paid: the total less withholding tax.</param>
/// <param name="CouponPdfUrl">The coupon slips, one per box (A10).</param>
public sealed record TripReceiptResponse(
    Guid ReceiptId, string ReceiptNo, decimal Subtotal, decimal Tax, decimal Total, decimal WithholdingTax, decimal Nett,
    string CurrencyCode, string PdfUrl, string? CouponPdfUrl = null);

/// <param name="Status">GATED (an EIR) or NOT_GATED (<see cref="Reason"/> says why; a paid box keeps its coupon).</param>
/// <param name="SurveyId">The GATE_IN survey of the row's damages (A4); <see cref="HoldsApplied"/>: the holds it put on.</param>
public sealed record TripRowResponse(
    int Index, string ContainerNo, Guid BookingContainerId, string OrderNo, string Status,
    string? EirNo, Guid? GateTransactionId, string? EirPdfUrl, string? CouponRef, string? Reason,
    IReadOnlyList<GateFindingResponse> Findings,
    Guid? SurveyId = null, IReadOnlyList<string>? HoldsApplied = null);

public sealed record VoidGateTransactionRequest(
    [property: Required, MinLength(3), MaxLength(300)] string Reason,
    string? RowVersion = null);

public sealed record DepartTruckRequest(DateTimeOffset? DepartedAt = null);

// ── the yard ────────────────────────────────────────────────────────────────

public sealed record YardContainerResponse(
    Guid ContainerVisitId, Guid BranchId, string ContainerNo, string? EquipmentTypeCode,
    string LineCode, string FullEmpty, string? ConditionCode, string? GradeCode,
    string? PositionText, DateTimeOffset GateInAt, string GateInEirNo, string GateInMovementCode,
    int DaysInYard, bool IsHeld, Guid? CurrentBookingContainerId, DateTimeOffset LastEventAt);

public sealed record TruckVisitResponse(
    Guid TruckVisitId, string VisitNo, Guid BranchId, string TruckPlate, string? TrailerPlate,
    string? HaulierCode, string? DriverName, string? LaneCode,
    DateTimeOffset ArrivedAt, DateTimeOffset? GateInAt, DateTimeOffset? GateOutAt, int? DwellMinutes,
    string Status, string Source, IReadOnlyList<GateTransactionSummaryResponse> Transactions,
    string? TruckCategoryCode = null,
    // What the truck did, DERIVED from its completed (not voided) moves — a PICKUP_DROPOFF_MODE code:
    // DROPOFF (in only), PICKUP (out only), PICKUP_DROPOFF (both), NONE (no move stands). Never an input.
    string? PickupDropoffMode = null);
