using System.Text.Json;
using Gecko.Data;
using Gecko.Tos.Infrastructure.Persistence;
using Gecko.Tos.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gecko.Tos.Application;

/// <summary>
/// Revenue's <c>GateCouponIssued</c> / <c>GateCouponRevoked</c> → TOS's own
/// <c>gate.gate_authorization</c> (ADR-007, PLAN_BILLING §4.2 step 4). The barrier
/// reads the coupon locally and never asks Revenue whether a box is paid.
///
/// Runs in a scope acting as the message's tenant (Gecko.Data's dispatcher), so
/// it writes through the ordinary RLS-bound TosDbContext.
///
/// Idempotent: the coupon's id IS the row's id and its source_event_id, so a
/// replayed message finds its row and stops.
/// </summary>
internal sealed class CouponHandler(TosDbContext db, TimeProvider clock, ILogger<CouponHandler> log) : IOutboxHandler
{
    public const string Issued = "GateCouponIssued";
    public const string Revoked = "GateCouponRevoked";

    private static readonly JsonSerializerOptions Payload = new() { PropertyNameCaseInsensitive = true };

    public bool CanHandle(string messageType) => messageType is Issued or Revoked;

    public Task HandleAsync(OutboxMessage message, CancellationToken ct) =>
        message.MessageType == Issued ? IssueAsync(message, ct) : RevokeAsync(message, ct);

    private async Task IssueAsync(OutboxMessage message, CancellationToken ct)
    {
        var coupon = JsonSerializer.Deserialize<CouponIssued>(message.PayloadJson, Payload)
                     ?? throw new InvalidOperationException($"Outbox message {message.MessageId} carries no coupon.");

        if (await db.GateAuthorizations.AnyAsync(a => a.SourceEventId == coupon.CouponId, ct))
            return;   // replayed

        var now = clock.GetUtcNow();

        // One LIVE coupon per booking + box + movement (uq_gate_authorization__live).
        // A new payment for the same move — the customer came back after the
        // paid-until date and paid the extra days — replaces the old one.
        var live = await db.GateAuthorizations.SingleOrDefaultAsync(a =>
            a.BookingId == coupon.BookingId && a.ContainerNo == coupon.ContainerNo && a.MovementCode == coupon.MovementCode
            && a.ConsumedByGateTransactionId == null && a.RevokedAt == null, ct);
        if (live is not null)
        {
            if (coupon.IssuedBy is not { } by)
            {
                // An automatic coupon (nothing cash due) never overrides a live one.
                log.LogInformation("Coupon {Ref} not written: {Existing} is already live for {Box} {Movement}.",
                    coupon.CouponRef, live.CouponRef, coupon.ContainerNo, coupon.MovementCode);
                return;
            }
            live.RevokedAt = now;
            live.RevokedBy = by;
            live.RevokeReason = $"Superseded by {coupon.CouponRef}.";
            live.UpdatedAt = now;
        }

        db.GateAuthorizations.Add(new GateAuthorization
        {
            GateAuthorizationId = coupon.CouponId,
            TenantId = message.TenantId,
            BranchId = coupon.BranchId,
            BookingId = coupon.BookingId,
            ContainerNo = coupon.ContainerNo,
            MovementCode = coupon.MovementCode,
            CouponRef = coupon.CouponRef,
            PaymentChannel = coupon.Channel,
            Amount = coupon.Amount,
            CurrencyCode = coupon.Amount is null ? null : coupon.CurrencyCode,
            ValidFrom = coupon.ValidFrom,
            ValidUntil = coupon.ValidUntil,
            SourceEventId = coupon.CouponId,
            CreatedBy = coupon.IssuedBy,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task RevokeAsync(OutboxMessage message, CancellationToken ct)
    {
        var revoke = JsonSerializer.Deserialize<CouponRevoked>(message.PayloadJson, Payload)
                     ?? throw new InvalidOperationException($"Outbox message {message.MessageId} carries no revocation.");

        var coupon = await db.GateAuthorizations.SingleOrDefaultAsync(a => a.SourceEventId == revoke.CouponId, ct);
        if (coupon is null || coupon.RevokedAt is not null)
            return;

        if (coupon.ConsumedByGateTransactionId is not null)
        {
            // The box already went through on it. Nothing to take back at the
            // barrier; Revenue flags the receipt for a credit note.
            log.LogWarning("Coupon {Ref} was revoked after it was spent at the gate.", coupon.CouponRef);
            return;
        }

        var now = clock.GetUtcNow();
        coupon.RevokedAt = now;
        coupon.RevokedBy = revoke.RevokedBy;
        coupon.RevokeReason = revoke.Reason;
        coupon.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The shape Revenue writes (Gecko.Revenue WindowService). Read, never interpreted beyond this.</summary>
    private sealed record CouponIssued(
        Guid CouponId, Guid BranchId, Guid BookingId, string? ContainerNo, string MovementCode,
        string CouponRef, string Channel, decimal? Amount, string? CurrencyCode,
        DateTimeOffset ValidFrom, DateTimeOffset ValidUntil, Guid? IssuedBy);

    private sealed record CouponRevoked(Guid CouponId, Guid RevokedBy, string Reason);
}
