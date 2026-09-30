using Gecko.MasterData.Application;
using static Gecko.MasterData.Application.GateHoursCalendar;

namespace Gecko.MasterData.Tests;

/// <summary>Gate hours as pure rules: the weekly clock, overnight tails, holidays. 1 March 2027 is a Monday.</summary>
public sealed class GateHoursCalendarTests
{
    private static readonly TimeZoneInfo Bangkok = Zone("Asia/Bangkok");
    private static readonly Dictionary<DateOnly, DateException> NoDates = [];
    private static readonly Dictionary<DateOnly, HolidayDay> NoHolidays = [];
    private static DateTimeOffset Local(int day, int hour, int minute = 0) => new(2027, 3, day, hour, minute, 0, TimeSpan.FromHours(7));
    private static Window W(byte day, int open, int close) => new(day, new TimeOnly(open, 0), new TimeOnly(close, 0));

    [Fact]
    public void Windows_overlap_on_the_weekly_clock_and_touching_ends_do_not()
    {
        Assert.True(Overlaps(W(7, 22, 6), W(1, 5, 9)));      // Sunday night runs into Monday morning
        Assert.False(Overlaps(W(7, 22, 6), W(1, 6, 9)));
        Assert.False(Overlaps(W(1, 8, 12), W(1, 12, 17)));
        Assert.True(Overlaps(W(1, 8, 12), W(1, 11, 13)));
        Assert.False(Overlaps(W(1, 8, 12), W(2, 8, 12)));
        Assert.Equal(360, Minutes(new TimeOnly(18, 0), TimeOnly.MinValue));   // 18:00–00:00 ends at midnight
    }

    [Fact]
    public void No_windows_means_no_gate_hours()
    {
        Assert.Null(Resolve(Local(1, 10), Bangkok, [], NoDates, NoHolidays));
    }

    [Fact]
    public void An_overnight_window_counts_after_midnight_even_on_a_holiday()
    {
        var windows = new[] { W(1, 22, 6) };
        var holidays = new Dictionary<DateOnly, HolidayDay> { [new DateOnly(2027, 3, 2)] = new(new DateOnly(2027, 3, 2), "Tuesday off", false) };
        var tail = Resolve(Local(2, 5, 59), Bangkok, windows, NoDates, holidays)!;
        Assert.True(tail.IsOpen);
        Assert.Equal(Local(2, 6), tail.OpenUntil);

        var after = Resolve(Local(2, 6), Bangkok, windows, NoDates, holidays)!;
        Assert.Equal(("HOLIDAY", "Tuesday off", Local(8, 22)), (after.State, after.Note, after.NextOpensAt));

        // A holiday on the day it opens removes the night.
        var monday = new Dictionary<DateOnly, HolidayDay> { [new DateOnly(2027, 3, 1)] = new(new DateOnly(2027, 3, 1), "Monday off", false) };
        Assert.False(Resolve(Local(2, 1), Bangkok, windows, NoDates, monday)!.IsOpen);
    }

    [Fact]
    public void A_half_day_holiday_keeps_the_hours_up_to_noon()
    {
        var windows = new[] { W(1, 8, 17) };
        var half = new Dictionary<DateOnly, HolidayDay> { [new DateOnly(2027, 3, 1)] = new(new DateOnly(2027, 3, 1), "Half", true) };
        var morning = Resolve(Local(1, 11), Bangkok, windows, NoDates, half)!;
        Assert.Equal((true, Local(1, 12)), (morning.IsOpen, morning.OpenUntil));
        Assert.Equal("HOLIDAY", Resolve(Local(1, 12), Bangkok, windows, NoDates, half)!.State);
        Assert.Equal("OUTSIDE_HOURS", Resolve(Local(1, 7), Bangkok, windows, NoDates, half)!.State);
    }

    [Fact]
    public void A_one_off_date_outranks_the_weekday_and_the_holiday()
    {
        var windows = new[] { W(1, 8, 17) };
        var date = new DateOnly(2027, 3, 1);
        var holidays = new Dictionary<DateOnly, HolidayDay> { [date] = new(date, "Off", false) };
        var open = new Dictionary<DateOnly, DateException> { [date] = new(date, false, new TimeOnly(9, 0), new TimeOnly(10, 0), "Shift") };
        Assert.True(Resolve(Local(1, 9, 30), Bangkok, windows, open, holidays)!.IsOpen);
        Assert.False(Resolve(Local(1, 10, 30), Bangkok, windows, open, holidays)!.IsOpen);

        var shut = new Dictionary<DateOnly, DateException> { [date] = new(date, true, null, null, "Stocktake") };
        var status = Resolve(Local(1, 9), Bangkok, windows, shut, NoHolidays)!;
        Assert.Equal(("CLOSED_DATE", "Stocktake", Local(8, 8)), (status.State, status.Note, status.NextOpensAt));
    }
}
