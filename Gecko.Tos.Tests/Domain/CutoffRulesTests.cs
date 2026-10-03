using Gecko.Tos.Domain;
using Cut = Gecko.Tos.Domain.CutoffRules.Cutoff;

namespace Gecko.Tos.Tests.Domain;

/// <summary>
/// The cut-off rules on their own. Each case is a defect class Vector's
/// schedule actually contains (PLAN §2 V-6), refused before it can be stored.
/// </summary>
public sealed class CutoffRulesTests
{
    private static readonly DateTimeOffset Etd = new(2026, 10, 1, 22, 0, 0, TimeSpan.FromHours(7));
    private static readonly IReadOnlySet<string> Lines = new HashSet<string> { "MAEU", "HLCU" };

    private static IReadOnlyList<(int Index, string Message)> Check(params Cut[] cutoffs) =>
        CutoffRules.Validate(cutoffs, Lines, Etd);

    [Fact]
    public void A_sane_set_passes()
    {
        var problems = Check(
            new Cut("PORT_DRY", null, null, Etd.AddHours(-28)),
            new Cut("YARD_DRY", null, null, Etd.AddHours(-52)),
            new Cut("YARD_DRY", null, Guid.NewGuid(), Etd.AddHours(-58)),     // a farther depot closes earlier
            new Cut("PORT_REEFER", "MAEU", null, Etd.AddHours(-20)),
            new Cut("VGM", null, null, Etd.AddHours(-30)));

        Assert.Empty(problems);
    }

    /// <summary>Vector "CFS Cut-off (Dry / Reefer)" (gecko_tos 21): a depot cut-off, like the yard's.</summary>
    [Fact]
    public void A_CFS_cutoff_is_a_depot_cutoff_that_may_differ_by_branch_and_must_beat_the_port()
    {
        Assert.Empty(Check(
            new Cut("PORT_REEFER", null, null, Etd.AddHours(-28)),
            new Cut("CFS_DRY", null, Guid.NewGuid(), Etd.AddHours(-60)),
            new Cut("CFS_REEFER", null, null, Etd.AddHours(-50))));

        var (index, message) = Assert.Single(Check(
            new Cut("PORT_REEFER", null, null, Etd.AddHours(-28)),
            new Cut("CFS_REEFER", null, null, Etd.AddHours(-20))));
        Assert.Equal(1, index);
        Assert.Contains("PORT_REEFER", message);
    }

    [Fact]
    public void Yard_after_port_is_refused()   // V-6: 141 schedules
    {
        var problems = Check(
            new Cut("PORT_DRY", null, null, Etd.AddHours(-28)),
            new Cut("YARD_DRY", null, null, Etd.AddHours(-20)));

        var (index, message) = Assert.Single(problems);
        Assert.Equal(1, index);
        Assert.Contains("boxes still have to reach the terminal", message);
    }

    [Fact]
    public void A_line_port_cutoff_binds_only_that_line_yard_cutoff()
    {
        // MAEU's port cut-off is earlier than the whole-call one. HLCU's yard
        // cut-off after MAEU's port cut-off is fine; MAEU's own is not.
        var problems = Check(
            new Cut("PORT_REEFER", null, null, Etd.AddHours(-10)),
            new Cut("PORT_REEFER", "MAEU", null, Etd.AddHours(-30)),
            new Cut("YARD_REEFER", "HLCU", null, Etd.AddHours(-20)),
            new Cut("YARD_REEFER", "MAEU", null, Etd.AddHours(-20)));

        var (index, _) = Assert.Single(problems);
        Assert.Equal(3, index);
    }

    [Fact]
    public void A_cutoff_after_the_ship_sails_is_refused()   // V-6: 495 schedules
    {
        var (index, message) = Assert.Single(Check(new Cut("PORT_DRY", null, null, Etd.AddHours(2))));
        Assert.Equal(0, index);
        Assert.Contains("already have sailed", message);
    }

    [Fact]
    public void Only_yard_cutoffs_may_be_branch_specific()
    {
        var (_, message) = Assert.Single(Check(new Cut("PORT_DRY", null, Guid.NewGuid(), Etd.AddHours(-30))));
        Assert.Contains("only YARD_*", message);
    }

    [Fact]
    public void A_line_cutoff_needs_the_line_on_the_call()
    {
        var (_, message) = Assert.Single(Check(new Cut("PORT_DRY", "COSU", null, Etd.AddHours(-30))));
        Assert.Contains("not on this call", message);
    }

    [Fact]
    public void Unknown_kinds_and_duplicates_are_refused()
    {
        var problems = Check(
            new Cut("CY_CLOSE", null, null, Etd.AddHours(-30)),
            new Cut("VGM", null, null, Etd.AddHours(-30)),
            new Cut("VGM", null, null, Etd.AddHours(-31)));

        Assert.Contains(problems, p => p.Index == 0 && p.Message.Contains("not a cut-off kind"));
        Assert.Contains(problems, p => p.Index == 2 && p.Message.Contains("twice"));
    }

    [Fact]
    public void A_missing_yard_cutoff_is_derived_from_the_whole_call_port_cutoff()   // PLAN Q3
    {
        var derived = CutoffRules.Derive(
        [
            new Cut("PORT_DRY", null, null, Etd.AddHours(-28)),
            new Cut("PORT_REEFER", null, null, Etd.AddHours(-20)),
            new Cut("YARD_REEFER", null, null, Etd.AddHours(-60)),             // a whole-call YARD_REEFER: nothing to derive
            new Cut("PORT_DG", null, null, Etd.AddHours(-40)),
            new Cut("YARD_DG", null, Guid.NewGuid(), Etd.AddHours(-70)),       // one branch only: the rest still need one
            new Cut("PORT_REEFER", "MAEU", null, Etd.AddHours(-25)),           // a line's port row derives nothing
        ], leadHours: 24);

        Assert.Equal(["YARD_DRY", "YARD_DG"], derived.Select(d => d.Kind));
        var dry = derived[0];
        Assert.Equal(Etd.AddHours(-52), dry.At);
        Assert.Null(dry.LineCode);
        Assert.Null(dry.BranchId);
        Assert.Equal(Etd.AddHours(-64), derived[1].At);
    }
}
