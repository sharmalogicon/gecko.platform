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
/// TOS gate events → Revenue (PLAN_BILLING §4.3):
///   * the stay projection — a gate-in opens one, a gate-out closes it, a void
///     undoes whichever it was; storage is priced from it;
///   * the cash charges the spent coupon paid for become EARNED (on the truck
///     visit), and go back to PAID if the EIR is voided (the coupon comes back
///     unspent in TOS too);
///   * 6.3, clock 2 — the box's CREDIT lines (native, and cash lines the visit's
///     haulier holds on credit) are priced AT GATE TIME with the payload's truck
///     category and visit haulier, through the same CashQuoter as the window, and
///     written UNBILLED (source GATE). The PER_TRIP gate charge is raised once
///     per WHOLE truck visit (owner 2026-10-01): not when the visit already has a
///     live one, cash or credit. A void CANCELS them (or flags an invoiced one for
///     a credit note) and re-raises the visit's gate charge on a surviving box.
/// Idempotent on the message id (billing.inbox), with uq_charge__gate and
/// uq_charge__gate_trip as the database's backstop.
/// </summary>
internal sealed class GateEventHandler(
    RevenueDbContext db, TimeProvider clock, AutomaticCoupons coupons, CashQuoter quoter, BranchCalendar calendar,
    IMasterDataReferences master, ILogger<GateEventHandler> log) : IOutboxHandler
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
            await AccrueAsync(gate, message, now, tripOnly: false, ct);
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
            await AccrueAsync(gate, message, now, tripOnly: false, ct);
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
                earned.TruckVisitId = null;
                earned.UpdatedAt = now;
            }

            // 6.3: what the gate event accrued is taken back (§4.3; Q10 for an invoiced one).
            var reason = $"EIR {gate.EirNo ?? gate.GateTransactionId.ToString()} voided: {gate.VoidReason ?? "no reason given"}";
            if (reason.Length > 300) reason = reason[..300];
            foreach (var accrued in await db.Charges.Where(c => c.GateTransactionId == gate.GateTransactionId && c.Source == ChargeSource.Gate
                                                                && c.Status != ChargeStatus.Cancelled).ToListAsync(ct))
            {
                if (accrued.Status == ChargeStatus.Invoiced)
                    accrued.CreditNoteRequired = true;
                else
                {
                    accrued.Status = ChargeStatus.Cancelled;
                    accrued.CancelledAt = now;
                    accrued.CancelReason = reason;
                }
                accrued.UpdatedAt = now;
            }
        }

        if (note is not null) log.LogInformation("Gate event {MessageId} ({Box}): {Note}", message.MessageId, gate.ContainerNo, note);
        Inbox.Record(db, message, note is null ? "APPLIED" : "IGNORED", note);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        // The voided box carried the visit's gate charge on credit: a box still on
        // the truck carries it now (once per whole visit, §7.1).
        if (message.MessageType == Voided && await ReRaiseTripChargeAsync(gate, message, now, ct))
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
        // The gate skips an optional step the truck did not do (TOS marks it SKIPPED, a
        // required one would have blocked): mirror it, or the window keeps quoting it as
        // "next" and its coupon is issued for a movement that will never happen.
        if (status == "DONE")
            for (var i = 0; i < steps.Count; i++)
                if (steps[i].Status == "PENDING" && steps[i].SequenceNo < step.SequenceNo)
                    steps[i] = steps[i] with { Status = "SKIPPED" };
        box.StepsJson = JsonSerializer.Serialize(steps);
        box.UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>
    /// 6.3 — the box's credit lines for this movement, priced at gate time with the
    /// truck's category and the VISIT's haulier (Vector overrides on the truck's
    /// haulier, GateIn.cs:1320), written UNBILLED. <paramref name="tripOnly"/>: the
    /// re-raise after a void, which writes the PER_TRIP gate charge only.
    /// </summary>
    /// <returns>Whether a PER_TRIP gate charge was written.</returns>
    private async Task<bool> AccrueAsync(GatePayload gate, OutboxMessage message, DateTimeOffset now, bool tripOnly, CancellationToken ct)
    {
        if (gate.BookingContainerId is not { } boxId || gate.MovementCode is null) return false;
        var box = await db.BookingPlanContainers.AsNoTracking().SingleOrDefaultAsync(b => b.BookingContainerId == boxId, ct);
        var plan = box is null ? null : await db.BookingPlans.AsNoTracking().SingleOrDefaultAsync(p => p.BookingId == box.BookingId, ct);
        if (box is null || plan is null)
        {
            log.LogWarning("Gate event {Gate}: box {Box} is not in Revenue's booking copy; no credit accrued.", gate.GateTransactionId, boxId);
            return false;
        }

        var orderType = (await master.OrderTypePlansAsync([plan.OrderTypeCode], ct)).GetValueOrDefault(plan.OrderTypeCode);
        var rules = orderType?.Steps.FirstOrDefault(s => string.Equals(s.MovementCode, gate.MovementCode, StringComparison.OrdinalIgnoreCase));
        if (rules is not { IsBillable: true }) return false;
        var branch = await calendar.BranchAsync(plan.BranchId, ct);
        if (branch is null) return false;

        var carrier = gate.TruckVisitId is { } visitId ? await TripCarrierAsync(visitId, ct) : null;
        var quote = await quoter.QuoteAsync(plan, box, rules, branch, null, gate.TransactionAt, ct,
            new GateTerms(gate.TruckCategoryCode, gate.VisitHaulierPartyCode, null, carrier));

        var raisedTrip = false;
        foreach (var line in quote.BilledLater.Where(l => !tripOnly || l.IsPerTrip))
        {
            // A gate charge must be keyed on its truck; a pre-S2 event has none.
            if (line.IsPerTrip && gate.TruckVisitId is null)
            {
                log.LogWarning("Gate event {Gate}: {Charge} is once per truck but the event names no truck visit; not accrued.", gate.GateTransactionId, line.ChargeCode);
                continue;
            }
            if (await db.Charges.AnyAsync(c => c.Source == ChargeSource.Gate && c.GateTransactionId == gate.GateTransactionId
                                               && c.ChargeCode == line.ChargeCode && c.BillTo == line.BillTo && c.PaymentTermCode == line.PaymentTermCode, ct))
                continue;   // uq_charge__gate: this gate move already raised it

            var charge = CashQuoter.ChargeFrom(plan, box, gate.MovementCode, null, line, ChargeSource.Gate, ChargeStatus.Unbilled, now);
            charge.GateTransactionId = gate.GateTransactionId;
            charge.EirNo = gate.EirNo;
            charge.TruckVisitId = gate.TruckVisitId;
            charge.SourceMessageId = message.MessageId;
            db.Charges.Add(charge);
            raisedTrip |= line.IsPerTrip;
        }

        if (!tripOnly) await RecordCreditPricingAsync(gate, plan, box, quote, message, now, ct);
        return raisedTrip;
    }

    /// <summary>
    /// The box already carrying this visit's gate charge, if any: a live credit one
    /// (GATE) or the cash one this visit's gate event earned. Tracked changes count
    /// (the cash earned by this very message is not saved yet).
    /// </summary>
    private async Task<string?> TripCarrierAsync(Guid visitId, CancellationToken ct)
    {
        static bool Live(Charge c) => c.IsTripCharge && c.Status != ChargeStatus.Cancelled
                                      && (c.Source == ChargeSource.Gate || c.Status == ChargeStatus.Earned);
        var local = db.Charges.Local.FirstOrDefault(c => c.TruckVisitId == visitId && Live(c));
        if (local is not null) return local.ContainerNo ?? "another box";

        var stored = await db.Charges.Where(c => c.TruckVisitId == visitId && c.IsTripCharge && c.Status != ChargeStatus.Cancelled
                                                 && (c.Source == ChargeSource.Gate || c.Status == ChargeStatus.Earned))
            .ToListAsync(ct);   // tracked: a change made in this handler wins over the stored row
        return stored.FirstOrDefault(Live) is { } carrier ? carrier.ContainerNo ?? "another box" : null;
    }

    /// <summary>
    /// After a void: if the voided box carried the visit's gate charge on credit and
    /// nothing live carries it now, the first box still on the truck (TOS lists them)
    /// gets it — priced with the same truck facts.
    /// </summary>
    private async Task<bool> ReRaiseTripChargeAsync(GatePayload gate, OutboxMessage message, DateTimeOffset now, CancellationToken ct)
    {
        if (gate.TruckVisitId is not { } visitId || gate.VisitSurvivors is not { Count: > 0 } survivors) return false;
        var lostTrip = await db.Charges.AnyAsync(c => c.GateTransactionId == gate.GateTransactionId && c.Source == ChargeSource.Gate
                                                      && c.IsTripCharge && c.Status == ChargeStatus.Cancelled, ct);
        if (!lostTrip || await TripCarrierAsync(visitId, ct) is not null) return false;

        foreach (var s in survivors)
        {
            var standIn = gate with
            {
                GateTransactionId = s.GateTransactionId, EirNo = s.EirNo, ContainerNo = s.ContainerNo, Direction = s.Direction,
                MovementCode = s.MovementCode, BookingId = s.BookingId, BookingContainerId = s.BookingContainerId,
                TransactionAt = s.TransactionAt,
            };
            if (await AccrueAsync(standIn, message, now, tripOnly: true, ct))
            {
                log.LogInformation("Visit {Visit}: gate charge re-raised on {Box} after EIR {Eir} was voided.", gate.VisitNo, s.ContainerNo, gate.EirNo);
                return true;
            }
        }
        return false;
    }

    /// <summary>The CREDIT pricing record (§4.1): one per gate transaction × movement, NO_CHARGE when nothing priced (Q11).</summary>
    private async Task RecordCreditPricingAsync(GatePayload gate, BookingPlan plan, BookingPlanContainer box, MovementQuote quote,
        OutboxMessage message, DateTimeOffset now, CancellationToken ct)
    {
        var credit = quote.Tried.Where(t => t.PaymentTermCode != "CASH").ToList();
        var record = await db.MovementPricings.SingleOrDefaultAsync(m =>
            m.GateTransactionId == gate.GateTransactionId && m.MovementCode == gate.MovementCode && m.Clock == "CREDIT", ct);
        if (record is null)
        {
            record = new MovementPricing { TenantId = plan.TenantId, BranchId = plan.BranchId, Clock = "CREDIT", MovementCode = gate.MovementCode!, CreatedAt = now };
            db.MovementPricings.Add(record);
        }
        record.GateTransactionId = gate.GateTransactionId;
        record.BookingId = plan.BookingId;
        record.BookingContainerId = box.BookingContainerId;
        record.ContainerNo = box.ContainerNo;
        record.OrderTypeCode = plan.OrderTypeCode;
        record.VariantsTried = credit.Count;
        record.VariantsPriced = quote.BilledLater.Count;
        record.TotalAmount = quote.BilledLater.Sum(l => l.Amount);
        record.CurrencyCode = quote.BilledLater.Select(l => l.CurrencyCode).FirstOrDefault();
        record.TrailJson = JsonSerializer.Serialize(credit);
        record.SourceMessageId = message.MessageId;
        record.PricedAt = now;
        record.UpdatedAt = now;
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
            paid.TruckVisitId = gate.TruckVisitId;
            paid.UpdatedAt = now;
        }
    }

    /// <param name="TruckVisitId">S2 (GATE_CHARGING_DESIGN §2): the truck facts; null on an event queued before them.</param>
    /// <param name="VisitSurvivors">On a void only: the visit's other boxes still standing.</param>
    private sealed record GatePayload(
        Guid GateTransactionId, string? EirNo, Guid BranchId, string ContainerNo, string? Direction, string? MovementCode,
        string? FullEmpty, Guid? BookingId, Guid? BookingContainerId, string? EquipmentTypeCode, string? IsoCode,
        string? LineCode, DateTimeOffset TransactionAt,
        Guid? TruckVisitId = null, string? VisitNo = null, int? VisitBoxIndex = null, string? TruckCategoryCode = null,
        string? TripTypeCode = null, string? VisitHaulierPartyCode = null, string? VoidReason = null,
        List<VisitSurvivor>? VisitSurvivors = null);

    private sealed record VisitSurvivor(
        Guid GateTransactionId, string? EirNo, string ContainerNo, string? Direction, string? MovementCode,
        Guid? BookingId, Guid? BookingContainerId, DateTimeOffset TransactionAt);
}

/// <summary>What a quote refresh changed on a booking's QUOTED lines; <c>NoRate</c> = lines no tariff prices.</summary>
internal sealed record QuoteRefresh(int Repriced, int Added, int Removed, int NoRate);

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
        await RefreshQuotesAsync(plan, branch, orderType, now, ct);

        foreach (var box in boxes)
        {
            // A booked box not nominated yet (gecko_tos 22) has nothing the barrier could match a
            // coupon to; it gets one when its number arrives (the change is a BookingChanged too).
            if (string.IsNullOrEmpty(box.ContainerNo)) continue;
            if (CashQuoter.NextStep(box, orderType) is not ({ } step, { IsBillable: true } rules)) continue;

            var quote = await quoter.QuoteAsync(plan, box, rules, branch, null, now, ct);
            // Something is (or may become) payable in cash: that is the window's job.
            if (quote.Lines.Count > 0 || quote.StorageApplies || quote.ReeferApplies) continue;
            // A cash charge with no rate anywhere is not charged (owner 2026-10-07, as Vector): with
            // nothing priced in cash the box owes nothing at the window and gets its coupon.
            if (quote.NoPrice.Count > 0)
                log.LogInformation("Booking {OrderNo} box {Box} {Movement}: no tariff prices {Charges}; not charged.",
                    plan.OrderNo, box.ContainerNo, step.MovementCode, string.Join(", ", quote.NoPrice.Select(l => l.ChargeCode)));

            await RevenueOutbox.EnqueueAsync(db, plan.TenantId, "BOOKING", plan.BookingId, RevenueOutbox.CouponIssued,
                new CouponIssuedPayload(
                    CouponId: AutomaticCouponId(box.BookingContainerId, step.MovementCode),
                    BranchId: plan.BranchId, BookingId: plan.BookingId, ContainerNo: box.ContainerNo,
                    MovementCode: step.MovementCode, CouponRef: $"AUTO-{plan.OrderNo}-{step.SequenceNo}-{ShortId(box.BookingContainerId)}",
                    Channel: "CREDIT", Amount: null, CurrencyCode: null,
                    ValidFrom: now, ValidUntil: plan.ValidTo is { } to && to > now ? to : now.AddDays(30),
                    IssuedBy: null,
                    TruckCategoryCode: quote.Terms?.TruckCategoryCode, HaulierCode: quote.Terms?.HaulierCode),
                ct);
        }
    }

    /// <summary>
    /// The booking statement's expected charges (gecko_revenue 23, owner 2026-10-04): every current
    /// box x every PENDING billable movement x its order-type charges, cash and credit, priced like
    /// the window prices them (quotation, else the standard tariff), written as billing.charge rows
    /// source QUOTE / status QUOTED. A cash charge no tariff prices is quoted at 0 with no schedule.
    /// What is no longer expected — paid, billed at the gate, movement done, box gone — is CANCELLED
    /// with its reason (DELETE is denied). Runs with the caller's SaveChanges / transaction.
    /// </summary>
    public async Task<QuoteRefresh> RefreshQuotesAsync(BookingPlan plan, BranchClockInfo branch, OrderTypePlanRef? orderType, DateTimeOffset now, CancellationToken ct,
        string? cancelReason = null)
    {
        if (orderType is null) return new QuoteRefresh(0, 0, 0, 0);
        var boxes = await db.BookingPlanContainers.Where(b => b.BookingId == plan.BookingId && b.IsCurrent && b.EndReason == null).ToListAsync(ct);
        var existing = await db.Charges.Where(c => c.BookingId == plan.BookingId && c.Source == ChargeSource.Quote && c.Status == ChargeStatus.Quoted).ToListAsync(ct);

        var wanted = new Dictionary<(Guid, string, string, string, string), Charge>();
        foreach (var box in boxes)
            foreach (var step in CashQuoter.Steps(box).Where(st => st.Status == "PENDING"))
            {
                var rules = orderType.Steps.FirstOrDefault(r => string.Equals(r.MovementCode, step.MovementCode, StringComparison.OrdinalIgnoreCase));
                if (rules is not { IsBillable: true }) continue;
                var quote = await quoter.QuoteAsync(plan, box, rules, branch, null, now, ct);
                var lines = quote.Lines.Where(l => l.Kind is QuoteLine.Movement or QuoteLine.Vas)
                    .Concat(quote.BilledLater).Concat(quote.NoPrice).Concat(quote.ZeroRated);
                foreach (var line in lines)
                {
                    var key = (box.BookingContainerId, step.MovementCode, line.ChargeCode, line.BillTo, line.PaymentTermCode);
                    wanted.TryAdd(key, CashQuoter.ChargeFrom(plan, box, step.MovementCode, null, line, ChargeSource.Quote, ChargeStatus.Quoted, now));
                }
            }

        int repriced = 0, removed = 0;
        foreach (var row in existing)
        {
            var key = (row.BookingContainerId!.Value, row.MovementCode!, row.ChargeCode!, row.BillTo!, row.PaymentTermCode!);
            if (wanted.Remove(key, out var fresh))
            {
                if (row.UnitRate != fresh.UnitRate || row.Amount != fresh.Amount || row.Quantity != fresh.Quantity) repriced++;
                // Same line, new price: keep the row, take the new figures and snapshot.
                row.Quantity = fresh.Quantity; row.UnitRate = fresh.UnitRate; row.Amount = fresh.Amount;
                row.TaxCode = fresh.TaxCode; row.TaxRate = fresh.TaxRate; row.TaxAmount = fresh.TaxAmount;
                row.PricedForDate = fresh.PricedForDate; row.ScheduleId = fresh.ScheduleId; row.ScheduleNo = fresh.ScheduleNo;
                row.ScheduleVersionNo = fresh.ScheduleVersionNo; row.ScheduleType = fresh.ScheduleType; row.ScopeRank = fresh.ScopeRank;
                row.TosRateId = fresh.TosRateId; row.RateRowVersion = fresh.RateRowVersion; row.Specificity = fresh.Specificity;
                row.PricingMethod = fresh.PricingMethod; row.BillingUnitCode = fresh.BillingUnitCode; row.PricesIncludeTax = fresh.PricesIncludeTax;
                row.BaseRate = fresh.BaseRate; row.FreeUnits = fresh.FreeUnits; row.ChargeableQuantity = fresh.ChargeableQuantity;
                row.ResolvedAt = fresh.ResolvedAt; row.PriceSnapshotJson = fresh.PriceSnapshotJson; row.PayerPartyCode = fresh.PayerPartyCode;
                row.ContainerNo = fresh.ContainerNo; row.UpdatedAt = now;
                // A supervisor's rate (gecko_revenue 25) came back through the quoter: the row keeps it.
                row.IsRateOverridden = fresh.IsRateOverridden; row.UnitRateOriginal = fresh.UnitRateOriginal;
                row.OverrideReason = fresh.OverrideReason; row.OverriddenBy = fresh.OverriddenBy; row.OverriddenAt = fresh.OverriddenAt;
                row.DiscountType = fresh.DiscountType; row.DiscountRate = fresh.DiscountRate;
            }
            else
            {
                row.Status = ChargeStatus.Cancelled;
                row.CancelledAt = now;
                row.CancelReason = cancelReason ?? "Quote no longer due: paid, billed at the gate, movement done or box removed.";
                row.UpdatedAt = now;
                removed++;
            }
        }
        db.Charges.AddRange(wanted.Values);
        await db.SaveChangesAsync(ct);
        // what the tariff still cannot price: those boxes are held at the gate
        var noRate = existing.Concat(wanted.Values).Count(c => c.Status == ChargeStatus.Quoted && c.ScheduleId is null && !c.IsRateOverridden && !c.IsLocked);
        return new QuoteRefresh(repriced, wanted.Count, removed, noRate);
    }

    /// <summary>The same box and movement always yield the same coupon id — TOS dedupes on it.</summary>
    public static Guid AutomaticCouponId(Guid bookingContainerId, string movementCode) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"auto-coupon|{bookingContainerId:N}|{movementCode}")));

    private static string ShortId(Guid id) => id.ToString("N")[..6].ToUpperInvariant();
}
