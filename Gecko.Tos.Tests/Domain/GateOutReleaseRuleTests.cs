using Gecko.Tos.Domain;

namespace Gecko.Tos.Tests.Domain;

/// <summary>
/// Vector's gate-out release gates (GateOut.cs:1139-1171, gate-in-vector-parity
/// Part B §B2) as pure rules: the move order, and the laden release date.
/// </summary>
public sealed class GateOutReleaseRuleTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public void A_movement_that_releases_damaged_boxes_lets_the_damage_hold_out_and_nothing_else()
    {
        // The ordinary case: a hold whose scope covers the direction refuses.
        Assert.Equal(GateSeverity.Block, GateRules.HoldBlocks("DMG", "ALL", "Damaged", GateRules.Out, "SURVEY_DAMAGED")!.Severity);

        // The damage hold is the one a damage survey applies, whatever the tenant calls it.
        var released = GateRules.HoldBlocks("DMG", "ALL", "Damaged", GateRules.Out, "SURVEY_DAMAGED", movementReleasesDamaged: true)!;
        Assert.Equal(("HOLD_RELEASED_BY_MOVEMENT", GateSeverity.Info), (released.Code, released.Severity));
        Assert.Contains("DMG", released.Message);

        // Any other hold — an expired CSC plate is technical too — or not going out: the flag changes nothing.
        Assert.Equal("HOLD", GateRules.HoldBlocks("CSC_EXP", "GATE_OUT", "CSC expired", GateRules.Out, "CSC_EXPIRED", movementReleasesDamaged: true)!.Code);
        Assert.Equal("HOLD", GateRules.HoldBlocks("CUSTOMS", "ALL", "Customs", GateRules.Out, "CUSTOMS_SELECTED", movementReleasesDamaged: true)!.Code);
        Assert.Equal("HOLD", GateRules.HoldBlocks("LEGAL", "ALL", "Legal", GateRules.Out, null, movementReleasesDamaged: true)!.Code);
        Assert.Equal("HOLD", GateRules.HoldBlocks("DMG", "ALL", "Damaged", GateRules.In, "SURVEY_DAMAGED", movementReleasesDamaged: true)!.Code);
    }

    [Fact]
    public void A_box_designated_to_certain_ports_leaves_only_for_one_of_them()
    {
        string[] ports = ["SGSIN", "MYPKG"];
        Assert.Null(GateRules.FixedPort("MSKU1234565", ports, "BK-1", "sgsin"));
        Assert.Null(GateRules.FixedPort("MSKU1234565", [], "BK-1", "HKHKG"));      // no list: any port
        Assert.Null(GateRules.FixedPort("MSKU1234565", null, "BK-1", null));       // not in the registry

        var wrong = GateRules.FixedPort("MSKU1234565", ports, "BK-1", "HKHKG")!;
        Assert.Equal(("FIXED_PORT", GateSeverity.Block), (wrong.Code, wrong.Severity));
        Assert.Contains("SGSIN, MYPKG", wrong.Message);
        Assert.Contains("HKHKG", wrong.Message);
        // A booking that names no discharge port cannot satisfy a designated box.
        Assert.NotNull(GateRules.FixedPort("MSKU1234565", ports, "BK-1", null));
    }

    [Fact]
    public void A_gate_out_must_be_later_than_the_previous_move()
    {
        Assert.Null(GateRules.BeforePreviousMove("MSKU1234565", Noon.AddMinutes(1), Noon));

        // On the same instant, or before it: refused (Vector compares "< 1").
        foreach (var at in new[] { Noon, Noon.AddHours(-2) })
        {
            var finding = GateRules.BeforePreviousMove("MSKU1234565", at, Noon)!;
            Assert.Equal(("BEFORE_PREVIOUS_MOVE", GateSeverity.Block), (finding.Code, finding.Severity));
            Assert.Contains("MSKU1234565", finding.Message);
        }
    }

    [Fact]
    public void A_full_export_box_waits_for_the_laden_release_date()
    {
        var early = GateRules.BeforeLadenRelease("MSKU1234565", "EASTPIONEER-069S", Noon.AddHours(-1), Noon)!;
        Assert.Equal(("BEFORE_LADEN_RELEASE", GateSeverity.Block), (early.Code, early.Severity));
        Assert.Contains("EASTPIONEER-069S", early.Message);

        Assert.Null(GateRules.BeforeLadenRelease("MSKU1234565", "EASTPIONEER-069S", Noon, Noon));            // from the release instant on
        Assert.Null(GateRules.BeforeLadenRelease("MSKU1234565", "EASTPIONEER-069S", Noon.AddDays(1), Noon));
    }

    [Fact]
    public void A_call_with_no_laden_release_date_restricts_nothing()
    {
        Assert.Null(GateRules.BeforeLadenRelease("MSKU1234565", null, Noon, null));
    }
}
