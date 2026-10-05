using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Application;
using Gecko.Revenue.Contracts;
using Gecko.Revenue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Endpoints.Window;

/// <summary>
/// <see cref="ITruckCashier"/>: the gate's big Save takes a whole truck's cash on ONE receipt (owner
/// 2026-10-04, GATE_IN_BIG_SAVE §2). Everything the window checks is checked here the same way, by the
/// same <see cref="WindowService"/>: an open drawer, prices from the live tariff, no unpriced charge, the
/// total the clerk saw, withholding tax over the threshold, payments that add up. The truck's gate charge
/// (PER_TRIP) is quoted once: the first box that carries it carries it for every booking on the truck.
/// </summary>
internal sealed class TruckCashier(RevenueDbContext db, WindowService window, IMasterDataReferences master) : ITruckCashier
{
    private static readonly string[] Channels = ["CASH", "TRANSFER", "CHEQUE", "CARD"];

    public async Task<bool> KnowsBookingsAsync(IReadOnlyCollection<Guid> bookingIds, CancellationToken ct)
    {
        var wanted = bookingIds.Distinct().ToList();
        return await db.BookingPlans.AsNoTracking().CountAsync(p => wanted.Contains(p.BookingId), ct) == wanted.Count;
    }

    public async Task<TruckPaymentResult> PayAsync(TruckPaymentRequest request, CancellationToken ct)
    {
        var hash = Idempotency.HashOf(request with { CashierUserId = Guid.Empty });
        if (await ReplayAsync(request.IdempotencyKey, hash, ct) is { } replay) return replay;
        if (request.Bookings.Count == 0 || request.Bookings.Any(b => b.BookingContainerIds.Count == 0))
            return Refused(400, "Name the truck's boxes.", null, "bookings");

        // ── price every booking on the truck, the gate charge once ─────────
        var groups = new List<(WindowContext Context, List<QuotedBox> Boxes)>();
        string? carrier = null;
        foreach (var booking in request.Bookings)
        {
            var plan = await db.BookingPlans.AsNoTracking().SingleOrDefaultAsync(p => p.BookingId == booking.BookingId, ct);
            if (plan is null) return Refused(409, "The cash window does not know one of these bookings yet.", "Try again in a moment.");
            if (plan.BranchId != request.BranchId) return Refused(400, $"{plan.OrderNo} is booked at another depot.", null, "bookings");
            if (plan.Status != "OPEN") return Refused(409, $"Booking {plan.OrderNo} is {plan.Status}.", null);
            if (await window.ContextAsync(plan, ct) is not { } context) return Refused(400, "Unknown depot.", null, "branchId");

            // The ticked gate VAS applies to the bookings whose order type offers it.
            var offered = (await master.OrderTypeChargesAsync(plan.OrderTypeCode, ct))
                .Where(c => c.IsValueAddedService && c.RaiseAtGateIn).Select(c => c.ChargeCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var vas = (request.Vas ?? []).Where(offered.Contains).ToList();
            var (terms, invalid) = await window.GateTermsAsync(context, request.TruckCategoryCode, request.HaulierCode, vas, ct);
            if (invalid is not null) return Refused(400, "The truck's terms are not valid.", string.Join(" ", invalid.ProblemDetails.Errors.SelectMany(e => e.Value)));

            var boxes = await window.QuoteBoxesAsync(context, booking.BookingContainerIds.ToHashSet(), null, ct, terms, carrier);
            var missing = booking.BookingContainerIds.Where(id => boxes.All(b => b.Box.BookingContainerId != id)).ToList();
            if (missing.Count > 0) return Refused(400, $"Not on {plan.OrderNo}: {string.Join(", ", missing)}.", null, "bookings");
            if (boxes.Where(b => b.Quote is { NoPrice.Count: > 0 }).ToList() is { Count: > 0 } unpriced)
                return Refused(409, "No price for some charges of these boxes.",
                    string.Join(" ", unpriced.Select(b => $"{b.Box.ContainerNo ?? b.Box.BookingContainerId.ToString()}: {b.Note}"))
                    + " Add the rate to the customer's tariff or the standard tariff, or a supervisor waives the line.");
            carrier ??= boxes.FirstOrDefault(b => b.Quote is { CarriesTripCharge: true }) is { } carries
                ? $"{carries.Box.ContainerNo ?? "a box"} on {plan.OrderNo}" : null;
            groups.Add((context, boxes.Where(b => b.Quote is { Lines.Count: > 0 }).ToList()));
        }

        var payable = groups.Where(g => g.Boxes.Count > 0).ToList();
        var total = payable.Sum(g => g.Boxes.Sum(b => b.Quote!.Total));
        if (total == 0) return new TruckPaymentResult(TruckPaymentOutcome.NothingDue, null, []);
        if (total != request.ExpectedTotal)
            return Refused(409, "The price changed.",
                $"The gate shows ฿{request.ExpectedTotal:N2}; it is now ฿{total:N2}. Refresh the charges and confirm the new amount.", "expectedTotal");

        // ── the money ───────────────────────────────────────────────────────
        foreach (var p in request.Payments)
        {
            var channel = p.Channel?.Trim().ToUpperInvariant() ?? "";
            if (!Channels.Contains(channel)) return Refused(400, $"'{p.Channel}' is not a channel ({string.Join(", ", Channels)}).", null, "payments");
            if (p.Amount <= 0) return Refused(400, "Every payment must be more than zero.", null, "payments");
            if (channel == "CASH" && p.TenderedAmount is { } tendered && tendered < p.Amount) return Refused(400, "Cash handed over is less than the amount.", null, "payments");
            if (channel != "CASH" && string.IsNullOrWhiteSpace(p.ReferenceNo)) return Refused(400, $"A {channel} payment needs its reference (slip, cheque or approval number).", null, "payments");
        }
        if (request.Payments.Count == 0) return Refused(400, "How was it paid?", null, "payments");

        var withheld = 0m;
        if (request.WithholdingTax)
        {
            if (!await window.WithholdingOfferedAsync(request.BranchId, total, ct))
                return Refused(400, $"Withholding tax may be applied only on a receipt over ฿{WithholdingTax.Threshold:N0}; this one is ฿{total:N2}.", null, "withholdingTax");
            withheld = WithholdingTax.Amount(payable.Sum(g => g.Boxes.Sum(b => b.Quote!.Subtotal)));
        }
        var paid = request.Payments.Sum(p => p.Amount);
        if (paid != total - withheld)
            return Refused(400, withheld > 0
                ? $"Payments add up to ฿{paid:N2}; the receipt is ฿{total:N2} less ฿{withheld:N2} withholding tax = ฿{total - withheld:N2}."
                : $"Payments add up to ฿{paid:N2}; the receipt is ฿{total:N2}.", null, "payments");

        var shift = await db.Shifts.AsNoTracking()
            .SingleOrDefaultAsync(s => s.BranchId == request.BranchId && s.CashierUserId == request.CashierUserId && s.Status == "OPEN", ct);
        if (shift is null) return Refused(409, "Your drawer is not open at this branch.", "Open it (POST /api/revenue/window/shifts) before taking payment.");

        var payerName = request.Payer?.Name?.Trim();
        if (string.IsNullOrEmpty(payerName) && payable.Count == 1 && payable[0].Context.Plan.CustomerPartyCode is { } customer)
            payerName = (await master.PartiesAsync([customer], ct)).GetValueOrDefault(customer)?.Name;

        try
        {
            var payer = request.Payer is { } p ? new PayerRequest(p.Name, p.TaxId, p.BranchNo, p.Address) : null;
            var payments = request.Payments.Select(x => new PaymentRequest(x.Channel, x.Amount, x.TenderedAmount, x.ReferenceNo, x.BankName)).ToList();
            var (receipt, _) = await window.IssueReceiptAsync(payable, shift, payments,
                string.IsNullOrEmpty(payerName) ? "Walk-in customer" : payerName, payer, request.CashierUserId, null, ct,
                withheld, request.IdempotencyKey, hash);
            return (await ReplayAsync(request.IdempotencyKey, hash, ct))!;
        }
        catch (DbUpdateException e) when (e.InnerException?.Message.Contains("uq_receipt__idempotency_key") == true)
        {
            db.ChangeTracker.Clear();
            return await ReplayAsync(request.IdempotencyKey, hash, ct)
                   ?? Refused(409, "This Save is still being processed.", "Repeat it in a moment.");
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Refused(409, "Already paid.", "Another receipt for these boxes was just issued. Refresh the charges.");
        }
    }

    /// <summary>The receipt an earlier call with this key issued, with its coupons; null when the key is new.</summary>
    private async Task<TruckPaymentResult?> ReplayAsync(string key, byte[] hash, CancellationToken ct)
    {
        var receipt = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.IdempotencyKey == key, ct);
        if (receipt is null) return null;
        if (!Idempotency.SameRequest(receipt.IdempotencyHash, hash))
            return Refused(422, $"{Idempotency.Header} {key} was already used with a different Save.", "Send a new key for a new Save.");

        var coupons = await db.Charges.AsNoTracking()
            .Where(c => c.ReceiptId == receipt.ReceiptId && c.CouponRef != null)
            .GroupBy(c => new { c.BookingId, c.BookingContainerId, c.ContainerNo, c.MovementCode, c.CouponRef })
            .Select(g => new TruckCoupon(g.Key.BookingId ?? Guid.Empty, g.Key.BookingContainerId ?? Guid.Empty, g.Key.ContainerNo, g.Key.MovementCode ?? "", g.Key.CouponRef!,
                g.Sum(c => c.Amount + c.TaxAmount)))
            .ToListAsync(ct);
        return new TruckPaymentResult(TruckPaymentOutcome.Paid,
            new TruckReceipt(receipt.ReceiptId, receipt.ReceiptNo, receipt.SubtotalAmount, receipt.TaxAmount, receipt.TotalAmount,
                receipt.WithholdingTaxAmount, receipt.CurrencyCode),
            coupons.OrderBy(c => c.CouponRef).ToList());
    }

    private static TruckPaymentResult Refused(int status, string title, string? detail, string? field = null) =>
        new(TruckPaymentOutcome.Refused, null, [], new TruckPaymentProblem(status, title, detail, field));
}
