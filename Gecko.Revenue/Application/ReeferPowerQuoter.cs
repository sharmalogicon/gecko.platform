using Gecko.MasterData.Contracts;
using Gecko.Revenue.Contracts;
using Gecko.Revenue.Domain;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Application;

/// <summary>The answer to "what does this visit's reefer power cost", and why — never a guessed amount.</summary>
internal static class ReeferOutcomes
{
    /// <summary>Hours × the tenant's hourly tariff rate.</summary>
    public const string Priced = "PRICED";
    /// <summary>No (non-voided) plug session on the visit.</summary>
    public const string NoSessions = "NO_SESSIONS";
    /// <summary>The tenant has no active REEFER charge code billed per hour with a CASH variant.</summary>
    public const string ChargeCodeNotSet = "CHARGE_CODE_NOT_SET";
    /// <summary>No approved tariff has an hourly rate for it (or the rate found is not per hour).</summary>
    public const string RateNotSet = "RATE_NOT_SET";
    /// <summary>The rate priced the hours at ฿0 (0 hours, or a zero rate).</summary>
    public const string PricedZero = "PRICED_ZERO";
}

/// <summary>What the pricing context knows about the box and who pays. Everything but the branch and box is optional.</summary>
internal sealed record ReeferPricingContext(
    Guid BranchId, string ContainerNo, string? EquipmentTypeCode,
    BookingPlan? Plan = null, BookingPlanContainer? Box = null,
    string? MovementCode = null, string? Direction = null, string? FullEmpty = null);

/// <summary>One CASH variant of an hourly REEFER charge code, priced (or not) for the visit's hours.</summary>
internal sealed record ReeferVariantPrice(ChargeVariantRef Variant, string Outcome, PriceResult? Price, decimal? Amount, IReadOnlyList<string> Trail);

internal sealed record ReeferPowerQuote(
    int BillableHours, int MinutesPlugged, int Sessions, string Outcome,
    string? ChargeCode, decimal? Amount, string? CurrencyCode, string Message,
    IReadOnlyList<ReeferVariantPrice> Variants)
{
    /// <summary>A reefer power rate exists for this box, even if nothing is owed yet (0 hours) — the window must see it.</summary>
    public bool RateAvailable => Variants.Any(v => v.Outcome is ReeferOutcomes.Priced or ReeferOutcomes.PricedZero);
}

/// <summary>
/// Reefer power (owner's decision: per STARTED hour plugged in), priced through the
/// ordinary tariff — no price table of its own.
///
/// WHAT A TENANT CONFIGURES, and nothing is charged until it has:
///   1. MDM: an active charge code with charge_type REEFER whose billing unit counts
///      HOURS (lookup.billing_unit.quantity_source = HOUR — PER_HOUR), with an active
///      CASH variant for the payer (bill-to). Every such variant is priced, as
///      storage prices every STORAGE variant.
///   2. Tariff: an approved TOS schedule with a rate for that code × bill-to × CASH,
///      per hour — FLAT on an hourly billing unit (amount = hours × rate), or tiered
///      with tier basis HOUR. A rate on any other unit is refused (RATE_NOT_SET),
///      never multiplied by hours.
/// No order-type linkage is needed: like storage, no movement raises it.
///
/// Hours: <see cref="ReeferPower.Measure"/> — the visit's sessions summed (an open
/// one to the as-at time), rounded up once. No free time: the resolver removes free
/// units only for a free-time kind (STORAGE / CHASSIS / TRUCK_WAITING), and reefer
/// power has none, so every started hour is chargeable.
/// </summary>
internal sealed class ReeferPowerQuoter(RevenueDbContext db, IMasterDataReferences master, ITariffPricing pricing)
{
    private const string Cash = "CASH";
    private const string HourSource = "HOUR";
    public const string ChargeType = "REEFER";

    public async Task<ReeferPowerQuote> QuoteAsync(IReadOnlyCollection<ReeferSession> sessions, ReeferPricingContext context,
        DateTimeOffset asAt, CancellationToken ct)
    {
        var time = ReeferPower.Measure(sessions.Select(s => new PlugSpan(s.PluggedInAt, s.PluggedOutAt, s.IsVoided)), asAt);
        var hourly = await db.BillingUnits.AsNoTracking().Where(u => u.QuantitySource == HourSource).Select(u => u.Code).ToListAsync(ct);

        var variants = (await master.ChargeVariantsOfTypeAsync(ChargeType, ct))
            .Where(v => v.PaymentTermCode == Cash && hourly.Contains(v.BillingUnitCode))
            .ToList();

        if (variants.Count == 0)
            return new ReeferPowerQuote(time.BillableHours, time.MinutesPlugged, time.Sessions,
                time.Sessions == 0 ? ReeferOutcomes.NoSessions : ReeferOutcomes.ChargeCodeNotSet, null, null, null,
                time.Sessions == 0
                    ? "No plug session on this visit."
                    : "Reefer power is not charged: the tenant has no active REEFER charge code billed per hour (PER_HOUR) with a CASH variant.",
                []);

        var priced = new List<ReeferVariantPrice>();
        foreach (var variant in variants)
            priced.Add(await PriceAsync(variant, context, time.BillableHours, asAt, hourly, ct));

        var charged = priced.Where(p => p.Outcome == ReeferOutcomes.Priced).ToList();
        var currency = priced.Select(p => p.Price?.CurrencyCode).FirstOrDefault(c => c is not null);

        if (time.Sessions == 0)
            return new ReeferPowerQuote(0, 0, 0, ReeferOutcomes.NoSessions, null, null, null, "No plug session on this visit.", priced);

        if (charged.Count > 0)
            return new ReeferPowerQuote(time.BillableHours, time.MinutesPlugged, time.Sessions, ReeferOutcomes.Priced,
                string.Join(",", charged.Select(c => c.Variant.ChargeCode).Distinct()),
                charged.Sum(c => c.Amount!.Value), charged[0].Price!.CurrencyCode ?? currency,
                $"{time.BillableHours} started hour(s) plugged in, priced by " +
                string.Join("; ", charged.Select(c => $"{c.Variant.ChargeCode}/{c.Variant.BillTo} on {c.Price!.ScheduleNo} v{c.Price.VersionNo}")) + ".",
                priced);

        var zero = priced.FirstOrDefault(p => p.Outcome == ReeferOutcomes.PricedZero);
        if (zero is not null)
            return new ReeferPowerQuote(time.BillableHours, time.MinutesPlugged, time.Sessions, ReeferOutcomes.PricedZero,
                zero.Variant.ChargeCode, 0m, zero.Price!.CurrencyCode ?? currency,
                $"{time.BillableHours} started hour(s); {zero.Variant.ChargeCode} prices them at 0 on {zero.Price.ScheduleNo}.", priced);

        return new ReeferPowerQuote(time.BillableHours, time.MinutesPlugged, time.Sessions, ReeferOutcomes.RateNotSet,
            string.Join(",", priced.Select(p => p.Variant.ChargeCode).Distinct()), null, null,
            "Reefer power is not charged: " + string.Join(" ", priced.Select(p => p.Trail.LastOrDefault() ?? $"No rate for {p.Variant.ChargeCode}.")),
            priced);
    }

    private async Task<ReeferVariantPrice> PriceAsync(ChargeVariantRef variant, ReeferPricingContext c, int hours, DateTimeOffset asAt,
        List<string> hourly, CancellationToken ct)
    {
        var plan = c.Plan;
        var request = new PriceRequest(
            ModuleCode: "TOS", BranchId: c.BranchId, EventTime: asAt, ChargeCode: variant.ChargeCode,
            BillTo: variant.BillTo, PaymentTermCode: Cash, AgentPartyCode: plan?.AgentPartyCode,
            ForwarderPartyCode: plan?.ForwarderPartyCode, CustomerPartyCode: plan?.CustomerPartyCode, BookingRef: plan?.OrderNo,
            OrderTypeCode: plan?.OrderTypeCode, MovementCode: c.MovementCode, EquipmentTypeCode: c.EquipmentTypeCode,
            CargoCategoryCode: plan?.CargoCategoryCode, IsDangerousGoods: c.Box?.IsDangerousGoods ?? false,
            GrossWeightKg: c.Box?.DeclaredGrossWeightKg, Quantity: hours, FreeTimeKind: null,
            FullEmpty: c.FullEmpty, Direction: c.Direction);

        PriceResult result;
        try
        {
            result = await pricing.PriceAsync(request, ct);
        }
        catch (TariffPricer.InvalidPriceRequestException ex)
        {
            return new ReeferVariantPrice(variant, ReeferOutcomes.RateNotSet, null, null, [$"{variant.ChargeCode}: {ex.Message}"]);
        }

        var trail = result.PrecedenceTrail.ToList();
        if (result.Outcome != PriceOutcomes.Priced)
        {
            trail.Add($"No approved tariff has a {variant.ChargeCode} rate for {variant.BillTo}/CASH on this box.");
            return new ReeferVariantPrice(variant, ReeferOutcomes.RateNotSet, result, null, trail);
        }

        // Only an hourly rate may be multiplied by hours: FLAT on an hourly unit, or tiers by HOUR.
        var basis = result.TosRateId is { } rateId
            ? await db.TosRates.AsNoTracking().Where(r => r.TosRateId == rateId).Select(r => r.TierBasis).SingleOrDefaultAsync(ct)
            : null;
        var perHour = result.BillingUnitCode is { } unit && hourly.Contains(unit)
                      && (result.PricingMethod == PricingMethods.Flat || basis == TierBases.Hour);
        if (!perHour)
        {
            trail.Add($"The {variant.ChargeCode} rate on {result.ScheduleNo} is {result.BillingUnitCode}" +
                      (basis is null ? "" : $" by {basis}") + ": reefer rate must be per hour.");
            return new ReeferVariantPrice(variant, ReeferOutcomes.RateNotSet, result, null, trail);
        }

        var amount = CashQuoter.Money(result.Amount ?? 0);
        return new ReeferVariantPrice(variant, amount > 0 ? ReeferOutcomes.Priced : ReeferOutcomes.PricedZero, result, amount, trail);
    }
}
