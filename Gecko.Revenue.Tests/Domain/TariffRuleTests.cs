using Gecko.Revenue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Tests.Domain;

public sealed class TariffRuleTests
{
    // ── lifecycle ───────────────────────────────────────────────────────────

    private static readonly DateOnly Jan1 = new(2026, 1, 1);
    private static readonly DateOnly Jun30 = new(2026, 6, 30);

    [Theory]
    [InlineData("2025-12-31", "SCHEDULED")]
    [InlineData("2026-01-01", "ACTIVE")]
    [InlineData("2026-06-30", "ACTIVE")]
    [InlineData("2026-07-01", "EXPIRED")]
    public void An_approved_version_is_described_by_the_day(string today, string expected) =>
        Assert.Equal(expected, ScheduleLifecycle.Describe(ScheduleStatuses.Approved, Jan1, Jun30, false, DateOnly.Parse(today)));

    [Fact]
    public void A_version_replaced_before_it_started_is_superseded() =>
        Assert.Equal(ScheduleLifecycle.Superseded, ScheduleLifecycle.Describe(ScheduleStatuses.Approved, Jan1, null, true, Jan1));

    [Fact]
    public void Unapproved_versions_are_described_by_their_status() =>
        Assert.Equal(ScheduleStatuses.Pending, ScheduleLifecycle.Describe(ScheduleStatuses.Pending, Jan1, null, false, Jan1));

    [Fact]
    public void Only_a_draft_is_editable()
    {
        Assert.True(ScheduleLifecycle.IsEditable(ScheduleStatuses.Draft));
        Assert.All([ScheduleStatuses.Pending, ScheduleStatuses.Approved, ScheduleStatuses.Rejected, ScheduleStatuses.Withdrawn],
            s => Assert.False(ScheduleLifecycle.IsEditable(s)));
    }

    [Fact]
    public void An_approved_tariff_cannot_be_withdrawn() =>
        Assert.False(ScheduleLifecycle.CanWithdraw(ScheduleStatuses.Approved));

    [Fact]
    public void Maker_and_submitter_are_not_independent_approvers()
    {
        Guid maker = Guid.NewGuid(), submitter = Guid.NewGuid(), checker = Guid.NewGuid();
        Assert.False(ScheduleLifecycle.IsIndependentApprover(maker, maker, submitter));
        Assert.False(ScheduleLifecycle.IsIndependentApprover(submitter, maker, submitter));
        Assert.True(ScheduleLifecycle.IsIndependentApprover(checker, maker, submitter));
        Assert.True(ScheduleLifecycle.IsIndependentApprover(checker, null, null));   // migrated / fixture rows
    }

    // ── conditions ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("WEIGHT_KG", "GT", 0, 30000.0, null, "ADD", 300.0, true)]
    [InlineData("WEIGHT_KG", "GT", 0, null, null, "ADD", 300.0, false)]        // no number
    [InlineData("CARGO_CATEGORY", "IS", 0, null, true, "ADD", 1.0, false)]     // IS on a code axis
    [InlineData("IS_REEFER", "IS", 0, null, true, "ADD", 200.0, true)]
    [InlineData("EQUIPMENT_SIZE", "IN", 2, null, null, "MULTIPLY", 1.5, true)]
    [InlineData("EQUIPMENT_SIZE", "EQ", 2, null, null, "ADD", 1.0, false)]     // EQ with two values
    [InlineData("EQUIPMENT_SIZE", "IN", 0, null, null, "ADD", 1.0, false)]     // IN with none
    [InlineData("CARGO_CATEGORY", "IN", 1, null, null, "MULTIPLY", 0.0, false)] // zero factor
    [InlineData("CARGO_CATEGORY", "EQ", 1, null, null, "REPLACE", -1.0, false)] // negative replacement
    public void Condition_shapes(string axis, string op, int values, double? number, bool? flag, string modifier, double value, bool ok) =>
        Assert.Equal(ok, ConditionRules.Problem(axis, op, values, (decimal?)number, flag, modifier, (decimal)value) is null);

    [Fact]
    public void Modifiers_apply_in_sequence_and_order_matters()
    {
        var addThenMultiply = ConditionRules.Apply(ConditionRules.Apply(1000, ModifierOps.Add, 200), ModifierOps.Multiply, 1.5m);
        var multiplyThenAdd = ConditionRules.Apply(ConditionRules.Apply(1000, ModifierOps.Multiply, 1.5m), ModifierOps.Add, 200);
        Assert.Equal(1800m, addThenMultiply);
        Assert.Equal(1700m, multiplyThenAdd);
    }

    // ── C# and SQL say the same thing ───────────────────────────────────────

    [Fact]
    public void Specificity_weights_are_ordered_so_a_higher_axis_beats_all_lower_ones_together() =>
        Assert.True(RateSpecificity.OrderType > RateSpecificity.Of(false, true, true, true, true, true));

    /// <summary>
    /// scope_rank is computed twice — persisted in SQL, and in C# for callers
    /// without a row. Every fixture schedule must get the same answer from both.
    /// </summary>
    [Fact]
    public async Task Scope_rank_in_csharp_matches_the_persisted_column()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var tenant in new[] { TestDatabase.Sct, TestDatabase.Sss })
        {
            await using var db = TestDatabase.ForTenant(tenant);
            var rows = await db.Schedules.AsNoTracking().ToListAsync(ct);
            Assert.NotEmpty(rows);
            Assert.All(rows, s => Assert.Equal(
                s.ScopeRank,
                ScheduleScope.Rank(s.ScheduleType, s.BranchId is not null, s.AgentPartyId is not null,
                    s.ForwarderPartyId is not null, s.CustomerPartyId is not null, s.BookingRef is not null)));
        }
    }

    /// <summary>Same for specificity, on every fixture rate.</summary>
    [Fact]
    public async Task Specificity_in_csharp_matches_the_persisted_column()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
        var rates = await db.TosRates.AsNoTracking().ToListAsync(ct);
        Assert.NotEmpty(rates);
        Assert.All(rates, r => Assert.Equal(
            (short?)RateSpecificity.Of(r.OrderTypeId is not null, r.MovementId is not null, r.EquipmentTypeId is not null,
                r.EquipmentSize is not null, r.CargoCategoryCode is not null, r.TruckCategoryCode is not null),
            r.Specificity));
    }

    /// <summary>The fixture tier sets pass both the SQL view and the C# rules.</summary>
    [Fact]
    public async Task Fixture_tiers_have_no_defects_in_either_language()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
        Assert.Empty(await db.VwRateTierDefects.ToListAsync(ct));

        var tiered = await db.TosRates.AsNoTracking().Where(r => r.TierBasis != null).ToListAsync(ct);
        var tiers = (await db.RateTiers.AsNoTracking().ToListAsync(ct)).ToLookup(t => t.OwnerId);
        Assert.NotEmpty(tiered);
        Assert.All(tiered, r => Assert.Empty(TierPricing.Defects(
            tiers[r.TosRateId].Select(t => new Tier(t.FromQty, t.ToQty, t.Rate)).ToList(), r.TierBasis!)));
    }

    [Fact]
    public async Task A_context_with_no_tenant_refuses_to_open_a_connection()
    {
        await using var db = TestDatabase.ForTenant(null);
        await Assert.ThrowsAsync<Gecko.Data.MissingTenantContextException>(
            () => db.Schedules.AnyAsync(TestContext.Current.CancellationToken));
    }
}
