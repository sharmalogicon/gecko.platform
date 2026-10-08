using System.Text.Json;
using Gecko.Data;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Application;

/// <summary>
/// TOS <c>BookingCloned</c> (owner 2026-10-07): the clone's statement carries what the clerk
/// put on the source's by hand.
///   * MANUAL lines (not cancelled, not waived) are added again, QUOTED, at the same price —
///     on the place that copies their box, or on the booking when they had no box. A line on
///     a box the clone did not copy is left behind.
///   * Price corrections (original rate + discount) on the source's quoted lines are set on
///     the clone's matching QUOTED line — same place, move, charge, bill-to and term — with
///     the lock. Automatic re-quotes keep a correction, so it stays.
/// Arrives after the clone's BookingChanged; until Revenue has the clone's plan it fails and
/// the outbox tries again.
/// </summary>
internal sealed class BookingClonedHandler(RevenueDbContext db, BranchCalendar calendar) : IOutboxHandler
{
    public const string BookingCloned = "BookingCloned";

    public bool CanHandle(string messageType) => messageType == BookingCloned;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        if (await Inbox.AlreadyHandledAsync(db, message, ct)) return;

        var cloned = JsonSerializer.Deserialize<ClonedPayload>(message.PayloadJson, Inbox.Json)
                     ?? throw new InvalidOperationException($"Outbox message {message.MessageId} carries no clone.");
        var plan = await db.BookingPlans.AsNoTracking().SingleOrDefaultAsync(p => p.BookingId == cloned.BookingId, ct)
                   ?? throw new InvalidOperationException($"Booking {cloned.BookingId} is not in Revenue yet; its BookingChanged comes first.");

        var boxOf = cloned.Containers.ToDictionary(c => c.SourceBookingContainerId, c => c.BookingContainerId);
        var source = await db.Charges.AsNoTracking()
            .Where(c => c.BookingId == cloned.SourceBookingId && c.Status != ChargeStatus.Cancelled && c.Status != ChargeStatus.Waived)
            .OrderBy(c => c.CreatedAt).ToListAsync(ct);
        var quoted = await db.Charges
            .Where(c => c.BookingId == cloned.BookingId && c.Source == ChargeSource.Quote && c.Status == ChargeStatus.Quoted)
            .ToListAsync(ct);
        var now = calendar.Now;
        var sourceOrderNo = await db.BookingPlans.AsNoTracking().Where(p => p.BookingId == cloned.SourceBookingId).Select(p => p.OrderNo).SingleOrDefaultAsync(ct);
        var note = $"Cloned from {sourceOrderNo ?? "the source booking"}";
        int manual = 0, corrected = 0;

        foreach (var s in source.Where(c => c.Source == ChargeSource.Manual))
        {
            Guid? box = null;
            if (s.BookingContainerId is { } from)
            {
                if (!boxOf.TryGetValue(from, out var to)) continue;
                box = to;
            }
            var c = new Charge
            {
                ChargeId = Guid.CreateVersion7(), TenantId = plan.TenantId, BranchId = plan.BranchId, Source = ChargeSource.Manual,
                BookingId = plan.BookingId, OrderNo = plan.OrderNo, BookingContainerId = box, MovementCode = s.MovementCode,
                ChargeCodeId = s.ChargeCodeId, ChargeCode = s.ChargeCode, ChargeName = s.ChargeName,
                BillTo = s.BillTo, PaymentTermCode = s.PaymentTermCode, PayerPartyCode = CashQuoter.PayerFor(plan, s.BillTo),
                Quantity = s.Quantity, CurrencyCode = s.CurrencyCode, TaxCode = s.TaxCode, TaxRate = s.TaxRate,
                BillingUnitCode = s.BillingUnitCode, Status = ChargeStatus.Quoted, CreatedAt = now,
            };
            CopyPrice(c, s, note, now);
            db.Charges.Add(c);
            manual++;
        }

        foreach (var s in source.Where(c => c.Source != ChargeSource.Manual && c.IsRateOverridden))
        {
            if (s.BookingContainerId is not { } from || !boxOf.TryGetValue(from, out var to)) continue;
            var target = quoted.FirstOrDefault(q => !q.IsRateOverridden && q.BookingContainerId == to && q.MovementCode == s.MovementCode
                && q.ChargeCode == s.ChargeCode && q.BillTo == s.BillTo && q.PaymentTermCode == s.PaymentTermCode);
            if (target is null) continue;
            CopyPrice(target, s, note, now);
            corrected++;
        }

        Inbox.Record(db, message, note: $"{manual} manual line(s), {corrected} correction(s) from {cloned.SourceBookingId}.");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The source line's price (original rate + discount → selling rate), its reason and lock.</summary>
    private static void CopyPrice(Charge c, Charge s, string note, DateTimeOffset now)
    {
        c.UnitRateOriginal = s.UnitRateOriginal;
        c.DiscountType = s.DiscountType;
        c.DiscountRate = s.DiscountRate;
        c.UnitRate = s.UnitRate;
        c.Amount = c.UnitRate is { } rate ? CashQuoter.Money(c.Quantity * rate) : 0m;
        c.TaxAmount = CashQuoter.Money(c.Amount * c.TaxRate / 100m);
        c.IsRateOverridden = true;
        c.OverrideReason = Truncate(s.OverrideReason is { Length: > 0 } why ? $"{why} ({note})" : note, 500);
        c.OverriddenBy = s.OverriddenBy;
        c.OverriddenAt = now;
        c.IsLocked = s.IsLocked;
        c.UpdatedAt = now;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private sealed record ClonedPayload(Guid BookingId, Guid SourceBookingId, List<ClonedBox> Containers);

    private sealed record ClonedBox(Guid BookingContainerId, Guid SourceBookingContainerId);
}
