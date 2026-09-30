using System.ComponentModel.DataAnnotations;

namespace Gecko.Tos.Endpoints.Reefer;

// ── responses ───────────────────────────────────────────────────────────────

/// <summary>
/// One plug-in/out session. <c>MinutesPlugged</c> and <c>BillableHours</c> run to
/// now while the session is open; hours are per STARTED hour and never stored.
/// <c>CanManage</c> is the write endpoints' own branch rule, so a button never promises a 403.
/// </summary>
public sealed record ReeferSessionResponse(
    Guid Id, Guid ContainerVisitId, string ContainerNo, Guid BranchId, string? BranchCode,
    string? EquipmentTypeCode, string? IsoTypeCode,
    DateTimeOffset PluggedInAt, Guid? PluggedInBy, DateTimeOffset? PluggedOutAt, Guid? PluggedOutBy,
    string? CloseReason, Guid? CloseGateTransactionId, string? PlugPointCode, decimal? SetPointC, string? Remarks,
    int MinutesPlugged, int BillableHours, bool IsOpen, bool CanManage, string RowVersion);

/// <summary>A reefer standing in the yard with nothing plugged in: what the plug-in screen offers.</summary>
public sealed record ReeferCandidateResponse(
    Guid ContainerVisitId, string ContainerNo, Guid BranchId, string? BranchCode, string? EquipmentTypeCode,
    DateTimeOffset? GateInAt, decimal? SuggestedSetPointC);

// ── requests ────────────────────────────────────────────────────────────────

/// <summary><c>PluggedInAt</c> defaults to now. The box must be a reefer standing in the yard.</summary>
public sealed record PlugInRequest(
    [property: Required, MaxLength(11)] string ContainerNo,
    DateTimeOffset? PluggedInAt = null,
    [property: MaxLength(20)] string? PlugPointCode = null,
    [property: Range(-40, 40, ErrorMessage = "Between −40 °C and +40 °C.")] decimal? SetPointC = null,
    [property: MaxLength(500)] string? Remarks = null);

/// <summary><c>PluggedOutAt</c> defaults to now. <c>Remarks</c>, when given, replaces the session's remarks.</summary>
public sealed record PlugOutRequest(
    DateTimeOffset? PluggedOutAt = null,
    [property: MaxLength(500)] string? Remarks = null,
    string? RowVersion = null);

/// <summary>
/// A correction of a session typed wrong. Replaces the times and details; it cannot
/// close an open session (plug it out) nor re-open a closed one.
/// </summary>
public sealed record CorrectReeferSessionRequest(
    [property: Required] DateTimeOffset? PluggedInAt,
    DateTimeOffset? PluggedOutAt = null,
    [property: MaxLength(20)] string? PlugPointCode = null,
    [property: Range(-40, 40, ErrorMessage = "Between −40 °C and +40 °C.")] decimal? SetPointC = null,
    [property: MaxLength(500)] string? Remarks = null,
    string? RowVersion = null);
