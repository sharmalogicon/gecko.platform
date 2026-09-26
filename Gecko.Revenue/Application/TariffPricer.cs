using Gecko.MasterData.Contracts;
using Gecko.Revenue.Contracts;
using Gecko.Revenue.Domain;
using Gecko.Revenue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Application;

/// <summary>
/// The resolver (PLAN.md §5), in the order the plan fixes:
///
///   1. the event's BRANCH-local date
///   2. candidate schedules — APPROVED, in force that day, same module, branch
///      matches or is "all", every party they name matches, booking matches
///   3. order by scope_rank (lower = more specific); ties: SPOT, then the later
///      start, then the higher version
///   4. per schedule, the matching rate row with the highest specificity; the
///      first schedule that has one WINS (fall-through is per charge)
///   5. free time from the first schedule, in the same order, that states a
///      matching rule — a contract may override the public days
///   6. tiers count chargeable units; conditions modify the unit (tier) rate in
///      sequence; amount = quantity × rate, or the tier sum
///
/// Every candidate is reported in the trail, winners and losers alike: "why
/// this price" is a question the depot's accountant will ask.
/// </summary>
internal sealed class TariffPricer(RevenueDbContext db, IMasterDataReferences masterData, BranchCalendar calendar, TimeProvider clock)
    : ITariffPricing
{
    public sealed class InvalidPriceRequestException(string field, string message) : Exception(message)
    {
        public string Field { get; } = field;
    }

    private static string? Up(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().ToUpperInvariant();

    public async Task<PriceResult> PriceAsync(PriceRequest request, CancellationToken ct)
    {
        var module = Up(request.ModuleCode)!;
        var charge = Up(request.ChargeCode)!;
        var billTo = Up(request.BillTo)!;
        var term = Up(request.PaymentTermCode)!;
        var (agent, forwarder, customer, booking) = (Up(request.AgentPartyCode), Up(request.ForwarderPartyCode), Up(request.CustomerPartyCode), Up(request.BookingRef));
        var (orderType, movement, typeCode, cargo, truck) = (Up(request.OrderTypeCode), Up(request.MovementCode), Up(request.EquipmentTypeCode), Up(request.CargoCategoryCode), Up(request.TruckCategoryCode));
        var size = Up(request.EquipmentSize);
        if (request.Quantity < 0) throw new InvalidPriceRequestException("quantity", "Quantity cannot be negative.");

        // The equipment type decides size, reefer and out-of-gauge — the request cannot contradict it.
        var (isReefer, isOog) = (false, false);
        if (typeCode is not null)
        {
            if (!(await masterData.EquipmentTypesAsync([typeCode], ct)).TryGetValue(typeCode, out var type))
                throw new InvalidPriceRequestException("equipmentTypeCode", $"Unknown equipment type '{typeCode}'.");
            if (size is not null && size != type.SizeCode)
                throw new InvalidPriceRequestException("equipmentSize", $"'{typeCode}' is a {type.SizeCode}ft type, not {size}ft.");
            (size, isReefer, isOog) = (type.SizeCode, type.IsReefer, type.IsOog);
        }

        var date = await calendar.LocalDateAsync(request.BranchId, request.EventTime, ct);
        var trail = new List<string>();

        // ── 2–3. candidate schedules ────────────────────────────────────────
        var candidates = (await db.Schedules.AsNoTracking()
                .Where(s => s.Status == ScheduleStatuses.Approved && s.ModuleCode == module && s.EffectiveFrom <= date
                    && (s.BranchId == null || s.BranchId == request.BranchId)
                    && (s.AgentPartyCode == null || s.AgentPartyCode == agent)
                    && (s.ForwarderPartyCode == null || s.ForwarderPartyCode == forwarder)
                    && (s.CustomerPartyCode == null || s.CustomerPartyCode == customer)
                    && (s.BookingRef == null || s.BookingRef == booking))
                .Select(s => new
                {
                    Schedule = s,
                    Until = db.VwScheduleEffectives.Where(e => e.ScheduleId == s.ScheduleId).Select(e => e.EffectiveUntil).FirstOrDefault(),
                    Superseded = db.VwScheduleEffectives.Any(e => e.ScheduleId == s.ScheduleId && e.IsFullySuperseded == true),
                })
                .ToListAsync(ct))
            .Where(c => !c.Superseded && (c.Until is null || date <= c.Until))
            .OrderBy(c => c.Schedule.ScopeRank)
            .ThenBy(c => c.Schedule.ScheduleType == ScheduleTypes.Spot ? 0 : 1)
            .ThenByDescending(c => c.Schedule.EffectiveFrom)
            .ThenByDescending(c => c.Schedule.VersionNo)
            .Select(c => c.Schedule)
            .ToList();
        var ids = candidates.Select(s => s.ScheduleId).ToList();

        // ── 4. best matching rate in each candidate ─────────────────────────
        var rates = await db.TosRates.AsNoTracking()
            .Where(r => ids.Contains(r.ScheduleId) && r.ChargeCode == charge && r.BillTo == billTo && r.PaymentTermCode == term
                && (r.OrderTypeCode == null || r.OrderTypeCode == orderType)
                && (r.MovementCode == null || r.MovementCode == movement)
                && (r.EquipmentTypeCode == null || r.EquipmentTypeCode == typeCode)
                && (r.EquipmentSize == null || r.EquipmentSize == size)
                && (r.CargoCategoryCode == null || r.CargoCategoryCode == cargo)
                && (r.TruckCategoryCode == null || r.TruckCategoryCode == truck))
            .ToListAsync(ct);
        var bestBySchedule = rates.GroupBy(r => r.ScheduleId).ToDictionary(g => g.Key, g => g.MaxBy(r => r.Specificity)!);

        Infrastructure.Persistence.Entities.Schedule? winner = null;
        foreach (var s in candidates)
        {
            var name = $"{s.ScheduleNo} v{s.VersionNo} ({s.ScheduleType}, rank {s.ScopeRank})";
            if (!bestBySchedule.TryGetValue(s.ScheduleId, out var best))
                trail.Add($"{name}: no {charge} rate for {billTo}/{term} matching this shipment");
            else if (winner is null)
            {
                winner = s;
                trail.Add($"{name}: {DescribeRate(best)} — CHOSEN");
            }
            else
                trail.Add($"{name}: {DescribeRate(best)} — outranked");
        }

        var now = clock.GetUtcNow();
        if (winner is null)
        {
            if (candidates.Count == 0) trail.Add($"No approved {module} tariff applies to this shipment on {date:yyyy-MM-dd}.");
            return new PriceResult(PriceOutcomes.Unpriced, charge, billTo, term, date,
                null, null, null, null, null, null, null, null, null, null, null, null, null, null,
                request.Quantity, null, null, null, [], [], null, trail, now);
        }

        var rate = bestBySchedule[winner.ScheduleId];

        // ── 5. free time ────────────────────────────────────────────────────
        decimal? freeUnits = null;
        string? freeFrom = null;
        var kind = Up(request.FreeTimeKind);
        var timeBased = rate.TierBasis is { } basis && TierBases.IsTimeBased(basis);
        if (kind is not null && timeBased)
        {
            var rules = (await db.FreeTimeRules.AsNoTracking()
                    .Where(f => ids.Contains(f.ScheduleId) && f.FreeTimeKind == kind)
                    .ToListAsync(ct))
                .ToLookup(f => f.ScheduleId);
            var group = FreeTimeMatcher.CargoGroup(request.IsDangerousGoods, isReefer);
            foreach (var s in candidates)
            {
                var match = FreeTimeMatcher.Best(
                    rules[s.ScheduleId].Select(f => new FreeTimeSpec(f.FreeTimeKind, f.FullEmpty, f.Direction, f.CargoGroup, f.EquipmentSize, f.FreeUnits)),
                    kind, Up(request.FullEmpty), Up(request.Direction), group, size);
                if (match is null) continue;
                (freeUnits, freeFrom) = (match.FreeUnits, s.ScheduleNo);
                trail.Add($"Free time: {match.FreeUnits} {(kind == FreeTimeKinds.TruckWaiting ? "hours" : "days")} from {s.ScheduleNo} v{s.VersionNo}");
                break;
            }
            if (freeUnits is null) trail.Add($"Free time: no {kind} rule matches — charged from the first unit");
        }

        // ── 6. conditions and amount ────────────────────────────────────────
        var conditions = await LoadConditionsAsync(rate.TosRateId, ct);
        var context = new ConditionContext(size, typeCode, cargo, truck, isReefer, request.IsDangerousGoods, isOog, request.GrossWeightKg);
        var holding = conditions.Where(c => ConditionEvaluator.Holds(c, context)).ToList();

        decimal Modify(decimal unit, List<AppliedCondition>? log)
        {
            foreach (var c in holding)
            {
                var after = ConditionRules.Apply(unit, c.ModifierOp, c.ModifierValue);
                log?.Add(new AppliedCondition(c.SequenceNo, ConditionEvaluator.Describe(c), unit, after));
                unit = after;
            }
            return unit;
        }

        var applied = new List<AppliedCondition>();
        var pricedTiers = new List<PricedTier>();
        decimal? baseRate = null, unitRate = null, chargeable = null;
        decimal amount;

        if (rate.PricingMethod == PricingMethods.Flat)
        {
            baseRate = rate.Rate!.Value;
            unitRate = Modify(baseRate.Value, applied);
            amount = unitRate.Value * request.Quantity;
        }
        else
        {
            var tiers = await db.RateTiers.AsNoTracking()
                .Where(t => t.OwnerType == "TOS_RATE" && t.OwnerId == rate.TosRateId)
                .OrderBy(t => t.FromQty)
                .ToListAsync(ct);
            chargeable = timeBased ? Math.Max(0, request.Quantity - (freeUnits ?? 0)) : request.Quantity;

            // Conditions act on each tier's unit rate; the log shows the first tier's effect.
            var modified = tiers.Select((t, i) => new Tier(t.FromQty, t.ToQty, Modify(t.Rate, i == 0 ? applied : null))).ToList();
            var quote = TierPricing.Price(modified, rate.PricingMethod, chargeable.Value);
            var baseByFrom = tiers.ToDictionary(t => t.FromQty, t => t.Rate);
            pricedTiers.AddRange(quote.Lines.Select(l => new PricedTier(l.FromQty, l.ToQty, l.Quantity, baseByFrom[l.FromQty], l.Rate, l.Amount)));
            amount = quote.Amount;
        }

        return new PriceResult(
            PriceOutcomes.Priced, charge, billTo, term, date,
            winner.ScheduleId, winner.ScheduleNo, winner.VersionNo, winner.ScheduleType, winner.ScopeRank,
            rate.TosRateId, Convert.ToBase64String(rate.RowVersion), rate.Specificity,
            rate.PricingMethod, rate.BillingUnitCode, winner.CurrencyCode, winner.PricesIncludeTax,
            baseRate, unitRate, request.Quantity, freeUnits, freeFrom, chargeable,
            pricedTiers, applied, decimal.Round(amount, 4), trail, now);
    }

    private static string DescribeRate(Infrastructure.Persistence.Entities.TosRate r) =>
        (r.PricingMethod == PricingMethods.Flat ? $"{r.Rate:0.##}" : $"{r.PricingMethod} by {r.TierBasis}")
        + $" (specificity {r.Specificity})";

    private async Task<List<ConditionSpec>> LoadConditionsAsync(Guid rateId, CancellationToken ct)
    {
        var rows = await db.RateConditions.AsNoTracking()
            .Where(c => c.OwnerType == "TOS_RATE" && c.OwnerId == rateId)
            .OrderBy(c => c.SequenceNo)
            .ToListAsync(ct);
        var ids = rows.Select(c => c.RateConditionId).ToList();
        var values = (await db.RateConditionValues.AsNoTracking()
                .Where(v => ids.Contains(v.RateConditionId))
                .ToListAsync(ct))
            .ToLookup(v => v.RateConditionId, v => v.ValueCode);
        return rows.Select(c => new ConditionSpec(c.SequenceNo, c.Axis, c.Op, values[c.RateConditionId].ToList(),
            c.NumberValue, c.BoolValue, c.ModifierOp, c.ModifierValue, c.Label)).ToList();
    }
}
