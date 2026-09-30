using Gecko.Tos.Domain;

namespace Gecko.Tos.Tests.Domain;

/// <summary>Reefer power is billed per STARTED hour; the hours are computed, never stored.</summary>
public sealed class ReeferHoursTests
{
    [Theory]
    [InlineData(0, 0)]                  // plugged in and straight out: nothing drawn
    [InlineData(1, 1)]                  // one second is a started hour
    [InlineData(59 * 60 + 59, 1)]       // 59m59s
    [InlineData(60 * 60, 1)]            // exactly 60 min
    [InlineData(61 * 60, 2)]            // 61 min
    [InlineData(24 * 3600, 24)]
    [InlineData(-30, 0)]                // clock skew never bills negative
    public void Billable_hours_are_per_started_hour(int seconds, int hours) =>
        Assert.Equal(hours, ReeferHours.BillableHours(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Crossing_midnight_is_just_elapsed_time()
    {
        var zone = TimeSpan.FromHours(7);
        var plugIn = new DateTimeOffset(2026, 9, 30, 23, 30, 0, zone);
        var plugOut = new DateTimeOffset(2026, 10, 1, 1, 31, 0, zone);
        Assert.Equal(3, ReeferHours.BillableHours(plugOut - plugIn));   // 2h01m
        Assert.Equal(121, ReeferHours.Minutes(plugOut - plugIn));
    }

    [Fact]
    public void Minutes_are_whole_and_never_negative()
    {
        Assert.Equal(59, ReeferHours.Minutes(TimeSpan.FromSeconds(59 * 60 + 59)));
        Assert.Equal(0, ReeferHours.Minutes(TimeSpan.FromMinutes(-5)));
    }
}
