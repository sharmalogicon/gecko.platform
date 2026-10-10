namespace Gecko.Tos.Contracts;

/// <summary>
/// The booking facts TOS owns that another module shows next to its own rows — the B/L, the vessel
/// and voyage, how far the boxes have got. Revenue's unbilled-orders list (Vector UnBilledOrders.cs)
/// reads them here instead of keeping a second copy that would drift.
/// </summary>
public interface ITosBookingHeaders
{
    Task<IReadOnlyDictionary<Guid, TosBookingHeader>> HeadersAsync(IReadOnlyCollection<Guid> bookingIds, CancellationToken ct);

    /// <summary>
    /// Bookings whose order no, Booking/B/L no or sub-B/L contains <paramref name="text"/>, at
    /// <paramref name="branchIds"/> (null = any branch of the tenant), newest first: exact matches before the rest.
    /// </summary>
    Task<IReadOnlyList<TosBookingHeader>> SearchAsync(string text, IReadOnlyCollection<Guid>? branchIds, int take, CancellationToken ct);

    /// <summary>
    /// The equipment type code each booked box was booked as (its equipment requirement's, e.g. "20GP"), by
    /// booking_container_id — what Vector's cash receipt listings print as Size/Type. Unknown ids are absent.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> ContainerTypesAsync(IReadOnlyCollection<Guid> bookingContainerIds, CancellationToken ct);
}

/// <param name="StepsTotal">Every planned step of every box still on the booking (cancelled ones not counted).</param>
/// <param name="StepsDone">Of those, the ones a gate move completed (DONE) or overtook (SKIPPED).</param>
/// <param name="CompletedBoxes">The boxes with no step left to do.</param>
/// <param name="TotalVolumeCbm">The booking's cargo volume in m³, when declared.</param>
public sealed record TosBookingHeader(
    Guid BookingId, string OrderNo, string? CarrierRef, string? SubBlNo, DateTimeOffset BookedAt, string Status,
    string BookingTypeCode, string OrderTypeCode, string LineCode, string? AgentCode, string? CustomerCode, string? ForwarderCode,
    string? VesselCode, string? CallRef, string? Voyage, string? TerminalCode,
    int StepsTotal, int StepsDone, IReadOnlyList<Guid> CompletedBoxes, decimal? TotalVolumeCbm = null);
