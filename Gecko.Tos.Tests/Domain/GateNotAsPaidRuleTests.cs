using Gecko.Tos.Domain;

namespace Gecko.Tos.Tests.Domain;

/// <summary>
/// GATE_CHARGING_DESIGN §7.4 / §7.5: the truck at the gate against what the
/// coupon was priced with. A different category is a warning, a different
/// haulier is said and nothing more; neither ever stops the box.
/// </summary>
public sealed class GateNotAsPaidRuleTests
{
    [Fact]
    public void The_same_truck_as_paid_says_nothing()
    {
        Assert.Empty(GateRules.NotAsPaid("RCT-1", true, "18_WHEEL", "18_wheel", "HAU-SHT", "HAU-SHT"));
        Assert.Empty(GateRules.NotAsPaid("RCT-1", true, null, null, null, null));
    }

    [Fact]
    public void A_different_truck_category_is_a_warning_that_names_both()
    {
        var finding = Assert.Single(GateRules.NotAsPaid("RCT-1", true, "6_WHEEL", "18_WHEEL", "HAU-SHT", "HAU-SHT"));
        Assert.Equal(("TRUCK_CATEGORY_NOT_AS_PAID", GateSeverity.Warn), (finding.Code, finding.Severity));
        Assert.Contains("18_WHEEL", finding.Message);
        Assert.Contains("6_WHEEL", finding.Message);
        Assert.Contains("RCT-1", finding.Message);

        // Paid at the any-truck rate, and the truck has a category of its own (or the reverse).
        Assert.Single(GateRules.NotAsPaid("RCT-1", true, null, "18_WHEEL", null, null), f => f.Code == "TRUCK_CATEGORY_NOT_AS_PAID");
        Assert.Single(GateRules.NotAsPaid("RCT-1", true, "18_WHEEL", null, null, null), f => f.Code == "TRUCK_CATEGORY_NOT_AS_PAID");
    }

    [Fact]
    public void A_coupon_no_cash_was_taken_on_is_not_judged_on_the_truck_category()
    {
        Assert.Empty(GateRules.NotAsPaid("AUTO-1", false, null, "18_WHEEL", "HAU-SHT", "HAU-SHT"));
        // ...but another haulier may owe cash the first one had on credit.
        Assert.Single(GateRules.NotAsPaid("AUTO-1", false, null, "18_WHEEL", "HAU-SHT", "HAU-LCH"), f => f.Code == "HAULIER_NOT_AS_PAID");
    }

    [Fact]
    public void A_different_haulier_is_information_only()
    {
        var finding = Assert.Single(GateRules.NotAsPaid("RCT-1", true, "18_WHEEL", "18_WHEEL", "HAU-SHT", "HAU-LCH"));
        Assert.Equal(("HAULIER_NOT_AS_PAID", GateSeverity.Info), (finding.Code, finding.Severity));
        Assert.Contains("HAU-LCH", finding.Message);
        Assert.Contains("HAU-SHT", finding.Message);
    }

    [Fact]
    public void Neither_finding_changes_the_decision()
    {
        var findings = GateRules.NotAsPaid("RCT-1", true, "6_WHEEL", "18_WHEEL", "HAU-SHT", "HAU-LCH").ToList();
        Assert.Equal(2, findings.Count);
        Assert.Equal("ALLOWED", GateRules.Decide(findings));
    }
}
