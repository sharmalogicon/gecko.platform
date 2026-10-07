namespace Gecko.Revenue.Domain;

/// <summary>One plug-in → plug-out of a reefer, as TOS reported it. <see cref="PluggedOutAt"/> null = still plugged in.</summary>
public sealed record PlugSpan(DateTimeOffset PluggedInAt, DateTimeOffset? PluggedOutAt, bool IsVoided = false);

/// <param name="BillableHours">Per STARTED hour over the whole visit: 60 min = 1, 61 min = 2, 0 = 0.</param>
/// <param name="MinutesPlugged">Whole minutes, rounded down (what the clerk reads, not what is billed).</param>
/// <param name="Sessions">Sessions that count (voided ones do not).</param>
public sealed record PluggedTime(int BillableHours, int MinutesPlugged, int Sessions, TimeSpan Plugged);

/// <summary>
/// Reefer power time for BILLING (owner's decision: per started hour plugged in).
///
/// Per CONTAINER VISIT, not per session: every non-voided session of the visit
/// is added up — an open one runs to the as-at time — and the total is rounded
/// up ONCE. Two sessions of 30 and 31 minutes are 61 minutes = 2 hours, not
/// 1 + 1. Overlapping sessions (only possible after a bad correction) count
/// the overlap once. Neither rule can ever bill more than rounding each session
/// on its own would.
///
/// Nothing is stored: a stored number would be a second truth that drifts the
/// first time TOS corrects a time (the same reason TOS's ReeferHours computes).
/// </summary>
public static class ReeferPower
{
    public static PluggedTime Measure(IEnumerable<PlugSpan> sessions, DateTimeOffset asAt)
    {
        var counted = sessions.Where(s => !s.IsVoided).ToList();

        var spans = counted
            .Select(s => (From: s.PluggedInAt, To: Min(s.PluggedOutAt ?? asAt, asAt)))
            .Where(s => s.To > s.From)
            .OrderBy(s => s.From)
            .ToList();

        var total = TimeSpan.Zero;
        DateTimeOffset? start = null, end = null;
        foreach (var (from, to) in spans)
        {
            if (end is { } e && from <= e)
            {
                if (to > e) end = to;
                continue;
            }
            if (start is { } s0 && end is { } e0) total += e0 - s0;
            (start, end) = (from, to);
        }
        if (start is { } s1 && end is { } e1) total += e1 - s1;

        return new PluggedTime(BillableHours(total), (int)Math.Floor(total.TotalMinutes), counted.Count, total);
    }

    /// <summary>
    /// Calendar days plugged in, for a rate tiered by DAY: the first plug-in's local
    /// date to the last plug-out's (an open session: the as-at time), both counted —
    /// as KORAKIT's old system counted its electricity slabs (stay days, end − start + 1).
    /// 0 when nothing counts.
    /// </summary>
    public static int PluggedDays(IEnumerable<PlugSpan> sessions, DateTimeOffset asAt, Func<DateTimeOffset, DateOnly> localDate)
    {
        var spans = sessions.Where(s => !s.IsVoided)
            .Select(s => (From: s.PluggedInAt, To: Min(s.PluggedOutAt ?? asAt, asAt)))
            .Where(s => s.To > s.From)
            .ToList();
        return spans.Count == 0
            ? 0
            : localDate(spans.Max(s => s.To)).DayNumber - localDate(spans.Min(s => s.From)).DayNumber + 1;
    }

    /// <summary>Per started hour, on exact ticks (no floating point): 60:00 = 1, 60:00.0000001 = 2, 0 = 0.</summary>
    public static int BillableHours(TimeSpan plugged) =>
        plugged <= TimeSpan.Zero ? 0 : (int)((plugged.Ticks + TimeSpan.TicksPerHour - 1) / TimeSpan.TicksPerHour);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
}
