namespace Gecko.Revenue.Domain;

public sealed record Tier(decimal FromQty, decimal? ToQty, decimal Rate);

public sealed record TierLine(decimal FromQty, decimal? ToQty, decimal Quantity, decimal Rate, decimal Amount);

public sealed record TierQuote(decimal ChargeableQuantity, IReadOnlyList<TierLine> Lines, decimal Amount);

/// <summary>
/// Prices a quantity against a set of tiers, and says what is wrong with a tier
/// set. Pure: the SQL view tariff.vw_rate_tier_defects states the same rules for
/// fixtures and the ETL; a test keeps the two in step.
///
/// THE USER'S RULES (2026-09-16):
///   Q4  tiers count CHARGEABLE units — free units are removed BEFORE this is
///       called; the caller passes what is left.
///   Q3  TIERED_INCREMENTAL is the default: each unit at its own tier's rate.
///       12 days, 3 free, 1–7 @160, 8+ @275 → 9 chargeable → 7×160 + 2×275 = 1,670.
/// </summary>
public static class TierPricing
{
    public const string FirstNotOne = "FIRST_NOT_1";
    public const string GapOrOverlap = "GAP_OR_OVERLAP";
    public const string OpenNotLast = "OPEN_NOT_LAST";
    public const string NoTiers = "NO_TIERS";
    public const string BadRange = "BAD_RANGE";

    public static IReadOnlyList<string> Defects(IReadOnlyCollection<Tier> tiers, string basis)
    {
        if (tiers.Count == 0) return [NoTiers];

        var defects = new List<string>();
        var ordered = tiers.OrderBy(t => t.FromQty).ToList();

        if (ordered.Any(t => t.FromQty < 0 || t.Rate < 0 || (t.ToQty is { } to && to < t.FromQty)))
            defects.Add(BadRange);
        if (TierBases.IsTimeBased(basis) && ordered[0].FromQty != 1)
            defects.Add(FirstNotOne);

        for (var i = 1; i < ordered.Count; i++)
        {
            var previous = ordered[i - 1];
            if (previous.ToQty is null) { defects.Add(OpenNotLast); continue; }
            if (ordered[i].FromQty != previous.ToQty + 1) defects.Add(GapOrOverlap);
        }

        return defects.Distinct().ToList();
    }

    /// <param name="chargeableQuantity">Units AFTER free time — never the raw stay.</param>
    public static TierQuote Price(IReadOnlyCollection<Tier> tiers, string pricingMethod, decimal chargeableQuantity)
    {
        if (Defects(tiers, TierBases.Teu) is { Count: > 0 } defects)
            throw new InvalidOperationException($"Cannot price against a broken tier set: {string.Join(", ", defects)}.");

        var qty = Math.Max(chargeableQuantity, 0);
        var ordered = tiers.OrderBy(t => t.FromQty).ToList();
        if (qty == 0) return new TierQuote(0, [], 0);

        switch (pricingMethod)
        {
            case PricingMethods.TieredIncremental:
            {
                var lines = new List<TierLine>();
                foreach (var tier in ordered)
                {
                    if (qty < tier.FromQty) break;
                    var upper = tier.ToQty is { } to ? Math.Min(qty, to) : qty;
                    var units = upper - tier.FromQty + 1;
                    lines.Add(new TierLine(tier.FromQty, tier.ToQty, units, tier.Rate, units * tier.Rate));
                }
                return new TierQuote(qty, lines, lines.Sum(l => l.Amount));
            }

            case PricingMethods.TieredBand:
            {
                // Every unit at the rate of the tier the quantity reaches.
                var tier = Reached(ordered, qty);
                return new TierQuote(qty, [new TierLine(tier.FromQty, tier.ToQty, qty, tier.Rate, qty * tier.Rate)], qty * tier.Rate);
            }

            case PricingMethods.TieredBlock:
            {
                // A flat amount for every tier entered (Vector's Qty-1 slab lines).
                var entered = ordered.Where(t => qty >= t.FromQty)
                    .Select(t => new TierLine(t.FromQty, t.ToQty, 1, t.Rate, t.Rate)).ToList();
                return new TierQuote(qty, entered, entered.Sum(l => l.Amount));
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(pricingMethod), pricingMethod, "Not a tiered pricing method.");
        }
    }

    private static Tier Reached(List<Tier> ordered, decimal qty) =>
        ordered.LastOrDefault(t => qty >= t.FromQty) ?? ordered[0];
}
