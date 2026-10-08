namespace Gecko.Revenue.Contracts;

/// <summary>
/// What Revenue holds against one booked box, for TOS decisions a human makes on the booking page
/// (not the barrier — ADR-007). Owner 2026-10-08: a box's size/type may not change once something
/// was paid on it (at the window, the gate or on a cash bill in advance).
/// </summary>
public interface IRevenueBoxCharges
{
    /// <summary>The receipt number a charge of this box is paid on (PAID, not yet earned); null when nothing is.</summary>
    Task<string?> PaidOnAsync(Guid bookingContainerId, CancellationToken ct);
}
