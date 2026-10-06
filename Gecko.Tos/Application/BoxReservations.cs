using Gecko.Tos.Domain;
using Gecko.Tos.Infrastructure.Persistence;
using Gecko.Tos.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Application;

/// <summary>
/// A gate clerk's hold on a box while a truck is being keyed (owner 2026-10-04, gecko_tos 24,
/// GATE_IN_BIG_SAVE.md §1). One live hold per box; the same draft holding it again is the same
/// hold, extended. A lapsed hold is released here, lazily, by the next request that touches the
/// box — so a crashed screen never freezes a box for good.
/// </summary>
internal static class BoxReservations
{
    /// <summary>How long a hold lasts; each Record or price refresh extends it.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromMinutes(15);

    /// <summary>A box a truck came to collect is held for it from gate in until it leaves with it (owner 2026-10-06).</summary>
    public static readonly TimeSpan PickupHold = TimeSpan.FromHours(24);

    public const string Removed = "REMOVED";
    public const string Expired = "EXPIRED";
    public const string Gated = "GATED";

    /// <summary>The live holds on a box (by its booking line, its number, or both), lapsed ones released first.</summary>
    public static async Task<List<BoxReservation>> LiveAsync(
        TosDbContext db, Guid? bookingContainerId, string? containerNo, DateTimeOffset now, CancellationToken ct)
    {
        if (bookingContainerId is null && containerNo is null) return [];
        var rows = await db.BoxReservations
            .Where(r => r.ReleasedAt == null
                        && ((bookingContainerId != null && r.BookingContainerId == bookingContainerId)
                            || (containerNo != null && r.ContainerNo == containerNo)))
            .ToListAsync(ct);
        foreach (var lapsed in rows.Where(r => r.ExpiresAt <= now))
            Release(lapsed, Expired, null, now);
        return rows.Where(r => r.ExpiresAt > now).ToList();
    }

    /// <summary>The live hold another draft has on the box, or null when it is free or this draft's own.</summary>
    public static async Task<BoxReservation?> HeldByOtherAsync(
        TosDbContext db, Guid? bookingContainerId, string? containerNo, Guid? draftId, DateTimeOffset now, CancellationToken ct) =>
        (await LiveAsync(db, bookingContainerId, containerNo, now, ct)).FirstOrDefault(r => r.DraftId != draftId);

    /// <summary>The finding the picker, the blind order and the barrier give for a box someone else holds.</summary>
    public static GateFinding Finding(BoxReservation hold, string? holderName) =>
        new("BOX_RESERVED",
            $"{hold.ContainerNo ?? "This box"} is on another truck being keyed{(holderName is null ? "" : $" by {holderName}")} until {hold.ExpiresAt:HH:mm} UTC.",
            GateSeverity.Block);

    /// <summary>The box went through the gate: its holds are done.</summary>
    public static async Task ConsumeAsync(TosDbContext db, Guid bookingContainerId, string containerNo, Guid gateTransactionId,
        Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var hold in await LiveAsync(db, bookingContainerId, containerNo, now, ct))
        {
            Release(hold, Gated, userId, now);
            hold.GateTransactionId = gateTransactionId;
        }
    }

    public static void Release(BoxReservation hold, string reason, Guid? userId, DateTimeOffset now)
    {
        hold.ReleasedAt = now;
        hold.ReleasedBy = userId;
        hold.ReleaseReason = reason;
        hold.UpdatedAt = now;
        hold.UpdatedBy = userId;
    }
}
