using System.Text.Json;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Contracts;
using Gecko.Revenue.Domain;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Application;

/// <summary>One step of a box's plan, as TOS sent it in BookingChanged.</summary>
internal sealed record PlanStep(short SequenceNo, string MovementCode, string Status);

/// <summary>One priced cash line: what the customer pays for at the window.</summary>
internal sealed record QuoteLine(
    string Kind, Guid ChargeCodeId, string ChargeCode, string ChargeName, string BillTo, string PaymentTermCode,
    string? PayerPartyCode, decimal Quantity, decimal? UnitRate, decimal Amount, string CurrencyCode,
    string? TaxCode, decimal TaxRate, decimal TaxAmount, DateOnly? ServiceFrom, DateOnly? ServiceTo, PriceResult Price)
{
    public const string Movement = "MOVEMENT";
    public const string Storage = "STORAGE";
    public const string Reefer = "REEFER";

    public decimal Total => Amount + TaxAmount;
}

/// <summary>A variant the quote tried: priced or not, and why (the pricing record, PLAN_BILLING §4.1).</summary>
internal sealed record TriedVariant(string ChargeCode, string BillTo, string PaymentTermCode, string Outcome, decimal? Amount, IReadOnlyList<string> Trail);

internal sealed record MovementQuote(
    Guid BookingContainerId, string? ContainerNo, string MovementCode, string Direction,
    DateOnly? PaidUntil, ContainerStay? Stay, int? StayDays, bool StorageApplies,
    IReadOnlyList<QuoteLine> Lines, IReadOnlyList<TriedVariant> Tried,
    bool ReeferApplies = false, ReeferPowerQuote? Reefer = null)
{
    public decimal Subtotal => Lines.Sum(l => l.Amount);
    public decimal Tax => Lines.Sum(l => l.TaxAmount);
    public decimal Total => Subtotal + Tax;
    public string? CurrencyCode => Lines.Select(l => l.CurrencyCode).FirstOrDefault();
}

/// <summary>
/// Clock 1 (PLAN_BILLING §4.2): what a box's next movement costs in CASH, right now.
///
/// §4.1 — the order type lists what CAN be charged (MDM order_type_charge); each
/// cash variant is priced through <see cref="ITariffPricing"/>, and the tariff
/// decides: a variant nobody has a rate for is normal (47% in Vector) and goes in
/// the trail, never onto the receipt. VAS (order-level) charges are not quoted
/// here — nobody raises them automatically.
///
/// §4.4 — a gate-OUT also carries storage for the stay, up to the day the
/// customer chooses (Q5). Days are INCLUSIVE branch-local calendar days, free days
/// come off in the resolver: exactly the rule the August replay matched Vector with.
///
/// What is already PAID, EARNED or WAIVED for the box and movement is not quoted
/// again; storage already paid is subtracted, so coming back after the paid-until
/// date quotes only the extra days.
/// </summary>
internal sealed class CashQuoter(RevenueDbContext db, IMasterDataReferences master, ITariffPricing pricing, ReeferPowerQuoter reefer)
{
    private const string Cash = "CASH";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] Settled = [ChargeStatus.Paid, ChargeStatus.Earned, ChargeStatus.Waived];

    public static IReadOnlyList<PlanStep> Steps(BookingPlanContainer box) =>
        JsonSerializer.Deserialize<List<PlanStep>>(box.StepsJson, Json) ?? [];

    /// <summary>The box's next pending step and its MDM rules; null when nothing is pending.</summary>
    public static (PlanStep Step, OrderTypeStepRef? Rules)? NextStep(BookingPlanContainer box, OrderTypePlanRef? plan)
    {
        var step = Steps(box).Where(s => s.Status == "PENDING").OrderBy(s => s.SequenceNo).FirstOrDefault();
        if (step is null) return null;
        var rules = plan?.Steps.FirstOrDefault(s => string.Equals(s.MovementCode, step.MovementCode, StringComparison.OrdinalIgnoreCase));
        return (step, rules);
    }

    public async Task<MovementQuote> QuoteAsync(BookingPlan plan, BookingPlanContainer box, OrderTypeStepRef step,
        BranchClockInfo branch, DateOnly? paidUntil, DateTimeOffset now, CancellationToken ct)
    {
        var lines = new List<QuoteLine>();
        var tried = new List<TriedVariant>();

        var settled = await db.Charges.AsNoTracking()
            .Where(c => c.BookingContainerId == box.BookingContainerId && c.MovementCode == step.MovementCode
                        && c.Source == ChargeSource.Window && Settled.Contains(c.Status))
            .Select(c => new { c.ChargeCode, c.BillTo, c.PaymentTermCode, c.Amount, c.ServiceTo, c.Quantity })
            .ToListAsync(ct);

        var equipment = box.EquipmentTypeCode is { Length: > 0 } type
            ? (await master.EquipmentTypesAsync([type], ct)).GetValueOrDefault(type)
            : null;
        var size = equipment?.SizeCode;

        // ── the movement's own charges ──────────────────────────────────────
        var menu = (await master.OrderTypeChargesAsync(plan.OrderTypeCode, ct))
            .Where(c => !c.IsValueAddedService
                        && string.Equals(c.MovementCode, step.MovementCode, StringComparison.OrdinalIgnoreCase)
                        && (c.PaymentTermCode is null || c.PaymentTermCode == Cash))
            .DistinctBy(c => (c.ChargeCode, c.BillTo))
            .ToList();
        var variants = (await master.ChargeVariantsAsync(menu.Select(c => c.ChargeCode), ct))
            .Where(v => v.PaymentTermCode == Cash)
            .ToDictionary(v => (v.ChargeCode, v.BillTo));

        foreach (var item in menu)
        {
            if (settled.Any(s => s.ChargeCode == item.ChargeCode && s.BillTo == item.BillTo && s.PaymentTermCode == Cash))
            {
                tried.Add(new TriedVariant(item.ChargeCode, item.BillTo, Cash, "SETTLED", null, []));
                continue;
            }
            if (!variants.TryGetValue((item.ChargeCode, item.BillTo), out var variant))
            {
                tried.Add(new TriedVariant(item.ChargeCode, item.BillTo, Cash, "NO_VARIANT", null, ["MDM has no CASH variant for this payer."]));
                continue;
            }

            var result = await pricing.PriceAsync(Request(plan, box, step, size, item.ChargeCode, item.BillTo, now,
                item.DefaultQty ?? 1, freeTimeKind: null, fullEmpty: step.FullEmpty), ct);
            var amount = result.Outcome == PriceOutcomes.Priced ? result.Amount ?? 0 : (decimal?)null;
            if (amount is not > 0)
            {
                tried.Add(new TriedVariant(item.ChargeCode, item.BillTo, Cash,
                    amount is null ? PriceOutcomes.Unpriced : "PRICED_ZERO", amount, result.PrecedenceTrail));
                continue;
            }

            tried.Add(new TriedVariant(item.ChargeCode, item.BillTo, Cash, PriceOutcomes.Priced, amount, result.PrecedenceTrail));
            lines.Add(Line(QuoteLine.Movement, variant, PayerFor(plan, item.BillTo), result.Quantity, result.UnitRate,
                amount.Value, result, null, null));
        }

        // ── storage, on the way out (§4.4) ──────────────────────────────────
        ContainerStay? stay = null;
        int? stayDays = null;
        var storageApplies = false;
        DateOnly? until = null;

        if (step.Direction == "OUT" && box.ContainerNo is { Length: > 0 } containerNo)
        {
            stay = await db.ContainerStays.AsNoTracking()
                .SingleOrDefaultAsync(s => s.ContainerNo == containerNo && s.Status == "OPEN", ct);

            if (stay is not null)
            {
                var category = stay.FullEmptyIn == "FULL" ? "LADEN" : "EMPTY";
                var storage = (await master.ChargeVariantsOfTypeAsync("STORAGE", ct))
                    .Where(v => v.PaymentTermCode == Cash && string.Equals(v.ChargeCategory, category, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                storageApplies = storage.Count > 0;

                until = paidUntil ?? branch.LocalDate(now);
                var inDate = branch.LocalDate(stay.InAt);
                stayDays = Math.Max(1, until.Value.DayNumber - inDate.DayNumber + 1);

                foreach (var variant in storage)
                {
                    var paid = settled.Where(s => s.ChargeCode == variant.ChargeCode && s.BillTo == variant.BillTo).ToList();
                    var paidTo = paid.Max(p => p.ServiceTo);
                    if (paidTo >= until)
                    {
                        tried.Add(new TriedVariant(variant.ChargeCode, variant.BillTo, Cash, "SETTLED", null, [$"Paid until {paidTo:yyyy-MM-dd}."]));
                        continue;
                    }

                    var result = await pricing.PriceAsync(Request(plan, box, step, size, variant.ChargeCode, variant.BillTo, now,
                        stayDays.Value, FreeTimeKinds.Storage, stay.FullEmptyIn), ct);
                    // Tiered storage is priced for the WHOLE stay, then what was paid
                    // before is taken off: a top-up must land on the right tier.
                    var due = result.Outcome == PriceOutcomes.Priced ? (result.Amount ?? 0) - paid.Sum(p => p.Amount) : (decimal?)null;
                    if (due is not > 0)
                    {
                        tried.Add(new TriedVariant(variant.ChargeCode, variant.BillTo, Cash,
                            due is null ? PriceOutcomes.Unpriced : "PRICED_ZERO", due, result.PrecedenceTrail));
                        continue;
                    }

                    var from = paidTo is { } p ? p.AddDays(1) : inDate;
                    // The receipt prints the days CHARGED: free days come off the first
                    // payment (32 in the yard, 7 free = 25); a top-up is all extra days.
                    var charged = paidTo is null ? result.ChargeableQuantity ?? stayDays.Value : until.Value.DayNumber - from.DayNumber + 1;
                    tried.Add(new TriedVariant(variant.ChargeCode, variant.BillTo, Cash, PriceOutcomes.Priced, due, result.PrecedenceTrail));
                    lines.Add(Line(QuoteLine.Storage, variant, PayerFor(plan, variant.BillTo),
                        charged, result.UnitRate, due.Value, result, from, until));
                }
            }
        }

        // ── reefer power, on the way out: per started hour plugged in ────────
        ReeferPowerQuote? power = null;
        var reeferApplies = false;
        if (stay is not null)
        {
            var sessions = await db.ReeferSessions.AsNoTracking()
                .Where(r => r.InGateTransactionId == stay.InGateTransactionId)
                .ToListAsync(ct);
            if (sessions.Count > 0 || box.IsReefer || equipment?.IsReefer == true)
            {
                var typeCode = sessions.Select(r => r.EquipmentTypeCode).FirstOrDefault(t => t is not null)
                               ?? stay.EquipmentTypeCode ?? box.EquipmentTypeCode;
                power = await reefer.QuoteAsync(sessions,
                    new ReeferPricingContext(plan.BranchId, stay.ContainerNo, typeCode, plan, box, step.MovementCode, step.Direction, step.FullEmpty),
                    now, ct);
                // A reefer box whose power the tariff prices must come to the window,
                // even at 0 hours so far — no automatic coupon for it. A rate that is not
                // set charges nothing and holds nothing.
                reeferApplies = power.RateAvailable;
                AddReefer(power, plan, settled.Select(s => (s.ChargeCode, s.BillTo)).ToList(), lines, tried);
            }
        }

        return new MovementQuote(box.BookingContainerId, box.ContainerNo, step.MovementCode, step.Direction,
            storageApplies ? until : null, stay, stayDays, storageApplies, lines, tried, reeferApplies, power);
    }

    /// <summary>
    /// The priced reefer variants become lines (quantity = billable hours); everything
    /// else goes in the trail with its reason, as an unpriced storage variant does —
    /// never on the receipt. Reefer power is paid once per box × movement: a variant
    /// already settled is not quoted again (the coupon it paid for is already out).
    /// </summary>
    private static void AddReefer(ReeferPowerQuote power, BookingPlan plan, List<(string ChargeCode, string BillTo)> settled,
        List<QuoteLine> lines, List<TriedVariant> tried)
    {
        if (power.Outcome == ReeferOutcomes.NoSessions) return;
        if (power.Outcome == ReeferOutcomes.ChargeCodeNotSet)
        {
            tried.Add(new TriedVariant(ReeferPowerQuoter.ChargeType, "-", Cash, power.Outcome, null, [power.Message]));
            return;
        }

        foreach (var v in power.Variants)
        {
            var trail = v.Trail.Append($"{power.BillableHours} started hour(s), {power.MinutesPlugged} min plugged in over {power.Sessions} session(s).").ToList();
            if (settled.Contains((v.Variant.ChargeCode, v.Variant.BillTo)))
            {
                tried.Add(new TriedVariant(v.Variant.ChargeCode, v.Variant.BillTo, Cash, "SETTLED", null, trail));
                continue;
            }
            tried.Add(new TriedVariant(v.Variant.ChargeCode, v.Variant.BillTo, Cash, v.Outcome, v.Amount, trail));
            if (v.Outcome != ReeferOutcomes.Priced) continue;

            lines.Add(Line(QuoteLine.Reefer, v.Variant, PayerFor(plan, v.Variant.BillTo), power.BillableHours,
                v.Price!.UnitRate, v.Amount!.Value, v.Price, null, null));
        }
    }

    private static PriceRequest Request(BookingPlan plan, BookingPlanContainer box, OrderTypeStepRef step, string? size,
        string chargeCode, string billTo, DateTimeOffset now, decimal quantity, string? freeTimeKind, string? fullEmpty) =>
        new(ModuleCode: "TOS", BranchId: plan.BranchId, EventTime: now, ChargeCode: chargeCode, BillTo: billTo,
            PaymentTermCode: Cash, AgentPartyCode: plan.AgentPartyCode, ForwarderPartyCode: plan.ForwarderPartyCode,
            CustomerPartyCode: plan.CustomerPartyCode, BookingRef: plan.OrderNo, OrderTypeCode: plan.OrderTypeCode,
            MovementCode: step.MovementCode, EquipmentTypeCode: box.EquipmentTypeCode, EquipmentSize: size,
            CargoCategoryCode: plan.CargoCategoryCode, IsDangerousGoods: box.IsDangerousGoods,
            GrossWeightKg: box.DeclaredGrossWeightKg, Quantity: quantity, FreeTimeKind: freeTimeKind,
            FullEmpty: fullEmpty, Direction: step.Direction);

    /// <summary>
    /// Money is rounded to satang HERE and nowhere later (14_billing). A tariff
    /// written VAT-inclusive is split; an exclusive one gets VAT on top — KORAKIT's
    /// ฿654.21 + 7% = ฿45.79 = ฿700.00.
    /// </summary>
    private static QuoteLine Line(string kind, ChargeVariantRef variant, string? payer, decimal quantity, decimal? unitRate,
        decimal amount, PriceResult price, DateOnly? from, DateOnly? to)
    {
        var rate = variant.TaxRatePct;
        decimal net, tax;
        if (price.PricesIncludeTax == true)
        {
            var gross = Money(amount);
            net = Money(gross / (1 + rate / 100m));
            tax = gross - net;
        }
        else
        {
            net = Money(amount);
            tax = Money(net * rate / 100m);
        }

        return new QuoteLine(kind, variant.ChargeCodeId, variant.ChargeCode, variant.DescriptionEn, variant.BillTo, Cash, payer,
            quantity, unitRate, net, price.CurrencyCode ?? "THB", variant.TaxCode, rate, tax, from, to, price);
    }

    public static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Who the booking says pays, for each bill-to role (lookup.bill_to_role).</summary>
    public static string? PayerFor(BookingPlan plan, string billTo) => billTo switch
    {
        "AGENT" => plan.AgentPartyCode,
        "FORWARDER" => plan.ForwarderPartyCode,
        "HAULIER" => plan.HaulierPartyCode,
        "LINE" => plan.LineCode,
        _ => plan.CustomerPartyCode,   // CUSTOMER, SHIPPER, CONSIGNEE
    };
}

internal static class ChargeStatus
{
    public const string Paid = "PAID";
    public const string Earned = "EARNED";
    public const string Unbilled = "UNBILLED";
    public const string Waived = "WAIVED";
    public const string Cancelled = "CANCELLED";
}

internal static class ChargeSource
{
    public const string Window = "WINDOW";
    public const string Gate = "GATE";
}
