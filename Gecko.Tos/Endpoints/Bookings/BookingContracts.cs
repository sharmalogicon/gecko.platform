using System.ComponentModel.DataAnnotations;

namespace Gecko.Tos.Endpoints.Bookings;

// ── responses ───────────────────────────────────────────────────────────────

/// <summary>
/// <see cref="Status"/> is what a person decided (OPEN / CANCELLED / CLOSED);
/// <see cref="Progress"/> is derived — NOT_STARTED / IN_PROGRESS / COMPLETED /
/// EXPIRED, or the status itself once it is not OPEN.
/// </summary>
public sealed record BookingSummaryResponse(
    Guid BookingId, string OrderNo, Guid BranchId, string? BranchCode, string? CarrierRef,
    string OrderTypeCode, string DirectionCode, string LineCode, string? CustomerCode,
    Guid? VesselCallId, string? CallRef, string? Voyage,
    string Status, string Progress, int QtyRequired, int QtyAssigned, int QtyCompleted,
    DateOnly? ValidTo, string Source, DateTimeOffset CreatedAt);

public sealed record BookingResponse(
    Guid BookingId, string OrderNo, Guid BranchId, string? BranchCode, string? CarrierRef,
    string OrderTypeCode, string BookingTypeCode, string DirectionCode, string CargoClassCode,
    string LineCode, string? AgentCode, string? CustomerCode, string? ForwarderCode, string? HaulierCode,
    Guid? VesselCallId, string? CallRef, string? VesselCode, DateTimeOffset? Etd, string? CallStatus, string? VoyageIn, string? VoyageOut,
    string? PolPortCode, string? PodPortCode, string? FpdPortCode,
    string? CargoCategoryCode, string? CommodityCode, DateOnly? ValidFrom, DateOnly? ValidTo,
    string Status, string Progress, DateTimeOffset? CancelledAt, string? CancelReason, DateTimeOffset? ClosedAt, string? CloseReason,
    string Source, string? CustomerRef, string? Remarks, DateTimeOffset CreatedAt, string RowVersion);

public sealed record RequirementResponse(
    Guid EquipmentRequirementId, short LineNo, string EquipmentTypeCode, short Qty, int QtyAssigned, int QtyCompleted,
    string? MinGradeCode, decimal? ReeferSetTempC, decimal? ReeferVentPct, decimal? ReeferHumidityPct,
    string? ImdgClass, string? UnNumber,
    short? OogOverHeightCm, short? OogOverWidthLeftCm, short? OogOverWidthRightCm, short? OogOverLengthFrontCm, short? OogOverLengthBackCm,
    decimal? DeclaredGrossWeightKg, string? Remarks);

/// <summary>One step of one box. <see cref="GateTransactionId"/> is the only link to time (D-4).</summary>
public sealed record StepResponse(Guid MovementPlanId, short SequenceNo, string MovementCode, bool IsRequired, string Status, Guid? GateTransactionId, string? SkipReason);

/// <summary><see cref="InRegistry"/> false = a box MDM does not know yet (allowed by gate.allow_unknown_container).</summary>
public sealed record BookingContainerResponse(
    Guid BookingContainerId, Guid EquipmentRequirementId, short LineNo, string ContainerNo, bool InRegistry, bool IsCheckDigitValid,
    string Source, string? DeclaredSealNo, decimal? DeclaredVgmKg, DateTimeOffset AssignedAt,
    DateTimeOffset? EndedAt, string? EndReason, IReadOnlyList<StepResponse> Steps,
    // Vector's P/U Mode / D/O Mode / repo mode: who collects or delivers this box. Null = not said.
    string? HandoverMode = null);

public sealed record BookingDetailResponse(
    BookingResponse Booking,
    int QtyRequired, int QtyAssigned, int QtyCompleted, int StepsDone,
    IReadOnlyList<RequirementResponse> Requirements,
    IReadOnlyList<BookingContainerResponse> Containers);

// ── requests ────────────────────────────────────────────────────────────────

/// <summary>
/// The booking header. Create also takes the requirement lines (a booking with
/// nothing to move is not a booking) and, optionally, pre-advised boxes (an
/// import D/O lists them — Q9).
/// </summary>
public sealed record SaveBookingRequest(
    [property: Required] Guid? BranchId,
    [property: Required, MaxLength(50)] string OrderTypeCode,
    [property: Required, MaxLength(30)] string LineCode,
    [property: MaxLength(40)] string? CarrierRef = null,
    [property: MaxLength(30)] string? AgentCode = null,
    [property: MaxLength(30)] string? CustomerCode = null,
    [property: MaxLength(30)] string? ForwarderCode = null,
    [property: MaxLength(30)] string? HaulierCode = null,
    Guid? VesselCallId = null,
    [property: MaxLength(20)] string? PolPortCode = null,
    [property: MaxLength(20)] string? PodPortCode = null,
    [property: MaxLength(20)] string? FpdPortCode = null,
    [property: MaxLength(20)] string? CargoCategoryCode = null,
    [property: MaxLength(30)] string? CommodityCode = null,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null,
    [property: MaxLength(50)] string? CustomerRef = null,
    [property: MaxLength(1000)] string? Remarks = null,
    IReadOnlyList<RequirementItem>? Requirements = null,
    IReadOnlyList<AssignContainerItem>? Containers = null,
    string? RowVersion = null);

/// <summary><see cref="LineNo"/> null on create = next number; on replace, the line it updates.</summary>
public sealed record RequirementItem(
    string EquipmentTypeCode,
    short Qty,
    short? LineNo = null,
    string? MinGradeCode = null,
    decimal? ReeferSetTempC = null,
    decimal? ReeferVentPct = null,
    decimal? ReeferHumidityPct = null,
    string? ImdgClass = null,
    string? UnNumber = null,
    short? OogOverHeightCm = null,
    short? OogOverWidthLeftCm = null,
    short? OogOverWidthRightCm = null,
    short? OogOverLengthFrontCm = null,
    short? OogOverLengthBackCm = null,
    decimal? DeclaredGrossWeightKg = null,
    string? Remarks = null);

public sealed record ReplaceRequirementsRequest(
    [property: Required, MinLength(1)] IReadOnlyList<RequirementItem> Requirements,
    [property: Required] string RowVersion);

/// <summary><see cref="LineNo"/> may be left out when the box's registry type, or a single line, makes it obvious.</summary>
public sealed record AssignContainerItem(
    string ContainerNo,
    short? LineNo = null,
    string? DeclaredSealNo = null,
    decimal? DeclaredVgmKg = null,
    // IMPORT: DO_OWN | DO_OTHER | DO_ONLY | DO_CUS · EXPORT: PU_OWN | PU_OTHER | PU_ONLY | PU_PORT · other: REPO_OWN | REPO_OTHER.
    // Optional. When sent, Vector's booking checks apply (BookingRules.HandoverRefusal).
    [property: MaxLength(20)] string? HandoverMode = null);

public sealed record AssignContainersRequest(
    [property: Required, MinLength(1)] IReadOnlyList<AssignContainerItem> Containers,
    [property: AllowedValues("PRE_ADVISED", "PORTAL", "EDI")] string Source = "PRE_ADVISED");

public sealed record EndBookingRequest(
    [property: Required, MinLength(5), MaxLength(500)] string Reason,
    [property: Required] string RowVersion);
