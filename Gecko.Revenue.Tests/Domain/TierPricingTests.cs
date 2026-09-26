using Gecko.Revenue.Domain;

namespace Gecko.Revenue.Tests.Domain;

/// <summary>The pricing rules the user set on 2026-09-16 (PLAN.md Q3, Q4), stated as arithmetic.</summary>
public sealed class TierPricingTests
{
    private static readonly Tier[] LadenStorage20 = [new(1, 7, 160), new(8, 14, 275), new(15, null, 390)];

    /// <summary>
    /// The example the user signed off: 12 days in the yard, 3 free, so 9
    /// CHARGEABLE days — tier day 1 is calendar day 4 — priced incrementally.
    /// </summary>
    [Fact]
    public void Twelve_days_with_three_free_costs_1670()
    {
        const int daysInYard = 12, freeDays = 3;

        var quote = TierPricing.Price(LadenStorage20, PricingMethods.TieredIncremental, daysInYard - freeDays);

        Assert.Equal(9, quote.ChargeableQuantity);
        Assert.Equal([(7m, 160m), (2m, 275m)], quote.Lines.Select(l => (l.Quantity, l.Rate)));
        Assert.Equal(1670m, quote.Amount);
    }

    [Theory]
    [InlineData(0, 0)]          // inside free time
    [InlineData(1, 160)]
    [InlineData(7, 1120)]       // exactly the first tier
    [InlineData(8, 1395)]       // first day of the second
    [InlineData(20, 5385)]      // 7×160 + 7×275 + 6×390 = 1120 + 1925 + 2340 — into the open tier
    public void Incremental_prices_each_day_at_its_own_tier(int chargeableDays, int expected) =>
        Assert.Equal(expected, TierPricing.Price(LadenStorage20, PricingMethods.TieredIncremental, chargeableDays).Amount);

    [Fact]
    public void Band_prices_every_day_at_the_tier_reached() =>
        Assert.Equal(9 * 275m, TierPricing.Price(LadenStorage20, PricingMethods.TieredBand, 9).Amount);

    [Fact]
    public void Block_charges_once_per_tier_entered() =>
        Assert.Equal(160m + 275m, TierPricing.Price(LadenStorage20, PricingMethods.TieredBlock, 9).Amount);

    /// <summary>Vector's live BookingStatement has 1–6 then 6–12: day 6 is in both.</summary>
    [Fact]
    public void Vectors_overlapping_slabs_are_a_defect() =>
        Assert.Contains(TierPricing.GapOrOverlap, TierPricing.Defects([new(1, 6, 350), new(6, 12, 550)], TierBases.Day));

    [Fact]
    public void A_gap_is_a_defect() =>
        Assert.Contains(TierPricing.GapOrOverlap, TierPricing.Defects([new(1, 7, 1), new(9, null, 2)], TierBases.Day));

    [Fact]
    public void Day_tiers_start_at_the_first_chargeable_day() =>
        Assert.Contains(TierPricing.FirstNotOne, TierPricing.Defects([new(4, null, 1)], TierBases.Day));

    [Fact]
    public void Teu_bands_may_start_at_zero() =>
        Assert.Empty(TierPricing.Defects([new(0, 50, 30), new(51, null, 25)], TierBases.FleetTeu));

    [Fact]
    public void Only_the_last_tier_may_be_open() =>
        Assert.Contains(TierPricing.OpenNotLast, TierPricing.Defects([new(1, null, 1), new(2, 5, 2)], TierBases.Day));

    [Fact]
    public void No_tiers_is_a_defect() =>
        Assert.Equal([TierPricing.NoTiers], TierPricing.Defects([], TierBases.Hour));

    [Fact]
    public void A_broken_tier_set_is_never_priced() =>
        Assert.Throws<InvalidOperationException>(() =>
            TierPricing.Price([new(1, 6, 350), new(6, 12, 550)], PricingMethods.TieredIncremental, 8));
}
