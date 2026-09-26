using System.ComponentModel.DataAnnotations;

namespace Gecko.Tos.Endpoints.Holds;

// ── responses ───────────────────────────────────────────────────────────────

/// <summary>
/// One hold row. The MDM half (type, scope, authority, colour) is read through
/// <c>IMasterDataReferences</c> and may be null if the hold type was deleted after
/// the hold was applied — the row keeps working, because it stores its own code.
/// </summary>
public sealed record HoldResponse(
    Guid ContainerHoldId, string? ContainerNo, Guid? BookingId, string? OrderNo, Guid? BranchId, string? BranchCode,
    string HoldCode, string? Description, string? HoldType, string? BlockingScope, string? ReleaseAuthority,
    byte? Priority, string? DisplayColorHex,
    DateTimeOffset AppliedAt, Guid? AppliedBy, string ApplyReason, string? ExternalRef, string Source,
    DateTimeOffset? ReleasedAt, Guid? ReleasedBy, string? ReleaseReason, string? ReleaseRef, string? ReleaseSource,
    bool IsActive, string RowVersion);

/// <summary>
/// What stops this box moving right now — the barrier's question (PLAN §5.4),
/// answered from <c>yard.vw_active_hold</c>, which already unions the holds placed
/// on the box with the holds placed on the booking it is assigned to.
/// </summary>
public sealed record ContainerHoldsResponse(
    string ContainerNo, bool IsHeld, IReadOnlyList<ActiveHoldResponse> Holds);

/// <summary><c>HeldVia</c> is CONTAINER (placed on the box) or BOOKING (placed on a booking it is on).</summary>
public sealed record ActiveHoldResponse(
    Guid ContainerHoldId, string HoldCode, string? Description, string? HoldType, string? BlockingScope,
    string? ReleaseAuthority, byte? Priority, string? DisplayColorHex,
    DateTimeOffset AppliedAt, string ApplyReason, string Source,
    string HeldVia, Guid? BookingId, string? OrderNo);

// ── requests ────────────────────────────────────────────────────────────────

/// <summary>
/// A hold goes on a BOX or on a BOOKING, never both: the two are different
/// statements ("this box may not move" vs "nothing on this order may move"), and
/// the database's two filtered unique indexes treat them separately.
/// </summary>
public sealed record ApplyHoldRequest(
    [property: Required, MaxLength(20)] string HoldCode,
    [property: Required, MinLength(3), MaxLength(500)] string Reason,
    [property: MaxLength(11)] string? ContainerNo = null,
    Guid? BookingId = null,
    [property: MaxLength(50)] string? ExternalRef = null);

public sealed record ReleaseHoldRequest(
    [property: Required, MinLength(3), MaxLength(500)] string Reason,
    [property: MaxLength(50)] string? ReleaseRef = null,
    string? RowVersion = null);
