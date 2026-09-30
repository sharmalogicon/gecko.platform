using Gecko.MasterData.Contracts;
using Gecko.Tos.Domain;

namespace Gecko.Tos.Tests.Domain;

/// <summary>TIER3 §1: shut is a warning, never a refusal; no gate hours is silence.</summary>
public sealed class GateHoursRuleTests
{
    private static readonly DateTimeOffset MondayEvening = new(2027, 3, 1, 20, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public void No_gate_hours_or_an_open_gate_says_nothing()
    {
        Assert.Null(GateRules.OutsideHours(null));
        Assert.Null(GateRules.OutsideHours(new GateHoursStatus("OPEN", null, MondayEvening, MondayEvening.AddHours(1), null)));
    }

    [Fact]
    public void A_shut_gate_is_a_warning_that_leaves_the_decision_alone()
    {
        var finding = GateRules.OutsideHours(new GateHoursStatus("OUTSIDE_HOURS", null, MondayEvening, null, MondayEvening.AddHours(12)))!;
        Assert.Equal(("OUTSIDE_GATE_HOURS", GateSeverity.Warn), (finding.Code, finding.Severity));
        Assert.Contains("Tue 2 Mar 08:00", finding.Message);
        Assert.Equal("ALLOWED", GateRules.Decide([finding]));

        var holiday = GateRules.OutsideHours(new GateHoursStatus("HOLIDAY", "Songkran", MondayEvening, null, null))!;
        Assert.Equal(("GATE_CLOSED_DAY", GateSeverity.Warn), (holiday.Code, holiday.Severity));
        Assert.Contains("Songkran", holiday.Message);
    }
}
