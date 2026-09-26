using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gecko.Revenue.Application;

/// <summary>
/// The idempotency both handlers share: a TOS message is applied once. The inbox
/// row goes in the SAME transaction as the change it records, so a handler that
/// dies halfway leaves neither, and the redelivered message starts clean.
/// </summary>
internal static class Inbox
{
    public static async Task<bool> AlreadyHandledAsync(RevenueDbContext db, OutboxMessage message, CancellationToken ct) =>
        await db.Inboxes.AnyAsync(i => i.SourceContext == "TOS" && i.MessageId == message.MessageId, ct);

    public static void Record(RevenueDbContext db, OutboxMessage message, string outcome = "APPLIED", string? note = null) =>
        db.Inboxes.Add(new Infrastructure.Persistence.Entities.Inbox
        {
            TenantId = message.TenantId,
            SourceContext = "TOS",
            MessageId = message.MessageId,
            MessageType = message.MessageType,
            AggregateId = message.AggregateId,
            Outcome = outcome,
            Note = note,
        });

    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
}

/// <summary>
/// TOS <c>BookingChanged</c> → Revenue's own copy of the booking (PLAN_BILLING
/// §4.2 step 0; ADR-007: Revenue never reads gecko_tos). The message carries the
/// whole booking, so the highest message id wins and a missed one heals itself.
///
/// Then the automatic coupon: a billable movement that can owe NO cash — nothing
/// in the order type prices in cash, and no storage on the way out — gets its
/// coupon now, channel CREDIT, so the truck never queues at the window for
/// nothing. Its id is derived from the box and the movement, so the next
/// BookingChanged re-sends the same coupon and TOS keeps one.
/// </summary>
internal sealed class BookingChangedHandler(
    RevenueDbContext db, BranchCalendar calendar, AutomaticCoupons coupons) : IOutboxHandler
{
    public const string BookingChanged = "BookingChanged";

    public bool CanHandle(string messageType) => messageType == BookingChanged;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        if (await Inbox.AlreadyHandledAsync(db, message, ct)) return;

        var booking = JsonSerializer.Deserialize<BookingPayload>(message.PayloadJson, Inbox.Json)
                      ?? throw new InvalidOperationException($"Outbox message {message.MessageId} carries no booking.");

        var plan = await db.BookingPlans.SingleOrDefaultAsync(p => p.BookingId == booking.BookingId, ct);
        if (plan is not null && plan.LastMessageId >= message.MessageId)
        {
            Inbox.Record(db, message, "STALE", $"Plan already at message {plan.LastMessageId}.");
            await db.SaveChangesAsync(ct);
            return;
        }

        if (plan is null)
        {
            plan = new BookingPlan { BookingId = booking.BookingId, TenantId = message.TenantId };
            db.BookingPlans.Add(plan);
        }

        plan.BranchId = booking.BranchId;
        plan.OrderNo = booking.OrderNo;
        plan.Status = booking.Status;
        plan.OrderTypeCode = booking.OrderTypeCode;
        plan.BookingTypeCode = booking.BookingTypeCode;
        plan.DirectionCode = booking.DirectionCode;
        plan.LineCode = booking.LineCode;
        plan.AgentPartyCode = booking.AgentPartyCode;
        plan.CustomerPartyCode = booking.CustomerCode;
        plan.ForwarderPartyCode = booking.ForwarderPartyCode;
        plan.HaulierPartyCode = booking.HaulierPartyCode;
        plan.CargoClassCode = booking.CargoClassCode;
        plan.CargoCategoryCode = booking.CargoCategoryCode;
        plan.ValidFrom = booking.ValidFrom;
        plan.ValidTo = booking.ValidTo;
        plan.RequirementsJson = JsonSerializer.Serialize(booking.Requirements);
        plan.LastMessageId = message.MessageId;
        plan.LastReason = booking.Reason;
        plan.ChangedAt = booking.ChangedAt;
        plan.PayloadJson = message.PayloadJson;
        plan.UpdatedAt = calendar.Now;

        var requirements = booking.Requirements.ToDictionary(r => r.EquipmentRequirementId);
        var existing = await db.BookingPlanContainers.Where(c => c.BookingId == booking.BookingId).ToDictionaryAsync(c => c.BookingContainerId, ct);
        var boxes = new List<BookingPlanContainer>();

        foreach (var sent in booking.Containers)
        {
            if (!existing.TryGetValue(sent.BookingContainerId, out var box))
            {
                box = new BookingPlanContainer { BookingContainerId = sent.BookingContainerId, TenantId = message.TenantId, BookingId = booking.BookingId };
                db.BookingPlanContainers.Add(box);
            }
            var requirement = sent.EquipmentRequirementId is { } rid ? requirements.GetValueOrDefault(rid) : null;
            box.EquipmentRequirementId = sent.EquipmentRequirementId;
            box.ContainerNo = sent.ContainerNo;
            box.EquipmentTypeCode = requirement?.EquipmentTypeCode;
            box.IsDangerousGoods = requirement?.IsDangerousGoods ?? false;
            box.IsReefer = requirement?.IsReefer ?? false;
            box.DeclaredGrossWeightKg = requirement?.DeclaredGrossWeightKg;
            box.EndReason = sent.EndReason;
            box.StepsJson = JsonSerializer.Serialize(sent.Steps.Select(s => new PlanStep(s.SequenceNo, s.MovementCode, s.Status)));
            box.IsCurrent = true;
            box.UpdatedAt = calendar.Now;
            boxes.Add(box);
        }
        // A box that left the booking keeps its row, so a charge raised for it still points somewhere.
        foreach (var gone in existing.Values.Where(e => booking.Containers.All(c => c.BookingContainerId != e.BookingContainerId)))
        {
            gone.IsCurrent = false;
            gone.UpdatedAt = calendar.Now;
        }

        Inbox.Record(db, message);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        if (plan.Status == "OPEN")
            await coupons.IssueAsync(plan, boxes.Where(b => b.EndReason is null).ToList(), ct);
        await transaction.CommitAsync(ct);
    }

    private sealed record BookingPayload(
        Guid BookingId, Guid BranchId, string OrderNo, string Reason, DateTimeOffset ChangedAt, string Status,
        string OrderTypeCode, string? BookingTypeCode, string? DirectionCode, string? LineCode, string? AgentPartyCode,
        string? CustomerCode, string? ForwarderPartyCode, string? HaulierPartyCode, string? CargoClassCode,
        string? CargoCategoryCode, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo,
        List<RequirementPayload> Requirements, List<ContainerPayload> Containers);

    private sealed record RequirementPayload(
        Guid EquipmentRequirementId, short LineNo, string? EquipmentTypeCode, int Qty,
        bool IsDangerousGoods, bool IsReefer, decimal? DeclaredGrossWeightKg);

    private sealed record ContainerPayload(
        Guid BookingContainerId, Guid? EquipmentRequirementId, string? ContainerNo, string? EndReason, List<StepPayload> Steps);

    private sealed record StepPayload(short SequenceNo, string MovementCode, string Status);
}

/// <summary>
/// TOS gate events → Revenue (PLAN_BILLING §4.3, the part the cash window needs):
///   * the stay projection — a gate-in opens one, a gate-out closes it, a void
///     undoes whichever it was; storage is priced from it;
///   * the cash charges the spent coupon paid for become EARNED, and go back to
///     PAID if the EIR is voided (the coupon comes back unspent in TOS too).
/// Credit accrual from the gate event is 6.3.
/// </summary>
internal sealed class GateEventHandler(RevenueDbContext db, TimeProvider clock, AutomaticCoupons coupons, ILogger<GateEventHandler> log) : IOutboxHandler
{
    public const string GatedIn = "ContainerGatedIn";
    public const string GatedOut = "ContainerGatedOut";
    public const string Voided = "GateTransactionVoided";

    public bool CanHandle(string messageType) => messageType is GatedIn or GatedOut or Voided;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        if (await Inbox.AlreadyHandledAsync(db, message, ct)) return;

        var gate = JsonSerializer.Deserialize<GatePayload>(message.PayloadJson, Inbox.Json)
                   ?? throw new InvalidOperationException($"Outbox message {message.MessageId} carries no gate event.");
        var now = clock.GetUtcNow();
        string? note = null;

        if (message.MessageType == GatedIn)
        {
            var open = await db.ContainerStays.SingleOrDefaultAsync(s => s.ContainerNo == gate.ContainerNo && s.Status == "OPEN", ct);
            if (open is not null)
                note = $"A stay was already open from {open.InEirNo ?? open.InGateTransactionId.ToString()}; left as it is.";
            else
                db.ContainerStays.Add(new ContainerStay
                {
                    TenantId = message.TenantId, BranchId = gate.BranchId, ContainerNo = gate.ContainerNo,
                    EquipmentTypeCode = gate.EquipmentTypeCode, IsoCode = gate.IsoCode, LineCode = gate.LineCode,
                    FullEmptyIn = gate.FullEmpty ?? "EMPTY", InGateTransactionId = gate.GateTransactionId,
                    InEirNo = gate.EirNo, InBookingId = gate.BookingId, InAt = gate.TransactionAt,
                    Status = "OPEN", Source = "GATE_EVENT",
                });
            await EarnAsync(gate, now, ct);
            await MarkStepAsync(gate, "DONE", ct);
        }
        else if (message.MessageType == GatedOut)
        {
            var open = await db.ContainerStays.SingleOrDefaultAsync(s => s.ContainerNo == gate.ContainerNo && s.Status == "OPEN", ct);
            if (open is null)
                note = "No open stay to close (the box's arrival was never seen by Revenue).";
            else
            {
                open.Status = "CLOSED";
                open.FullEmptyOut = gate.FullEmpty;
                open.OutGateTransactionId = gate.GateTransactionId;
                open.OutEirNo = gate.EirNo;
                open.OutBookingId = gate.BookingId;
                open.OutAt = gate.TransactionAt < open.InAt ? open.InAt : gate.TransactionAt;
                open.UpdatedAt = now;
            }
            await EarnAsync(gate, now, ct);
            await MarkStepAsync(gate, "DONE", ct);
        }
        else
        {
            await MarkStepAsync(gate, "PENDING", ct);
            var opened = await db.ContainerStays.SingleOrDefaultAsync(s => s.InGateTransactionId == gate.GateTransactionId, ct);
            if (opened is not null && opened.Status != "CANCELLED")
            {
                opened.Status = "CANCELLED";
                opened.OutGateTransactionId = null; opened.OutEirNo = null; opened.OutBookingId = null; opened.OutAt = null; opened.FullEmptyOut = null;
                opened.UpdatedAt = now;
            }
            var closed = await db.ContainerStays.SingleOrDefaultAsync(s => s.OutGateTransactionId == gate.GateTransactionId, ct);
            if (closed is not null)
            {
                closed.Status = "OPEN";
                closed.OutGateTransactionId = null; closed.OutEirNo = null; closed.OutBookingId = null; closed.OutAt = null; closed.FullEmptyOut = null;
                closed.UpdatedAt = now;
            }

            foreach (var earned in await db.Charges.Where(c => c.EarnedGateTransactionId == gate.GateTransactionId && c.Status == ChargeStatus.Earned).ToListAsync(ct))
            {
                earned.Status = ChargeStatus.Paid;
                earned.EarnedGateTransactionId = null;
                earned.EarnedAt = null;
                earned.UpdatedAt = now;
            }
        }

        if (note is not null) log.LogInformation("Gate event {MessageId} ({Box}): {Note}", message.MessageId, gate.ContainerNo, note);
        Inbox.Record(db, message, note is null ? "APPLIED" : "IGNORED", note);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        // The box's NEXT step may owe no cash (SCT: the empty return after a FULL_OUT):
        // it gets its coupon now, as a BookingChanged would have given it.
        if (message.MessageType != Voided && gate.BookingContainerId is { } boxId
            && await db.BookingPlanContainers.AsNoTracking().SingleOrDefaultAsync(b => b.BookingContainerId == boxId, ct) is { } box
            && await db.BookingPlans.AsNoTracking().SingleOrDefaultAsync(p => p.BookingId == box.BookingId, ct) is { Status: "OPEN" } plan)
            await coupons.IssueAsync(plan, [box], ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// The gate event is what moves a box's step, and TOS does not send a
    /// BookingChanged for it — so Revenue's copy of the plan follows the gate
    /// event itself, or the window would keep quoting a movement already made.
    /// A void puts the step back to PENDING, as TOS does.
    /// </summary>
    private async Task MarkStepAsync(GatePayload gate, string status, CancellationToken ct)
    {
        if (gate.BookingContainerId is not { } boxId || gate.MovementCode is null) return;
        var box = await db.BookingPlanContainers.SingleOrDefaultAsync(b => b.BookingContainerId == boxId, ct);
        if (box is null) return;

        var steps = CashQuoter.Steps(box).ToList();
        var step = status == "DONE"
            ? steps.Where(p => p.MovementCode == gate.MovementCode && p.Status == "PENDING").MinBy(p => p.SequenceNo)
            : steps.Where(p => p.MovementCode == gate.MovementCode && p.Status == "DONE").MaxBy(p => p.SequenceNo);
        if (step is null) return;

        steps[steps.IndexOf(step)] = step with { Status = status };
        box.StepsJson = JsonSerializer.Serialize(steps);
        box.UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>The cash the spent coupon paid for is now earned — nothing is billed twice.</summary>
    private async Task EarnAsync(GatePayload gate, DateTimeOffset now, CancellationToken ct)
    {
        if (gate.BookingContainerId is not { } boxId || gate.MovementCode is null) return;
        foreach (var paid in await db.Charges.Where(c => c.BookingContainerId == boxId && c.MovementCode == gate.MovementCode
                                                         && c.Source == ChargeSource.Window && c.Status == ChargeStatus.Paid).ToListAsync(ct))
        {
            paid.Status = ChargeStatus.Earned;
            paid.EarnedGateTransactionId = gate.GateTransactionId;
            paid.EarnedAt = gate.TransactionAt;
            paid.GateTransactionId = gate.GateTransactionId;
            paid.EirNo = gate.EirNo;
            paid.UpdatedAt = now;
        }
    }

    private sealed record GatePayload(
        Guid GateTransactionId, string? EirNo, Guid BranchId, string ContainerNo, string? Direction, string? MovementCode,
        string? FullEmpty, Guid? BookingId, Guid? BookingContainerId, string? EquipmentTypeCode, string? IsoCode,
        string? LineCode, DateTimeOffset TransactionAt);
}

/// <summary>
/// The automatic coupon (PLAN_BILLING §4.2 step 0): a billable movement that can
/// owe NO cash — nothing in the order type prices in cash, and no storage on the
/// way out — gets its coupon at once, channel CREDIT, so the truck never queues at
/// the window for nothing. The id is derived from the box and the movement, so a
/// re-send is the same coupon and TOS keeps one. Runs inside the caller's
/// transaction: the outbox row commits with the change that caused it.
/// </summary>
internal sealed class AutomaticCoupons(
    RevenueDbContext db, IMasterDataReferences master, CashQuoter quoter, BranchCalendar calendar, ILogger<AutomaticCoupons> log)
{
    public async Task IssueAsync(BookingPlan plan, List<BookingPlanContainer> boxes, CancellationToken ct)
    {
        if (boxes.Count == 0) return;
        var branch = await calendar.BranchAsync(plan.BranchId, ct);
        if (branch is null)
        {
            log.LogWarning("Booking {OrderNo}: branch {BranchId} has no MDM profile; no automatic coupon.", plan.OrderNo, plan.BranchId);
            return;
        }

        var orderType = (await master.OrderTypePlansAsync([plan.OrderTypeCode], ct)).GetValueOrDefault(plan.OrderTypeCode);
        var now = calendar.Now;

        foreach (var box in boxes)
        {
            if (CashQuoter.NextStep(box, orderType) is not ({ } step, { IsBillable: true } rules)) continue;

            var quote = await quoter.QuoteAsync(plan, box, rules, branch, null, now, ct);
            // Something is (or may become) payable in cash: that is the window's job.
            if (quote.Lines.Count > 0 || quote.StorageApplies) continue;

            await RevenueOutbox.EnqueueAsync(db, plan.TenantId, "BOOKING", plan.BookingId, RevenueOutbox.CouponIssued,
                new CouponIssuedPayload(
                    CouponId: AutomaticCouponId(box.BookingContainerId, step.MovementCode),
                    BranchId: plan.BranchId, BookingId: plan.BookingId, ContainerNo: box.ContainerNo,
                    MovementCode: step.MovementCode, CouponRef: $"AUTO-{plan.OrderNo}-{step.SequenceNo}-{ShortId(box.BookingContainerId)}",
                    Channel: "CREDIT", Amount: null, CurrencyCode: null,
                    ValidFrom: now, ValidUntil: plan.ValidTo is { } to && to > now ? to : now.AddDays(30),
                    IssuedBy: null),
                ct);
        }
    }

    /// <summary>The same box and movement always yield the same coupon id — TOS dedupes on it.</summary>
    public static Guid AutomaticCouponId(Guid bookingContainerId, string movementCode) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"auto-coupon|{bookingContainerId:N}|{movementCode}")));

    private static string ShortId(Guid id) => id.ToString("N")[..6].ToUpperInvariant();
}
