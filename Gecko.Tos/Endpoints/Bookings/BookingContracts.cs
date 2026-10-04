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
    string Source, string? CustomerRef, string? Remarks, DateTimeOffset CreatedAt, string RowVersion,
    // gecko_tos 21 — Vector's Vessel & Voyage and Other Information panels.
    bool AllowLateGateIn = false, DateTimeOffset? LateGateSetAt = null, string? PaperlessCode = null, string? SubBlNo = null,
    string? NextPrevLocation = null, int? TotalQty = null, string? UomCode = null, decimal? TotalVolumeCbm = null,
    decimal? TotalWeightKg = null, string? MarksAndNos = null, string? SpecialInstruction = null,
    // Derived, read-only (as Vector showed them): the line's operator code ("Owner Code") and the destination port's trade mode.
    string? ContainerOwnerCode = null, string? TradeModeCode = null,
    // Who made it and who last changed it, with names for the screen.
    Guid? CreatedBy = null, string? CreatedByName = null, DateTimeOffset? UpdatedAt = null, Guid? UpdatedBy = null, string? UpdatedByName = null);

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
    string? HandoverMode = null,
    // gecko_tos 20: the UI's id for the row (retry = same line) and the per-box details Vector keeps.
    Guid? ClientLineId = null, string? CustomerSealNo = null, decimal? DeclaredVolumeCbm = null, DateOnly? RequiredDate = null,
    string? CargoCategoryCode = null, string? ImdgClass = null, string? UnNumber = null,
    decimal? ReeferSetTempC = null, decimal? ReeferVentPct = null, decimal? ReeferHumidityPct = null,
    string? StowageCode = null, string? StowageNo = null, bool? IsPreCool = null, string? Remarks = null,
    // The line's own row version: send it back to edit the line.
    string? RowVersion = null);

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
    string? RowVersion = null,
    // gecko_tos 21. AllowLateGateIn: null = leave as it is (false on create); changing it needs tos.cutoff.override.
    bool? AllowLateGateIn = null,
    [property: MaxLength(30)] string? PaperlessCode = null,
    [property: MaxLength(40)] string? SubBlNo = null,
    [property: MaxLength(50)] string? NextPrevLocation = null,
    int? TotalQty = null,
    [property: MaxLength(20)] string? UomCode = null,
    decimal? TotalVolumeCbm = null,
    decimal? TotalWeightKg = null,
    [property: MaxLength(200)] string? MarksAndNos = null,
    [property: MaxLength(1000)] string? SpecialInstruction = null);

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

/// <summary>
/// One container line. <see cref="LineNo"/> may be left out when the box's registry type, or a single line, makes it
/// obvious. <see cref="ClientLineId"/>: the UI's own id for the row, made when the row is keyed — a retry with it answers
/// with the line already made (required on …/containers/batch). Reefer and DG details left out are taken from the
/// requirement line. <see cref="DeclaredSealNo"/> is the line's / agent's seal, <see cref="DeclaredVgmKg"/> the weight.
/// </summary>
public sealed record AssignContainerItem(
    string ContainerNo,
    short? LineNo = null,
    string? DeclaredSealNo = null,
    decimal? DeclaredVgmKg = null,
    // IMPORT: DO_OWN | DO_OTHER | DO_ONLY | DO_CUS · EXPORT: PU_OWN | PU_OTHER | PU_ONLY | PU_PORT · other: REPO_OWN | REPO_OTHER.
    // Optional. When sent, Vector's booking checks apply (BookingRules.HandoverRefusal).
    [property: MaxLength(20)] string? HandoverMode = null,
    Guid? ClientLineId = null,
    [property: MaxLength(20)] string? CustomerSealNo = null,
    decimal? DeclaredVolumeCbm = null,
    DateOnly? RequiredDate = null,
    [property: MaxLength(40)] string? CargoCategoryCode = null,
    [property: MaxLength(10)] string? ImdgClass = null,
    [property: MaxLength(4)] string? UnNumber = null,
    decimal? ReeferSetTempC = null,
    decimal? ReeferVentPct = null,
    decimal? ReeferHumidityPct = null,
    [property: MaxLength(20)] string? StowageCode = null,
    [property: MaxLength(20)] string? StowageNo = null,
    bool? IsPreCool = null,
    [property: MaxLength(500)] string? Remarks = null);

public sealed record AssignContainersRequest(
    [property: Required, MinLength(1)] IReadOnlyList<AssignContainerItem> Containers,
    [property: AllowedValues("PRE_ADVISED", "PORTAL", "EDI")] string Source = "PRE_ADVISED");

/// <summary>
/// Container entry that survives a dropped connection: every row carries its <see cref="AssignContainerItem.ClientLineId"/>,
/// each row stands alone (the good ones are saved, the bad ones come back with their errors), and a retry is answered
/// with the lines already made. At most 200 rows per call.
/// </summary>
public sealed record AssignContainersBatchRequest(
    [property: Required, MinLength(1), MaxLength(200)] IReadOnlyList<AssignContainerItem> Containers,
    [property: AllowedValues("PRE_ADVISED", "PORTAL", "EDI")] string Source = "PRE_ADVISED");

/// <summary>How full each requirement line is after the call: <see cref="Assigned"/> boxes active on it of <see cref="Qty"/>.</summary>
public sealed record LineFillResponse(short LineNo, string EquipmentTypeCode, short Qty, int Assigned);

/// <param name="Outcome">CREATED (saved now), REPLAYED (saved by an earlier try of the same clientLineId), REJECTED (see <see cref="Errors"/>).</param>
/// <param name="Errors">Field (containerNo, lineNo, clientLineId, reeferSetTempC…; "" = the row) → messages. Null unless REJECTED.</param>
public sealed record ContainerBatchItemResponse(
    int Index, Guid? ClientLineId, string? ContainerNo, string Outcome,
    BookingContainerResponse? Line, IReadOnlyDictionary<string, string[]>? Errors);

public sealed record ContainerBatchResponse(
    Guid BookingId, string OrderNo, int Created, int Replayed, int Rejected,
    IReadOnlyList<LineFillResponse> Lines, IReadOnlyList<ContainerBatchItemResponse> Items);

/// <summary>
/// The details of one container line, REPLACED AS A WHOLE (a field left out is cleared — send back what you read and
/// change what the user changed), guarded by the line's own <see cref="RowVersion"/>: two people editing the same line
/// cannot overwrite each other (stale = 409). The container number and the requirement line do not change here
/// (unassign and assign). Once the box has passed the gate, its seals, cargo, IMO/UN, required date and handover mode
/// are frozen.
/// </summary>
public sealed record UpdateContainerLineRequest(
    [property: Required] string RowVersion,
    [property: MaxLength(20)] string? DeclaredSealNo = null,
    [property: MaxLength(20)] string? CustomerSealNo = null,
    decimal? DeclaredVgmKg = null,
    decimal? DeclaredVolumeCbm = null,
    DateOnly? RequiredDate = null,
    [property: MaxLength(40)] string? CargoCategoryCode = null,
    [property: MaxLength(10)] string? ImdgClass = null,
    [property: MaxLength(4)] string? UnNumber = null,
    decimal? ReeferSetTempC = null,
    decimal? ReeferVentPct = null,
    decimal? ReeferHumidityPct = null,
    [property: MaxLength(20)] string? StowageCode = null,
    [property: MaxLength(20)] string? StowageNo = null,
    bool? IsPreCool = null,
    [property: MaxLength(500)] string? Remarks = null,
    [property: MaxLength(20)] string? HandoverMode = null);

public sealed record EndBookingRequest(
    [property: Required, MinLength(5), MaxLength(500)] string Reason,
    [property: Required] string RowVersion);
