using Gecko.MasterData.Contracts;
using Gecko.SharedKernel;
using Gecko.Tos.Domain;
using Gecko.Tos.Endpoints;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Application;

/// <summary>
/// A box the booking did not name, keyed at the gate (owner 2026-10-06; Vector GateIn.cs:2887-2935 and
/// Operation.usp_BookingContainerMovement, which writes the number onto the booking line on Save).
///
/// The empty places of a booking are alike when they share the booking, the equipment type, the pick-up /
/// drop-off mode and the next step. So when the place a clerk picked is taken — another truck holds it, or
/// another clerk's Save filled it a moment ago — the box goes to the next free place like it (owner: "a",
/// automatically, with a note), and only when none is left is the clerk refused. A place that already
/// carries a number is never overwritten.
///
/// Concurrency: <see cref="NominateAsync"/> runs under the booking's update lock, and
/// booking.uq_booking_container__active keeps one number on one live line, so two Saves in the same
/// instant cannot both write one place or one box.
/// </summary>
internal static class GateNominations
{
    public const string Source = "GATE";

    /// <summary>A booking line, with what decides whether another line is "like" it.</summary>
    internal sealed record Place
    {
        public Guid BookingContainerId { get; init; }
        public Guid BookingId { get; init; }
        public string OrderNo { get; init; } = "";
        public Guid BranchId { get; init; }
        public string BookingStatus { get; init; } = "";
        public string BookingTypeCode { get; init; } = "";
        public string OrderTypeCode { get; init; } = "";
        public string EquipmentTypeCode { get; init; } = "";
        public string? HandoverModeCode { get; init; }
        public string? ContainerNo { get; init; }
        public string Source { get; init; } = "";
        public DateTimeOffset? EndedAt { get; init; }
        public short LineNo { get; init; }
        public DateTimeOffset AssignedAt { get; init; }
    }

    /// <summary>Where a box goes: the place, the place it was moved from (null = the one asked for), or why not.</summary>
    /// <param name="Written">The number was written onto the place by this call (so a Save that stops takes it off again).</param>
    internal sealed record Choice(Place? Place, Guid? SwitchedFrom, string? Code, string? Problem, bool Written = false)
    {
        public string? Note => Place is null || SwitchedFrom is null ? null
            : $"The place picked was taken by another truck; using the next free place like it (line {Place.LineNo}, {Place.EquipmentTypeCode}) on {Place.OrderNo}.";
    }

    public static Task<Place?> PlaceAsync(TosDbContext db, Guid bookingContainerId, CancellationToken ct) =>
        Places(db).Where(p => p.BookingContainerId == bookingContainerId).SingleOrDefaultAsync(ct);

    private static IQueryable<Place> Places(TosDbContext db) =>
        from x in db.BookingContainers.AsNoTracking()
        join b in db.Bookings on x.BookingId equals b.BookingId
        join r in db.EquipmentRequirements on x.EquipmentRequirementId equals r.EquipmentRequirementId
        select new Place
        {
            BookingContainerId = x.BookingContainerId, BookingId = x.BookingId, OrderNo = b.OrderNo, BranchId = b.BranchId,
            BookingStatus = b.Status, BookingTypeCode = b.BookingTypeCode, OrderTypeCode = b.OrderTypeCode,
            EquipmentTypeCode = r.EquipmentTypeCode, HandoverModeCode = x.HandoverModeCode, ContainerNo = x.ContainerNo,
            Source = x.AssignmentSource, EndedAt = x.EndedAt, LineNo = r.LineNo, AssignedAt = x.AssignedAt,
        };

    /// <summary>
    /// The place <paramref name="containerNo"/> goes to: the line of this booking it is already on (a retry), the
    /// place asked for when it is free, else the first free place like it. Null <paramref name="containerNo"/> =
    /// the clerk picked a place before keying the box.
    /// </summary>
    public static async Task<Choice> ChooseAsync(TosDbContext db, Place wanted, string? containerNo, Guid? draftId,
        DateTimeOffset now, CancellationToken ct)
    {
        if (containerNo is not null
            && await Places(db).FirstOrDefaultAsync(p => p.BookingId == wanted.BookingId && p.ContainerNo == containerNo && p.EndedAt == null, ct) is { } already)
            return new Choice(already, already.BookingContainerId == wanted.BookingContainerId ? null : wanted.BookingContainerId, null, null);

        // A place named for another box by the booking (not by a gate a moment ago) is that box's: a typo, not a race.
        if (wanted.ContainerNo is not null && wanted.Source != Source && wanted.EndedAt is null)
            return new Choice(null, null, "PLACE_IS_FOR_ANOTHER_BOX",
                $"That place on {wanted.OrderNo} is for {wanted.ContainerNo}, not {containerNo}. Pick an empty place, or check the number.");

        var step = await NextStepAsync(db, wanted.BookingContainerId, ct);
        if (await IsFreeAsync(db, wanted, step, draftId, now, ct)) return new Choice(wanted, null, null, null);

        var siblings = await Places(db)
            .Where(p => p.BookingId == wanted.BookingId && p.BookingContainerId != wanted.BookingContainerId
                        && p.EquipmentTypeCode == wanted.EquipmentTypeCode && p.HandoverModeCode == wanted.HandoverModeCode
                        && p.ContainerNo == null && p.EndedAt == null)
            .OrderBy(p => p.LineNo).ThenBy(p => p.AssignedAt).ThenBy(p => p.BookingContainerId)
            .ToListAsync(ct);
        foreach (var sibling in siblings)
            if (await NextStepAsync(db, sibling.BookingContainerId, ct) == step && await IsFreeAsync(db, sibling, step, draftId, now, ct))
                return new Choice(sibling, wanted.BookingContainerId, null, null);

        return new Choice(null, null, "NO_FREE_PLACE",
            $"No free {wanted.EquipmentTypeCode} place is left on {wanted.OrderNo}{(wanted.HandoverModeCode is null ? "" : $" ({wanted.HandoverModeCode})")}: other trucks hold or filled them. Add a place to the booking, or use another booking.");
    }

    /// <summary>Empty (or this box's), not ended, nothing done on it yet, at the same next step, and not held by another truck.</summary>
    private static async Task<bool> IsFreeAsync(TosDbContext db, Place place, string? step, Guid? draftId, DateTimeOffset now, CancellationToken ct) =>
        place.EndedAt is null && place.ContainerNo is null
        && !await db.MovementPlans.AnyAsync(m => m.BookingContainerId == place.BookingContainerId && m.Status == "DONE", ct)
        && await NextStepAsync(db, place.BookingContainerId, ct) == step
        && await BoxReservations.HeldByOtherAsync(db, place.BookingContainerId, null, draftId, now, ct) is null;

    private static Task<string?> NextStepAsync(TosDbContext db, Guid bookingContainerId, CancellationToken ct) =>
        db.MovementPlans.AsNoTracking().Where(m => m.BookingContainerId == bookingContainerId && m.Status == "PENDING")
            .OrderBy(m => m.SequenceNo).Select(m => m.MovementCode).FirstOrDefaultAsync(ct);

    /// <summary>
    /// What would refuse <paramref name="containerNo"/> on <paramref name="place"/> before anything is written: the
    /// number itself, another booking, another truck, the yard rules for the place's next step, and its type.
    /// </summary>
    public static async Task<List<GateFinding>> CheckAsync(TosDbContext db, IMasterDataReferences master, BranchClock clock,
        Place place, string containerNo, Guid? draftId, DateTimeOffset now, CancellationToken ct)
    {
        var findings = new List<GateFinding>();
        var digitOk = ContainerNumber.IsValid(containerNo);
        var enforce = await master.GetBoolSettingAsync(TosSettingKeys.EnforceCheckDigit, place.BranchId, false, ct);
        if (GateRules.CheckDigit(containerNo, digitOk, enforce, ContainerNumber.CheckDigitOf(containerNo)) is { } digit) findings.Add(digit);

        var elsewhere = await Places(db).FirstOrDefaultAsync(p => p.ContainerNo == containerNo && p.EndedAt == null && p.BookingId != place.BookingId, ct);
        if (elsewhere is not null)
            findings.Add(new GateFinding("ON_ANOTHER_BOOKING", $"{containerNo} is on booking {elsewhere.OrderNo}. A box is on one booking at a time.", GateSeverity.Block));

        if (await BoxReservations.HeldByOtherAsync(db, null, containerNo, draftId, now, ct) is { } held)
            findings.Add(BoxReservations.Finding(held, null));

        var stepCode = await NextStepAsync(db, place.BookingContainerId, ct);
        var step = (await master.OrderTypePlansAsync([place.OrderTypeCode], ct)).GetValueOrDefault(place.OrderTypeCode)?
            .Steps.FirstOrDefault(s => s.MovementCode == stepCode);
        if (step is not null)
        {
            var visit = await db.ContainerVisits.AsNoTracking()
                .SingleOrDefaultAsync(v => v.ContainerNo == containerNo && v.GateOutTransactionId == null, ct);
            var yardCode = visit is null ? null : (await clock.BranchesAsync([visit.BranchId], ct)).GetValueOrDefault(visit.BranchId)?.BranchCode;
            findings.AddRange(GateRules.Yard(containerNo, step.Direction, step.FullEmpty, visit is not null, visit?.BranchId == place.BranchId,
                yardCode, visit?.FullEmpty));
        }

        // The registry's type against the place (Vector GateIn.cs:3033-3063): an IMPORT box may differ (A11 changes the booking).
        var registry = (await master.ContainersAsync([containerNo], ct)).GetValueOrDefault(containerNo);
        var line = await db.Bookings.AsNoTracking().Where(b => b.BookingId == place.BookingId)
            .Select(b => new { b.LinePartyId, b.LinePartyCode }).SingleAsync(ct);
        if (GateRules.OwnerMismatch(containerNo, registry?.OwnerPartyId, line.LinePartyId, line.LinePartyCode, place.OrderNo,
                await master.GetBoolSettingAsync(TosSettingKeys.RefuseOwnerMismatch, place.BranchId, true, ct)) is { } owner)
            findings.Add(owner);
        if (registry is null && !await master.GetBoolSettingAsync(TosSettingKeys.AllowUnknownContainer, place.BranchId, true, ct))
            findings.Add(new GateFinding("UNKNOWN_CONTAINER", $"{containerNo} is not in the container registry, and this depot does not accept unknown boxes.", GateSeverity.Block));
        if (registry?.EquipmentTypeCode is { } type && !string.Equals(type, place.EquipmentTypeCode, StringComparison.OrdinalIgnoreCase))
            findings.Add(place.BookingTypeCode == "IMPORT"
                ? new GateFinding("TYPE_DIFFERS", $"{containerNo} is a {type} in the registry; the place is {place.EquipmentTypeCode}. Confirm the type on Save.", GateSeverity.Warn)
                : new GateFinding("TYPE_MISMATCH", $"{containerNo} is a {type}; this place on {place.OrderNo} is for a {place.EquipmentTypeCode}.", GateSeverity.Block));
        return findings;
    }

    /// <summary>
    /// Writes <paramref name="containerNo"/> onto a free place like <paramref name="wanted"/>, under the booking lock
    /// (the choice is made again inside it, so a place another Save filled a moment ago is seen). The place written,
    /// or why not.
    /// </summary>
    public static async Task<Choice> NominateAsync(TosDbContext db, IMasterDataReferences master, Guid wanted, string containerNo,
        Guid? draftId, ITenantContext caller, TimeProvider time, CancellationToken ct)
    {
        var place = await PlaceAsync(db, wanted, ct);
        if (place is null) return new Choice(null, null, "UNKNOWN_PLACE", "Unknown booked box.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM booking.booking WITH (UPDLOCK, HOLDLOCK) WHERE booking_id = {place.BookingId}", ct);
        place = (await PlaceAsync(db, wanted, ct))!;
        var now = time.GetUtcNow();
        var choice = await ChooseAsync(db, place, containerNo, draftId, now, ct);
        if (choice.Place is not { } chosen) return choice;
        if (chosen.ContainerNo == containerNo) { await tx.CommitAsync(ct); return choice; }   // a retry: already written

        var registry = (await master.ContainersAsync([containerNo], ct)).GetValueOrDefault(containerNo);
        var box = await db.BookingContainers.SingleAsync(x => x.BookingContainerId == chosen.BookingContainerId, ct);
        box.ContainerNo = containerNo;
        box.ContainerId = registry?.ContainerId;
        box.IsCheckDigitValid = ContainerNumber.IsValid(containerNo);
        box.AssignmentSource = Source;
        box.AssignedAt = now;
        box.AssignedBy = caller.UserId();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException?.Message.Contains("uq_booking_container__active") == true)
        {
            db.ChangeTracker.Clear();
            return new Choice(null, null, "ON_ANOTHER_BOOKING", $"{containerNo} was put on another booking a moment ago.");
        }
        await BookingEvents.QueueChangedAsync(db, chosen.BookingId, "CONTAINER_CHANGED", now, ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return choice with { Place = chosen with { ContainerNo = containerNo, Source = Source }, Written = true };
    }

    /// <summary>The Save stopped before the box moved: the number it wrote comes off the place again.</summary>
    public static async Task UndoAsync(TosDbContext db, Guid bookingContainerId, string containerNo, TimeProvider time, CancellationToken ct)
    {
        var box = await db.BookingContainers.SingleOrDefaultAsync(x => x.BookingContainerId == bookingContainerId, ct);
        if (box is null || box.ContainerNo != containerNo || box.AssignmentSource != Source || box.EndedAt is not null) return;
        if (await db.MovementPlans.AnyAsync(m => m.BookingContainerId == bookingContainerId && m.Status == "DONE", ct)) return;
        box.ContainerNo = null;
        box.ContainerId = null;
        await db.SaveChangesAsync(ct);
        await BookingEvents.QueueChangedAsync(db, box.BookingId, "CONTAINER_CHANGED", time.GetUtcNow(), ct);
        db.ChangeTracker.Clear();
    }
}
