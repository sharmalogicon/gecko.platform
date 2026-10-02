using Gecko.Tos.Domain;

namespace Gecko.Tos.Tests.Domain;

/// <summary>
/// Vector's P/U Mode / D/O Mode on a booked box (BookingEntry.cs:436-441, 626-655):
/// which list a booking offers, and the two modes that relax the booking checks.
/// </summary>
public sealed class BookingHandoverModeRuleTests
{
    [Fact]
    public void The_booking_direction_selects_the_list()
    {
        Assert.Null(BookingRules.HandoverModeProblem("IMPORT", "DO_CUS"));
        Assert.Null(BookingRules.HandoverModeProblem("EXPORT", "PU_PORT"));
        Assert.Null(BookingRules.HandoverModeProblem("DOMESTIC", "REPO_OWN"));
        Assert.Null(BookingRules.HandoverModeProblem(null, "REPO_OTHER"));

        Assert.Contains("DO_OWN", BookingRules.HandoverModeProblem("IMPORT", "PU_OWN"));
        Assert.Contains("PU_OWN", BookingRules.HandoverModeProblem("EXPORT", "DO_CUS"));
        Assert.NotNull(BookingRules.HandoverModeProblem("DOMESTIC", "PU_OWN"));
        Assert.NotNull(BookingRules.HandoverModeProblem("EXPORT", "WHATEVER"));
    }

    [Fact]
    public void An_export_box_is_an_empty_one_from_this_yard_unless_it_is_picked_up_elsewhere()
    {
        Assert.Null(BookingRules.HandoverRefusal("EXPORT", "PU_OWN", inYardHere: true, "EMPTY"));
        Assert.Contains("not in this yard", BookingRules.HandoverRefusal("EXPORT", "PU_OWN", inYardHere: false, null));
        Assert.Contains("not EMPTY", BookingRules.HandoverRefusal("EXPORT", "PU_ONLY", inYardHere: true, "FULL"));

        Assert.Null(BookingRules.HandoverRefusal("EXPORT", "PU_OTHER", inYardHere: false, null));
        Assert.Null(BookingRules.HandoverRefusal("EXPORT", "PU_OTHER", inYardHere: true, "FULL"));
    }

    [Fact]
    public void An_import_box_is_not_already_here_unless_the_customer_drops_it_off()
    {
        Assert.Null(BookingRules.HandoverRefusal("IMPORT", "DO_OWN", inYardHere: false, null));
        Assert.Contains("already in this yard", BookingRules.HandoverRefusal("IMPORT", "DO_OWN", inYardHere: true, "FULL"));
        Assert.Null(BookingRules.HandoverRefusal("IMPORT", "DO_CUS", inYardHere: true, "FULL"));
    }

    [Fact]
    public void A_repo_booking_has_no_such_check()
    {
        Assert.Null(BookingRules.HandoverRefusal("DOMESTIC", "REPO_OWN", inYardHere: true, "FULL"));
        Assert.Null(BookingRules.HandoverRefusal("DOMESTIC", "REPO_OTHER", inYardHere: false, null));
    }
}
