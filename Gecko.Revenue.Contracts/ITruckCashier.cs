namespace Gecko.Revenue.Contracts;

/// <summary>
/// The gate's big Save (owner 2026-10-04, GATE_IN_BIG_SAVE.md §2): ONE receipt for the cash a whole
/// truck owes, across the bookings its boxes are on (Vector GateIn.cs btnSave_Click). Priced exactly
/// as the cash window prices (the same quoter, the gate charge once per truck), checked against what
/// the clerk saw, and paid. The coupons go to TOS the usual way (the outbox); TOS waits for them.
///
/// A human moment, not the barrier (ADR-007): the barrier still reads only its local coupon.
/// </summary>
public interface ITruckCashier
{
    /// <summary>Whether Revenue's copy of these bookings exists yet (a booking raised a moment ago arrives through the outbox).</summary>
    Task<bool> KnowsBookingsAsync(IReadOnlyCollection<Guid> bookingIds, CancellationToken ct);

    /// <summary>Whether Revenue's copy of these boxes carries these equipment types yet (a type the gate just changed travels by the outbox).</summary>
    Task<bool> KnowsBoxTypesAsync(IReadOnlyDictionary<Guid, string> typeByBookingContainerId, CancellationToken ct);

    /// <summary>Whether Revenue's copy of these booking places carries these container numbers yet (a box keyed at the gate travels by the outbox).</summary>
    Task<bool> KnowsBoxNumbersAsync(IReadOnlyDictionary<Guid, string> containerNoByBookingContainerId, CancellationToken ct);

    /// <summary>
    /// Takes the truck's cash. Retry-safe: the same <see cref="TruckPaymentRequest.IdempotencyKey"/> answers with the receipt the
    /// first call issued. Nothing cash due is <see cref="TruckPaymentOutcome.NothingDue"/>, not an error.
    /// </summary>
    Task<TruckPaymentResult> PayAsync(TruckPaymentRequest request, CancellationToken ct);
}

/// <param name="Bookings">The truck's boxes, by booking. The gate charge (PER_TRIP) is taken once for all of them.</param>
/// <param name="ExpectedTotal">What the clerk saw (the cash total, VAT included): a different total now is refused, as at the window.</param>
public sealed record TruckPaymentRequest(
    Guid BranchId, Guid CashierUserId, string IdempotencyKey,
    IReadOnlyList<TruckBookingBoxes> Bookings,
    string? TruckCategoryCode, string? HaulierCode, IReadOnlyList<string>? Vas,
    TruckPayer? Payer, IReadOnlyList<TruckPaymentLine> Payments, decimal ExpectedTotal, bool WithholdingTax);

public sealed record TruckBookingBoxes(Guid BookingId, IReadOnlyList<Guid> BookingContainerIds);

public sealed record TruckPayer(string? Name, string? TaxId, string? BranchNo, string? Address);

public sealed record TruckPaymentLine(string Channel, decimal Amount, decimal? TenderedAmount, string? ReferenceNo, string? BankName);

public enum TruckPaymentOutcome { Paid, NothingDue, Refused }

/// <param name="Problem">Why it was refused (Refused only): no drawer open, the price changed, a charge with no price…</param>
public sealed record TruckPaymentResult(
    TruckPaymentOutcome Outcome, TruckReceipt? Receipt, IReadOnlyList<TruckCoupon> Coupons,
    TruckPaymentProblem? Problem = null);

public sealed record TruckReceipt(Guid ReceiptId, string ReceiptNo, decimal Subtotal, decimal Tax, decimal Total, decimal WithholdingTax, string CurrencyCode);

/// <summary>One box's coupon: what TOS's barrier will find for it once the outbox has delivered it.</summary>
public sealed record TruckCoupon(Guid BookingId, Guid BookingContainerId, string? ContainerNo, string MovementCode, string CouponRef, decimal Amount);

/// <param name="Status">The HTTP status the gate should answer with (400 / 409).</param>
public sealed record TruckPaymentProblem(int Status, string Title, string? Detail, string? Field = null);
