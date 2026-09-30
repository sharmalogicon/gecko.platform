using Gecko.Revenue.Domain;

namespace Gecko.Revenue.Tests.Domain;

/// <summary>The owner's rule — reefer power per STARTED hour plugged in — over a whole container visit.</summary>
public sealed class ReeferPowerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 8, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset Later = T0.AddDays(3);

    private static PlugSpan Closed(int fromMinute, int minutes, bool voided = false) =>
        new(T0.AddMinutes(fromMinute), T0.AddMinutes(fromMinute + minutes), voided);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(59, 1)]
    [InlineData(60, 1)]
    [InlineData(61, 2)]
    [InlineData(120, 2)]
    [InlineData(121, 3)]
    public void Every_started_hour_is_billed(int minutes, int hours)
    {
        var time = ReeferPower.Measure([Closed(0, minutes)], Later);
        Assert.Equal((hours, minutes), (time.BillableHours, time.MinutesPlugged));
    }

    [Fact]
    public void One_second_past_the_hour_starts_the_next_one() =>
        Assert.Equal(2, ReeferPower.BillableHours(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1)));

    [Fact]
    public void Two_sessions_are_rounded_once_not_each()
    {
        // 30 + 31 = 61 minutes → 2 hours (per session it would also be 1 + 1 = 2; 20 + 20 shows the difference).
        Assert.Equal(2, ReeferPower.Measure([Closed(0, 30), Closed(90, 31)], Later).BillableHours);
        Assert.Equal(1, ReeferPower.Measure([Closed(0, 20), Closed(90, 20)], Later).BillableHours);
        Assert.Equal(2, ReeferPower.Measure([Closed(0, 30), Closed(90, 31)], Later).Sessions);
    }

    [Fact]
    public void A_session_across_midnight_counts_its_whole_length()
    {
        var bangkok = TimeSpan.FromHours(7);
        var time = ReeferPower.Measure(
            [new PlugSpan(new DateTimeOffset(2026, 9, 29, 23, 30, 0, bangkok), new DateTimeOffset(2026, 9, 30, 1, 31, 0, bangkok))], Later);
        Assert.Equal((3, 121), (time.BillableHours, time.MinutesPlugged));   // 2 h 01 min
    }

    [Fact]
    public void A_voided_session_is_never_billed()
    {
        var time = ReeferPower.Measure([Closed(0, 30), Closed(60, 600, voided: true)], Later);
        Assert.Equal((1, 30, 1), (time.BillableHours, time.MinutesPlugged, time.Sessions));
        Assert.Equal((0, 0), (ReeferPower.Measure([Closed(0, 600, voided: true)], Later).BillableHours, ReeferPower.Measure([Closed(0, 600, voided: true)], Later).Sessions));
    }

    [Fact]
    public void An_open_session_runs_to_the_as_at_time()
    {
        PlugSpan open = new(T0, null);
        Assert.Equal(2, ReeferPower.Measure([open], T0.AddMinutes(61)).BillableHours);
        Assert.Equal(1, ReeferPower.Measure([open], T0.AddMinutes(60)).BillableHours);
        Assert.Equal(0, ReeferPower.Measure([open], T0).BillableHours);
        // Plugged in "after" the as-at time (clock skew) is nothing, not negative.
        Assert.Equal(0, ReeferPower.Measure([open], T0.AddMinutes(-5)).BillableHours);
    }

    [Fact]
    public void Overlapping_sessions_count_the_overlap_once()
    {
        // 0–60 and 30–90 → 90 minutes, not 120.
        var time = ReeferPower.Measure([Closed(0, 60), Closed(30, 60)], Later);
        Assert.Equal((2, 90), (time.BillableHours, time.MinutesPlugged));
    }

    [Fact]
    public void No_sessions_is_zero() =>
        Assert.Equal(new PluggedTime(0, 0, 0, TimeSpan.Zero), ReeferPower.Measure([], Later));
}
