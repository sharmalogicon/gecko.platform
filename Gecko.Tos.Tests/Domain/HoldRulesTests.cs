using Gecko.Tos.Domain;

namespace Gecko.Tos.Tests.Domain;

/// <summary>
/// The authority map (PLAN §10.7), tested without a token or a database. Every
/// value MDM allows in <c>equipment.hold.release_authority</c> has to land
/// somewhere deliberate — a hold nobody can lift is a box nobody can move.
/// </summary>
public class HoldRulesTests
{
    /// <summary>The AllowedValues list on MDM's SaveHoldRequest.</summary>
    private static readonly string[] EveryAuthority =
        ["SUPERVISOR", "MNR", "DEPOT_OPERATIONS", "DEPOT_FINANCE", "LINE", "CUSTOMS"];

    [Fact]
    public void Every_release_authority_maps_to_one_of_the_three_seeded_permissions()
    {
        string[] seeded = [HoldRules.ReleaseOperations, HoldRules.ReleaseFinance, HoldRules.ReleaseLine];

        foreach (var authority in EveryAuthority)
            Assert.Contains(HoldRules.ReleaseRule(authority).Permission, seeded);
    }

    [Theory]
    [InlineData("DEPOT_FINANCE", "tos.hold.release.finance")]
    [InlineData("LINE", "tos.hold.release.line")]
    [InlineData("SUPERVISOR", "tos.hold.release.operations")]
    [InlineData("DEPOT_OPERATIONS", "tos.hold.release.operations")]
    public void The_depots_own_holds_need_no_paperwork_beyond_a_reason(string authority, string permission)
    {
        var (mapped, referenceRequired) = HoldRules.ReleaseRule(authority);

        Assert.Equal(permission, mapped);
        Assert.False(referenceRequired);
        Assert.Null(HoldRules.ReferenceReason(authority));
    }

    /// <summary>
    /// CUSTOMS and MNR are somebody else's decision. Operations may RECORD the
    /// release, but only by quoting the document it is acting on — so the row names
    /// the paper instead of shrugging.
    /// </summary>
    [Theory]
    [InlineData("CUSTOMS")]
    [InlineData("MNR")]
    public void A_hold_the_depot_does_not_own_is_released_only_against_a_reference(string authority)
    {
        var (permission, referenceRequired) = HoldRules.ReleaseRule(authority);

        Assert.Equal(HoldRules.ReleaseOperations, permission);
        Assert.True(referenceRequired);
        Assert.False(string.IsNullOrWhiteSpace(HoldRules.ReferenceReason(authority)));
    }

    /// <summary>An unknown authority must not fall open: the strictest depot permission wins.</summary>
    [Fact]
    public void An_authority_master_data_invents_later_still_needs_a_permission()
    {
        var (permission, _) = HoldRules.ReleaseRule("PORT_AUTHORITY");

        Assert.Equal(HoldRules.ReleaseOperations, permission);
    }

    [Theory]
    [InlineData("ALL", "IN", true)]
    [InlineData("ALL", "OUT", true)]
    [InlineData("GATE_IN", "IN", true)]
    [InlineData("GATE_IN", "OUT", false)]
    [InlineData("GATE_OUT", "OUT", true)]
    [InlineData("GATE_OUT", "IN", false)]
    [InlineData("RELEASE", "OUT", true)]
    [InlineData("RELEASE", "IN", false)]
    [InlineData("LOAD", "OUT", true)]
    public void A_holds_scope_decides_which_move_it_stops(string scope, string direction, bool blocks)
    {
        Assert.Equal(blocks, HoldRules.Blocks(scope, direction));
        Assert.Contains(scope, HoldRules.BlockingScopes);
    }
}
