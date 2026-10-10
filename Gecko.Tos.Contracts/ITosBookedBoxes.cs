namespace Gecko.Tos.Contracts;

/// <summary>
/// A depot's booked boxes with their vessel and gate dates — what Vector's Container Storage Activity read from
/// BookingContainer + ContainerMovement. Owner 2026-10-10: a box is "in the window" by its booking's vessel ETA, or,
/// for a booking with no vessel call (REPO, INTERNAL), by the box's first gate-in.
/// </summary>
public interface ITosBookedBoxes
{
    /// <summary>Boxes with a container number on bookings at <paramref name="branchId"/> (not cancelled), in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    Task<IReadOnlyList<TosBookedBox>> BoxesAsync(Guid branchId, DateTimeOffset from, DateTimeOffset to, TosBookedBoxFilter filter, CancellationToken ct);

    /// <summary>The same facts for given booked boxes (by booking_container_id), whatever their dates; unknown ids are absent.</summary>
    Task<IReadOnlyDictionary<Guid, TosBookedBox>> BoxesByIdAsync(IReadOnlyCollection<Guid> bookingContainerIds, CancellationToken ct);
}

/// <param name="LineCode">Vector's AgentCode: the booking's shipping line.</param>
/// <param name="Voyage">Matches the call's voyage in or out (operator's or the line's).</param>
/// <param name="CarrierRef">Vector's BookingBLNo.</param>
public sealed record TosBookedBoxFilter(
    string? LineCode = null, string? VesselCode = null, string? Voyage = null, string? BookingTypeCode = null,
    string? OrderTypeCode = null, string? CarrierRef = null);

/// <param name="Eta">The booking's vessel ETA; null without a vessel call.</param>
/// <param name="EmptyIn">First completed empty gate-in of the box on this booking; <paramref name="EmptyOut"/> the last empty
/// gate-out, <paramref name="LadenIn"/>/<paramref name="LadenOut"/> the same for full moves.</param>
public sealed record TosBookedBox(
    Guid BookingContainerId, Guid BookingId, string ContainerNo, string EquipmentTypeCode, string OrderTypeCode,
    string BookingTypeCode, string LineCode, string? VesselCode, string? Voyage, DateTimeOffset? Eta,
    DateTimeOffset? EmptyIn, DateTimeOffset? EmptyOut, DateTimeOffset? LadenIn, DateTimeOffset? LadenOut);
