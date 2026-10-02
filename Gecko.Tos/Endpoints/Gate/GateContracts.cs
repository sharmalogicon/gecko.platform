using System.ComponentModel.DataAnnotations;

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
    IReadOnlyList<GateFindingResponse>? Findings = null);

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
    [property: MaxLength(20)] string? NextLocationCode = null);

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
