using System.ComponentModel.DataAnnotations;

namespace Gecko.Tos.Endpoints.Vessels;

// ── responses ───────────────────────────────────────────────────────────────

/// <summary>One row of the schedule. <see cref="Lines"/> reads "MAEU 2638W" — line code and outbound voyage.</summary>
public sealed record VesselCallSummaryResponse(
    Guid VesselCallId, string CallRef, string VesselCode, string? VesselName, string PortCode, string? TerminalCode,
    string? OperatorVoyageIn, string? OperatorVoyageOut,
    DateTimeOffset Eta, DateTimeOffset? Etb, DateTimeOffset Etd,
    DateTimeOffset? Ata, DateTimeOffset? Atb, DateTimeOffset? Atd,
    DateTimeOffset? LastYardCutoffAt, string Status, IReadOnlyList<string> Lines);

public sealed record VesselCallResponse(
    Guid VesselCallId, string CallRef, string VesselCode, string? VesselName, string PortCode, string? TerminalCode,
    string? OperatorVoyageIn, string? OperatorVoyageOut,
    DateTimeOffset Eta, DateTimeOffset? Etb, DateTimeOffset Etd,
    DateTimeOffset? Ata, DateTimeOffset? Atb, DateTimeOffset? Atd,
    string Status, bool IsCancelled, DateTimeOffset? CancelledAt, string? CancelReason,
    string Source, string? Remarks, string RowVersion,
    // A FULL EXPORT box may not gate out before this (Vector LadenReleaseDate); null = no restriction.
    DateTimeOffset? LadenReleaseAt = null);

public sealed record VesselCallLineResponse(
    Guid VesselCallLineId, string LineCode, string? AgentCode, string? VoyageIn, string? VoyageOut, string? ServiceCode);

/// <summary><see cref="Source"/> DERIVED = computed from the port cut-off minus <see cref="DerivedLeadHours"/> (PLAN Q3).</summary>
public sealed record VesselCallCutoffResponse(
    Guid VesselCallCutoffId, string Kind, string? LineCode, Guid? BranchId, DateTimeOffset At,
    string Source, short? DerivedLeadHours, string? Remarks);

public sealed record VesselCallDetailResponse(
    VesselCallResponse Call, IReadOnlyList<VesselCallLineResponse> Lines, IReadOnlyList<VesselCallCutoffResponse> Cutoffs);

/// <summary>The cut-off that applies to one line at one branch — the most specific row per kind wins.</summary>
public sealed record EffectiveCutoffResponse(string Kind, DateTimeOffset At, string Source, string AppliesTo);

// ── requests ────────────────────────────────────────────────────────────────

/// <summary>
/// Create takes the whole call — header, lines, cut-offs — because a call with
/// no lines cannot be booked against. Update (PUT) changes the header only;
/// lines and cut-offs are replaced as whole sets on their own routes.
/// </summary>
public sealed record SaveVesselCallRequest(
    [property: Required, MaxLength(20)] string VesselCode,
    [property: Required, MaxLength(20)] string PortCode,
    [property: Required] DateTimeOffset? Eta,
    [property: Required] DateTimeOffset? Etd,
    [property: MaxLength(30), RegularExpression("^[A-Z0-9][A-Z0-9 ._/-]{0,29}$", ErrorMessage = "Upper-case letters, digits and . _ / -, up to 30 — e.g. BLUEMERIDIAN-2640W.")] string? CallRef = null,
    [property: MaxLength(30)] string? TerminalCode = null,
    [property: MaxLength(20)] string? OperatorVoyageIn = null,
    [property: MaxLength(20)] string? OperatorVoyageOut = null,
    DateTimeOffset? Etb = null,
    [property: MaxLength(500)] string? Remarks = null,
    IReadOnlyList<VesselCallLineItem>? Lines = null,
    IReadOnlyList<VesselCallCutoffItem>? Cutoffs = null,
    string? RowVersion = null,
    // Header field like Etb: what is sent is what is stored (null = no laden release date).
    DateTimeOffset? LadenReleaseAt = null);

public sealed record VesselCallLineItem(
    string LineCode,
    string? AgentCode = null,
    string? VoyageIn = null,
    string? VoyageOut = null,
    string? ServiceCode = null);

public sealed record VesselCallCutoffItem(
    string Kind,
    DateTimeOffset? At,
    string? LineCode = null,
    Guid? BranchId = null,
    string? Remarks = null);

public sealed record ReplaceVesselCallLinesRequest([property: Required, MinLength(1)] IReadOnlyList<VesselCallLineItem> Lines);

public sealed record ReplaceVesselCallCutoffsRequest([property: Required] IReadOnlyList<VesselCallCutoffItem> Cutoffs);

public sealed record CancelVesselCallRequest(
    [property: Required, MinLength(5), MaxLength(500)] string Reason,
    [property: Required] string RowVersion);

/// <summary>All three actuals, as they should now stand — send null to clear one entered by mistake.</summary>
public sealed record RecordActualsRequest(
    [property: Required] string RowVersion,
    DateTimeOffset? Ata = null,
    DateTimeOffset? Atb = null,
    DateTimeOffset? Atd = null);
