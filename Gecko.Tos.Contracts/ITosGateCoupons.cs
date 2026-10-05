namespace Gecko.Tos.Contracts;

/// <summary>
/// The gate coupons as the barrier holds them (ADR-007: Revenue issues a coupon, TOS keeps it). Revenue's coupon
/// slip (GATE_IN_COMPLETION_PLAN A10, Vector CouponInvoice) prints how long each one is good for and whether it
/// was used or withdrawn — facts only TOS has.
/// </summary>
public interface ITosGateCoupons
{
    /// <summary>By coupon ref; a ref TOS has not received yet is absent.</summary>
    Task<IReadOnlyDictionary<string, TosGateCoupon>> CouponsAsync(IReadOnlyCollection<string> couponRefs, CancellationToken ct);
}

/// <param name="OrderNo">The booking it was paid on (a truck's receipt can span several).</param>
/// <param name="UsedByEirNo">The EIR of the move that used it; null while unused.</param>
public sealed record TosGateCoupon(
    string CouponRef, string? OrderNo, string? ContainerNo, string MovementCode, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil,
    DateTimeOffset? UsedAt, string? UsedByEirNo, DateTimeOffset? RevokedAt);
