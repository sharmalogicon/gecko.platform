using Gecko.MasterData.Contracts;
using Gecko.Tos.Domain;

namespace Gecko.Tos.Tests.Domain;

public sealed class BookingRulesTests
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    [Theory]
    [InlineData("NOT_STARTED", "2026-09-21", "EXPIRED")]    // the release ended yesterday, depot time
    [InlineData("IN_PROGRESS", "2026-09-21", "EXPIRED")]
    [InlineData("NOT_STARTED", "2026-09-22", "NOT_STARTED")] // valid through today
    [InlineData("COMPLETED", "2026-09-01", "COMPLETED")]     // finished is finished, whatever the date
    [InlineData("CANCELLED", "2026-09-01", "CANCELLED")]
    [InlineData("NOT_STARTED", null, "NOT_STARTED")]         // no validity = no expiry
    public void Expired_is_derived_from_the_branch_calendar(string view, string? validTo, string expected) =>
        Assert.Equal(expected, BookingRules.EffectiveProgress(view, validTo is null ? null : DateOnly.Parse(validTo), Today));

    [Fact]
    public void The_plan_is_the_order_types_steps_in_sequence()
    {
        var plan = new OrderTypePlanRef(Guid.NewGuid(), "EXP CY/CY", true, "EXPORT", "GENERAL", "EXPORT",
        [
            Step("FULL_OUT", 3), Step("MTY_OUT", 1), Step("FULL_IN", 2, requireVessel: true),
        ]);

        Assert.Equal(["MTY_OUT", "FULL_IN", "FULL_OUT"], BookingRules.PlanFor(plan).Select(s => s.MovementCode));
        Assert.True(plan.RequiresVesselCall);
    }

    [Fact]
    public void A_box_must_match_the_line_and_fit_on_it()
    {
        Assert.Null(BookingRules.CannotAssign(qty: 2, usedOnLine: 1, "20GP", "20GP"));
        Assert.Null(BookingRules.CannotAssign(qty: 2, usedOnLine: 1, "20GP", registryType: null));   // unknown box: type unknown too
        Assert.Contains("registry says", BookingRules.CannotAssign(2, 0, "20GP", "40HC"));
        Assert.Contains("line is full", BookingRules.CannotAssign(2, 2, "20GP", "20GP"));
    }

    [Fact]
    public void Quantity_cannot_drop_below_the_boxes_on_the_line()   // Q10
    {
        Assert.Null(BookingRules.CannotSetQty(3, 3));
        Assert.Contains("unassign", BookingRules.CannotSetQty(2, 3));
    }

    [Fact]
    public void A_booking_with_gate_history_is_closed_not_cancelled()   // Q7
    {
        Assert.Null(BookingRules.CannotCancel("OPEN", 0));
        Assert.Contains("Close the booking instead", BookingRules.CannotCancel("OPEN", 1));
        Assert.Contains("Only an OPEN booking", BookingRules.CannotCancel("CLOSED", 0));
    }

    /// <summary>
    /// Owner 2026-10-04 (BOOKING_ENTRY_GAP_ANALYSIS §8): Vector's four booking types. The customer
    /// is required on all four; the loading and destination ports on IMPORT and EXPORT when the
    /// order type requires a vessel schedule.
    /// </summary>
    [Theory]
    [InlineData("IMPORT")]
    [InlineData("EXPORT")]
    [InlineData("REPO")]
    [InlineData("INTERNAL")]
    public void Every_booking_type_needs_a_customer(string bookingType)
    {
        Assert.Equal(["customerCode"], BookingRules.MissingHeaderFields(bookingType, requiresVesselSchedule: false,
            hasCustomer: false, hasLoadingPort: false, hasDestinationPort: false).Select(m => m.Field));
        Assert.Empty(BookingRules.MissingHeaderFields(bookingType, false, hasCustomer: true, false, false));
    }

    [Theory]
    [InlineData("IMPORT", true, true)]
    [InlineData("EXPORT", true, true)]
    [InlineData("IMPORT", false, false)]     // no vessel schedule: ports optional
    [InlineData("EXPORT", false, false)]
    [InlineData("REPO", true, false)]        // never on a depot move, schedule or not
    [InlineData("INTERNAL", true, false)]
    public void Ports_are_required_on_a_scheduled_import_or_export(string bookingType, bool scheduled, bool portsRequired)
    {
        var missing = BookingRules.MissingHeaderFields(bookingType, scheduled, hasCustomer: true, hasLoadingPort: false, hasDestinationPort: false)
            .Select(m => m.Field).ToList();
        Assert.Equal(portsRequired ? ["polPortCode", "fpdPortCode"] : new List<string>(), missing);
        Assert.Empty(BookingRules.MissingHeaderFields(bookingType, scheduled, true, hasLoadingPort: true, hasDestinationPort: true));
    }

    private static OrderTypeStepRef Step(string code, short seq, bool requireVessel = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), code, seq, true, true, false, false, requireVessel, false, false,
            Direction: code.EndsWith("_IN", StringComparison.Ordinal) ? "IN" : "OUT",
            FullEmpty: code.StartsWith("FULL", StringComparison.Ordinal) ? "FULL" : "EMPTY",
            RequiresSurvey: false, ChangesYardPosition: true);
}
