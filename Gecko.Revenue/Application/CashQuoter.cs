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

/// <summary>
/// One priced line. In <see cref="MovementQuote.Lines"/> it is CASH, paid at the
/// window; in <see cref="MovementQuote.BilledLater"/> it is CREDIT (native, or a
/// cash line the haulier's own term turned into credit), shown at the window and
/// accrued UNBILLED by the gate event (PLAN_BILLING 6.3).
/// </summary>
/// <param name="BillingUnitCode">The MDM charge code's unit: PER_TRIP is the once-per-truck gate charge.</param>
/// <param name="ByHaulierTerm">A CASH line moved to credit by the haulier's charge term (gecko_master 22).</param>
internal sealed record QuoteLine(
    string Kind, Guid ChargeCodeId, string ChargeCode, string ChargeName, string BillTo, string PaymentTermCode,
    string? PayerPartyCode, decimal Quantity, decimal? UnitRate, decimal Amount, string CurrencyCode,
    string? TaxCode, decimal TaxRate, decimal TaxAmount, DateOnly? ServiceFrom, DateOnly? ServiceTo, PriceResult Price,
    string? BillingUnitCode = null, bool ByHaulierTerm = false)
{
    public const string Movement = "MOVEMENT";
    public const string Storage = "STORAGE";
    public const string Reefer = "REEFER";
    public const string Vas = "VAS";

    public decimal Total => Amount + TaxAmount;
    public bool IsPerTrip => BillingUnitCode == CashQuoter.PerTrip;
}

/// <summary>
/// What the truck brings to a quote (GATE_CHARGING_DESIGN §2-§3, §7): its
/// category (a tariff axis), its haulier (whose own terms may turn cash into
/// credit), the VAS the clerk ticked, and — for every box after the first on the
/// same truck — which box already carries the truck's PER_TRIP gate charge.
/// A null truck category / haulier means "the default": the tenant setting
/// gate.default_truck_category and the booking's haulier.
/// </summary>
internal sealed record GateTerms(
    string? TruckCategoryCode = null, string? HaulierCode = null,
    IReadOnlyCollection<string>? Vas = null, string? TripChargeCarriedBy = null)
{
    public static readonly GateTerms None = new();
}

/// <summary>
/// Withholding tax at the cash window, as Vector's gate screen has it (owner
/// 2026-10-01): the clerk MAY apply it when the cash total, VAT included, is over
/// 1,000 (GateIn.cs:2118); it is 3% of the amount before VAT (GateIn.cs:3214) and
/// comes off what is paid, not off the tax invoice's total.
/// </summary>
internal static class WithholdingTax
{
    public const decimal Threshold = 1000m;
    public const decimal Rate = 3m;

    public static bool MayApply(decimal totalWithVat) => totalWithVat > Threshold;

    public static decimal Amount(decimal subtotalBeforeVat) => CashQuoter.Money(subtotalBeforeVat * Rate / 100m);
}

/// <summary>A variant the quote tried: priced or not, and why (the pricing record, PLAN_BILLING §4.1).</summary>
internal sealed record TriedVariant(string ChargeCode, string BillTo, string PaymentTermCode, string Outcome, decimal? Amount, IReadOnlyList<string> Trail);

/// <summary>
/// One gate VAS this movement offers (Vector GateIn.cs:956 LoadGateInVASCharges), priced as it would be if ticked:
/// <see cref="Line"/> null when no tariff prices it (<see cref="Outcome"/> UNPRICED / NO_VARIANT).
/// </summary>
internal sealed record VasOption(string ChargeCode, string ChargeName, string BillTo, string PaymentTermCode, bool Ticked,
    string Outcome, QuoteLine? Line);

internal sealed record MovementQuote(
    Guid BookingContainerId, string? ContainerNo, string MovementCode, string Direction,
    DateOnly? PaidUntil, ContainerStay? Stay, int? StayDays, bool StorageApplies,
    IReadOnlyList<QuoteLine> Lines, IReadOnlyList<TriedVariant> Tried,
    bool ReeferApplies = false, ReeferPowerQuote? Reefer = null,
    IReadOnlyList<QuoteLine>? Later = null, GateTerms? Terms = null, IReadOnlyList<QuoteLine>? Unpriced = null,
    IReadOnlyList<VasOption>? VasMenu = null)
{
    /// <summary>
    /// Cash charges the order type raises on this movement that NO tariff prices (not a
    /// contract, not the public / standard tariff), as lines at 0. While any is here the
    /// box gets no automatic coupon and no receipt: a supervisor adds the rate or waives
    /// the line (owner 2026-10-03: missing price = warn, then block — never a free pass).
    /// </summary>
    public IReadOnlyList<QuoteLine> NoPrice => Unpriced ?? [];

    /// <summary>Credit lines, priced for display: nothing to pay at the window (§2 step 2).</summary>
    public IReadOnlyList<QuoteLine> BilledLater => Later ?? [];
    /// <summary>The box carries a PER_TRIP gate charge, cash or credit — the next box on the truck does not.</summary>
    public bool CarriesTripCharge => Lines.Concat(BilledLater).Any(l => l.IsPerTrip);

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
    private const string Credit = "CREDIT";
    /// <summary>lookup.billing_unit "Per truck trip": the gate charge, levied once per truck visit (owner 2026-10-01, §7.1).</summary>
    public const string PerTrip = "PER_TRIP";
    public const string TruckCategoryList = "TRUCK_CATEGORY";
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

    /// <summary>
    /// Fills a quote's defaults (§7.4): no truck category → the tenant's
    /// gate.default_truck_category (KORAKIT: 18_WHEEL); no haulier → the booking's.
    /// </summary>
    public async Task<GateTerms> ResolveAsync(GateTerms? gate, BookingPlan plan, CancellationToken ct)
    {
        gate ??= GateTerms.None;
        var truck = gate.TruckCategoryCode;
        if (string.IsNullOrWhiteSpace(truck))
            truck = await master.GetStringSettingAsync(RevenueSettingKeys.DefaultTruckCategory, plan.BranchId, ct);
        var haulier = string.IsNullOrWhiteSpace(gate.HaulierCode) ? plan.HaulierPartyCode : gate.HaulierCode;
        return gate with
        {
            TruckCategoryCode = string.IsNullOrWhiteSpace(truck) ? null : truck.Trim().ToUpperInvariant(),
            HaulierCode = string.IsNullOrWhiteSpace(haulier) ? null : haulier.Trim().ToUpperInvariant(),
        };
    }

    /// <summary>
    /// Vector offers gate VAS on an empty drop-off or a pick-up (GateIn.cs:961-990):
    /// the step's direction and load state decide, as the trip type would.
    /// </summary>
    public static bool OffersVas(OrderTypeStepRef step) =>
        step.Direction == "OUT" || (step.Direction == "IN" && step.FullEmpty == "EMPTY");

    public async Task<MovementQuote> QuoteAsync(BookingPlan plan, BookingPlanContainer box, OrderTypeStepRef step,
        BranchClockInfo branch, DateOnly? paidUntil, DateTimeOffset now, CancellationToken ct, GateTerms? gate = null)
    {
        var lines = new List<QuoteLine>();
        var later = new List<QuoteLine>();
        var unpriced = new List<QuoteLine>();
        var tried = new List<TriedVariant>();
        var terms = await ResolveAsync(gate, plan, ct);

        var settled = await db.Charges.AsNoTracking()
            .Where(c => c.BookingContainerId == box.BookingContainerId && c.MovementCode == step.MovementCode
                        && c.Source == ChargeSource.Window && Settled.Contains(c.Status))
            .Select(c => new { c.ChargeCode, c.BillTo, c.PaymentTermCode, c.Amount, c.ServiceTo, c.Quantity })
            .ToListAsync(ct);

        var equipment = box.EquipmentTypeCode is { Length: > 0 } type
            ? (await master.EquipmentTypesAsync([type], ct)).GetValueOrDefault(type)
            : null;
        var size = equipment?.SizeCode;

        // ── the movement's own charges, and the VAS the clerk ticked (§3 f) ──
        var orderTypeCharges = await master.OrderTypeChargesAsync(plan.OrderTypeCode, ct);
        var ofStep = orderTypeCharges
            .Where(c => !c.IsValueAddedService && string.Equals(c.MovementCode, step.MovementCode, StringComparison.OrdinalIgnoreCase))
            .ToList();
        // A VAS is the order type's, at gate, on this movement (or on any movement when the order type names none).
        var offered = OffersVas(step)
            ? orderTypeCharges.Where(c => c.IsValueAddedService && c.RaiseAtGateIn
                                          && (c.MovementCode is null || string.Equals(c.MovementCode, step.MovementCode, StringComparison.OrdinalIgnoreCase)))
                .ToList()
            : [];
        var ticked = terms.Vas is { Count: > 0 } vas
            ? offered.Where(c => vas.Contains(c.ChargeCode, StringComparer.OrdinalIgnoreCase)).ToList()
            : [];
        var cashMenu = ofStep.Where(c => c.PaymentTermCode is null || c.PaymentTermCode == Cash)
            .Select(c => (Item: c, Kind: QuoteLine.Movement))
            .Concat(ticked.Where(c => c.PaymentTermCode is null || c.PaymentTermCode == Cash).Select(c => (Item: c, Kind: QuoteLine.Vas)))
            .DistinctBy(c => (c.Item.ChargeCode, c.Item.BillTo))
            .ToList();
        var creditMenu = ofStep.Where(c => c.PaymentTermCode == Credit)
            .Select(c => (Item: c, Kind: QuoteLine.Movement))
            .Concat(ticked.Where(c => c.PaymentTermCode == Credit).Select(c => (Item: c, Kind: QuoteLine.Vas)))
            .DistinctBy(c => (c.Item.ChargeCode, c.Item.BillTo))
            .ToList();

        var variants = (await master.ChargeVariantsAsync(cashMenu.Concat(creditMenu).Select(c => c.Item.ChargeCode).Distinct(), ct))
            .ToDictionary(v => (v.ChargeCode, v.BillTo, v.PaymentTermCode));
        var perTrip = variants.Values.Where(v => v.BillingUnitCode == PerTrip).Select(v => v.ChargeCode).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The haulier's own terms (§3 c): matched on (movement, charge), they only
        // ever turn a CASH line into credit — a native credit line stays credit.
        var haulierTerms = terms.HaulierCode is { } haulier
            ? (await master.HaulierChargeTermsAsync(haulier, plan.OrderTypeCode, ct))
                .Where(t => string.Equals(t.MovementCode, step.MovementCode, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(t => t.ChargeCode, t => t.PaymentTermCode, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // "LOAD ONLY GATE-CHARGE FOR" (§3 g): the order type pays its gate charge and no other cash.
        var gateChargeOnly = cashMenu.Any(c => perTrip.Contains(c.Item.ChargeCode))
                             && (await GateChargeOnlyOrderTypesAsync(plan.BranchId, ct)).Contains(plan.OrderTypeCode);

        string? CarriedElsewhere(string chargeCode) =>
            perTrip.Contains(chargeCode) && terms.TripChargeCarriedBy is { } carrier
                ? $"The gate charge is once per truck visit: {carrier} on the same truck carries it. If this box comes on another truck, its gate charge is waived (owner 2026-10-01)."
                : null;

        foreach (var (item, kind) in cashMenu)
        {
            if (settled.Any(s => s.ChargeCode == item.ChargeCode && s.BillTo == item.BillTo && s.PaymentTermCode == Cash))
            {
                tried.Add(new TriedVariant(item.ChargeCode, item.BillTo, Cash, "SETTLED", null, []));
                continue;
            }
            if (CarriedElsewhere(item.ChargeCode) is { } carried)
            {
                tried.Add(new TriedVariant(item.ChargeCode, item.BillTo, Cash, "PER_TRIP_ON_OTHER_BOX", null, [carried]));
                continue;
            }
            if (gateChargeOnly && !perTrip.Contains(item.ChargeCode))
            {
                tried.Add(new TriedVariant(item.ChargeCode, item.BillTo, Cash, "GATE_CHARGE_ONLY", null,
                    [$"{plan.OrderTypeCode} is charged the gate charge only (gate.gate_charge_only_order_types)."]));
                continue;
            }
            if (haulierTerms.TryGetValue(item.ChargeCode, out var haulierTerm) && haulierTerm == Credit)
            {
                tried.Add(new TriedVariant(item.ChargeCode, item.BillTo, Cash, "HAULIER_CREDIT", null,
                    [$"{terms.HaulierCode} has a CREDIT term for {item.ChargeCode} at {step.MovementCode}: billed later, not paid here."]));
                await PriceAsync(item, kind, Credit, byHaulier: true, later);
                continue;
            }
            await PriceAsync(item, kind, Cash, byHaulier: false, lines);
        }

        foreach (var (item, kind) in creditMenu)
        {
            if (CarriedElsewhere(item.ChargeCode) is { } carried)
            {
                tried.Add(new TriedVariant(item.ChargeCode, item.BillTo, Credit, "PER_TRIP_ON_OTHER_BOX", null, [carried]));
                continue;
            }
            await PriceAsync(item, kind, Credit, byHaulier: false, later);
        }

        async Task PriceAsync(OrderTypeChargeRef item, string kind, string term, bool byHaulier, List<QuoteLine> into)
        {
            if (!variants.TryGetValue((item.ChargeCode, item.BillTo, term), out var variant))
            {
                tried.Add(new TriedVariant(item.ChargeCode, item.BillTo, term, "NO_VARIANT", null, [$"MDM has no {term} variant for this payer."]));
                return;
            }

            var result = await pricing.PriceAsync(Request(plan, box, step, size, item.ChargeCode, item.BillTo, term, terms.TruckCategoryCode,
                now, item.DefaultQty ?? 1, freeTimeKind: null, fullEmpty: step.FullEmpty), ct);
            var amount = result.Outcome == PriceOutcomes.Priced ? result.Amount ?? 0 : (decimal?)null;
            if (amount is not > 0)
            {
                // A rate of 0 is "free under this tariff" (owner 2026-10-03). No rate at all, on a
                // cash line, is a hole in the tariff: kept as a NoPrice line so it cannot be skipped.
                tried.Add(new TriedVariant(item.ChargeCode, item.BillTo, term,
                    amount is null ? PriceOutcomes.Unpriced : "PRICED_ZERO", amount, result.PrecedenceTrail));
                if (amount is null && term == Cash)
                    unpriced.Add(Line(kind, variant, PayerFor(plan, item.BillTo), result.Quantity, null, 0m, result, null, null));
                return;
            }

            tried.Add(new TriedVariant(item.ChargeCode, item.BillTo, term, PriceOutcomes.Priced, amount, result.PrecedenceTrail));
            into.Add(Line(kind, variant, PayerFor(plan, item.BillTo), result.Quantity, result.UnitRate,
                amount.Value, result, null, null) with { ByHaulierTerm = byHaulier });
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

                    var result = await pricing.PriceAsync(Request(plan, box, step, size, variant.ChargeCode, variant.BillTo, Cash,
                        terms.TruckCategoryCode, now, stayDays.Value, FreeTimeKinds.Storage, stay.FullEmptyIn), ct);
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

        // ── the VAS menu: every VAS this movement offers, priced as if ticked, so the clerk sees the price on Record ──
        var menu = new List<VasOption>();
        if (offered.Count > 0)
        {
            var vasVariants = (await master.ChargeVariantsAsync(offered.Select(c => c.ChargeCode).Distinct(), ct))
                .ToDictionary(v => (v.ChargeCode, v.BillTo, v.PaymentTermCode));
            foreach (var item in offered.DistinctBy(c => (c.ChargeCode, c.BillTo, c.PaymentTermCode ?? Cash)).OrderBy(c => c.ChargeCode))
            {
                var term = item.PaymentTermCode ?? Cash;
                var isTicked = ticked.Any(t => string.Equals(t.ChargeCode, item.ChargeCode, StringComparison.OrdinalIgnoreCase));
                if (!vasVariants.TryGetValue((item.ChargeCode, item.BillTo, term), out var variant))
                {
                    menu.Add(new VasOption(item.ChargeCode, item.ChargeCode, item.BillTo, term, isTicked, "NO_VARIANT", null));
                    continue;
                }
                var result = await pricing.PriceAsync(Request(plan, box, step, size, item.ChargeCode, item.BillTo, term, terms.TruckCategoryCode,
                    now, item.DefaultQty ?? 1, freeTimeKind: null, fullEmpty: step.FullEmpty), ct);
                menu.Add(result.Outcome == PriceOutcomes.Priced && result.Amount is { } amount
                    ? new VasOption(item.ChargeCode, variant.DescriptionEn, item.BillTo, term, isTicked, amount > 0 ? PriceOutcomes.Priced : "PRICED_ZERO",
                        Line(QuoteLine.Vas, variant, PayerFor(plan, item.BillTo), result.Quantity, result.UnitRate, amount, result, null, null))
                    : new VasOption(item.ChargeCode, variant.DescriptionEn, item.BillTo, term, isTicked, PriceOutcomes.Unpriced, null));
            }
        }

        return new MovementQuote(box.BookingContainerId, box.ContainerNo, step.MovementCode, step.Direction,
            storageApplies ? until : null, stay, stayDays, storageApplies, lines, tried, reeferApplies, power, later, terms, unpriced, menu);
    }

    /// <summary>gate.gate_charge_only_order_types — a JSON array of order type codes; anything unreadable is "none".</summary>
    private async Task<IReadOnlySet<string>> GateChargeOnlyOrderTypesAsync(Guid branchId, CancellationToken ct)
    {
        var raw = await master.GetStringSettingAsync(RevenueSettingKeys.GateChargeOnlyOrderTypes, branchId, ct);
        try
        {
            var codes = string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<string[]>(raw);
            return (codes ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new HashSet<string>();
        }
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
        string chargeCode, string billTo, string paymentTerm, string? truckCategory, DateTimeOffset now, decimal quantity,
        string? freeTimeKind, string? fullEmpty) =>
        new(ModuleCode: "TOS", BranchId: plan.BranchId, EventTime: now, ChargeCode: chargeCode, BillTo: billTo,
            PaymentTermCode: paymentTerm, TruckCategoryCode: truckCategory,
            AgentPartyCode: plan.AgentPartyCode, ForwarderPartyCode: plan.ForwarderPartyCode,
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

        return new QuoteLine(kind, variant.ChargeCodeId, variant.ChargeCode, variant.DescriptionEn, variant.BillTo, variant.PaymentTermCode, payer,
            quantity, unitRate, net, price.CurrencyCode ?? "THB", variant.TaxCode, rate, tax, from, to, price, variant.BillingUnitCode);
    }

    /// <summary>
    /// One priced line as a billing.charge with its price snapshot — the one way a
    /// charge is written, by the window (cash) and by the gate event (credit, 6.3).
    /// </summary>
    public static Charge ChargeFrom(BookingPlan plan, BookingPlanContainer box, string movementCode, Guid? stayId,
        QuoteLine line, string source, string status, DateTimeOffset now)
    {
        var p = line.Price;
        return new Charge
        {
            ChargeId = Guid.CreateVersion7(), TenantId = plan.TenantId, BranchId = plan.BranchId, Source = source,
            BookingId = plan.BookingId, OrderNo = plan.OrderNo, BookingContainerId = box.BookingContainerId,
            ContainerNo = box.ContainerNo, MovementCode = movementCode,
            ContainerStayId = stayId, IsTripCharge = line.IsPerTrip,
            ServiceFrom = line.ServiceFrom, ServiceTo = line.ServiceTo,
            ChargeCodeId = line.ChargeCodeId, ChargeCode = line.ChargeCode, ChargeName = line.ChargeName,
            BillTo = line.BillTo, PaymentTermCode = line.PaymentTermCode, PayerPartyCode = line.PayerPartyCode,
            Quantity = line.Quantity, UnitRate = line.UnitRate, Amount = line.Amount, CurrencyCode = line.CurrencyCode,
            TaxCode = line.TaxCode, TaxRate = line.TaxRate, TaxAmount = line.TaxAmount,
            PricedForDate = p.PricedForDate, ScheduleId = p.ScheduleId, ScheduleNo = p.ScheduleNo, ScheduleVersionNo = p.VersionNo,
            ScheduleType = p.ScheduleType, ScopeRank = p.ScopeRank, TosRateId = p.TosRateId, RateRowVersion = p.RateRowVersion,
            Specificity = p.Specificity, PricingMethod = p.PricingMethod, BillingUnitCode = p.BillingUnitCode,
            PricesIncludeTax = p.PricesIncludeTax, BaseRate = p.BaseRate, FreeUnits = p.FreeUnits,
            ChargeableQuantity = p.ChargeableQuantity, ResolvedAt = p.ResolvedAt, PriceSnapshotJson = JsonSerializer.Serialize(p),
            Status = status, CreatedAt = now, UpdatedAt = now,
        };
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
    /// <summary>gecko_revenue 23: expected, not yet paid or billed (source QUOTE).</summary>
    public const string Quoted = "QUOTED";
    public const string Paid = "PAID";
    public const string Earned = "EARNED";
    public const string Unbilled = "UNBILLED";
    public const string Invoiced = "INVOICED";
    public const string Waived = "WAIVED";
    public const string Cancelled = "CANCELLED";
}

internal static class ChargeSource
{
    public const string Window = "WINDOW";
    public const string Gate = "GATE";
    /// <summary>gecko_revenue 23: the expected charge of a booked box, status QUOTED.</summary>
    public const string Quote = "QUOTE";
}
