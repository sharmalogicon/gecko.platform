using Gecko.MasterData.Contracts;

namespace Gecko.MasterData.Application;

/// <summary>
/// Gate hours as pure functions (TIER3_DESIGN_NOTES §1): given a depot's weekly
/// windows, its one-off dates and its public holidays, is the gate open at an
/// instant, and when does it open next?
///
/// Rules, all in the depot's local time:
/// <list type="bullet">
/// <item>A window belongs to the day it OPENS on. <c>closes &lt;= opens</c> runs
/// past midnight (22:00–06:00; 18:00–00:00 ends at midnight).</item>
/// <item>Precedence: a one-off date &gt; a public holiday &gt; the weekday.</item>
/// <item>A holiday closes the gate for the windows that open on it. A half-day
/// holiday keeps them up to <see cref="HalfDayEnds"/> (12:00) and drops the rest.</item>
/// <item>The overnight tail of yesterday's window still counts today, even on a
/// holiday: it opened before the holiday began.</item>
/// </list>
/// </summary>
public static class GateHoursCalendar
{
    public const string DefaultTimeZone = "Asia/Bangkok";
    public static readonly TimeOnly HalfDayEnds = new(12, 0);
    public const int LookAheadDays = 14;

    public const string Open = "OPEN";
    public const string OutsideHours = "OUTSIDE_HOURS";
    public const string Holiday = "HOLIDAY";
    public const string ClosedDate = "CLOSED_DATE";

    public sealed record Window(byte IsoWeekday, TimeOnly OpensAt, TimeOnly ClosesAt);
    public sealed record DateException(DateOnly Date, bool IsClosed, TimeOnly? OpensAt, TimeOnly? ClosesAt, string Reason);
    public sealed record HolidayDay(DateOnly Date, string Name, bool IsHalfDay);

    /// <summary>1 = Monday … 7 = Sunday.</summary>
    public static byte IsoWeekday(DateOnly date) => (byte)(date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek);

    /// <summary>How long a window stays open, in minutes (overnight when it closes at or before it opens).</summary>
    public static int Minutes(TimeOnly opensAt, TimeOnly closesAt)
    {
        var open = opensAt.Hour * 60 + opensAt.Minute;
        var close = closesAt.Hour * 60 + closesAt.Minute;
        return close > open ? close - open : 1440 - open + close;
    }

    /// <summary>
    /// Two windows overlap on the weekly clock (Monday 22:00–06:00 overlaps Tuesday
    /// 05:00–12:00). Touching ends (12:00 / 12:00) do not overlap.
    /// </summary>
    public static bool Overlaps(Window a, Window b)
    {
        const int week = 7 * 1440;
        static (int Start, int End) Span(Window w)
        {
            var start = (w.IsoWeekday - 1) * 1440 + w.OpensAt.Hour * 60 + w.OpensAt.Minute;
            return (start, start + Minutes(w.OpensAt, w.ClosesAt));
        }

        var (s1, e1) = Span(a);
        var (s2, e2) = Span(b);
        // Compare b shifted a week either way, so Sunday night meets Monday morning.
        foreach (var shift in new[] { -week, 0, week })
            if (s1 < e2 + shift && s2 + shift < e1) return true;
        return false;
    }

    /// <summary>The windows that open on a local date, as local [start, end) intervals, and why the day is closed if it is.</summary>
    public static (List<(DateTime Start, DateTime End)> Windows, string? ClosedState, string? Note) Day(
        DateOnly date,
        IReadOnlyCollection<Window> windows,
        IReadOnlyDictionary<DateOnly, DateException> exceptions,
        IReadOnlyDictionary<DateOnly, HolidayDay> holidays)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue);
        (DateTime, DateTime) Interval(TimeOnly o, TimeOnly c) =>
            (midnight.Add(o.ToTimeSpan()), midnight.Add(o.ToTimeSpan()).AddMinutes(Minutes(o, c)));

        if (exceptions.TryGetValue(date, out var exception))
            return exception is { IsClosed: false, OpensAt: { } eo, ClosesAt: { } ec }
                ? ([Interval(eo, ec)], null, exception.Reason)
                : ([], ClosedDate, exception.Reason);

        var weekday = IsoWeekday(date);
        var day = windows.Where(w => w.IsoWeekday == weekday).OrderBy(w => w.OpensAt)
            .Select(w => Interval(w.OpensAt, w.ClosesAt)).ToList();

        if (holidays.TryGetValue(date, out var holiday))
        {
            if (!holiday.IsHalfDay) return ([], Holiday, holiday.Name);
            var noon = midnight.Add(HalfDayEnds.ToTimeSpan());
            var kept = day.Where(w => w.Item1 < noon).Select(w => (w.Item1, w.Item2 < noon ? w.Item2 : noon)).ToList();
            return (kept, null, holiday.Name);
        }

        return (day, null, null);
    }

    /// <summary>The gate at <paramref name="at"/>. Null when the depot has no weekly windows at all.</summary>
    public static GateHoursStatus? Resolve(
        DateTimeOffset at, TimeZoneInfo zone,
        IReadOnlyCollection<Window> windows,
        IReadOnlyDictionary<DateOnly, DateException> exceptions,
        IReadOnlyDictionary<DateOnly, HolidayDay> holidays)
    {
        if (windows.Count == 0) return null;

        var localAt = TimeZoneInfo.ConvertTime(at, zone);
        var local = localAt.DateTime;
        var today = DateOnly.FromDateTime(local);
        DateTimeOffset Stamp(DateTime t) => new(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), zone.GetUtcOffset(t));

        var yesterday = Day(today.AddDays(-1), windows, exceptions, holidays);
        var current = Day(today, windows, exceptions, holidays);
        foreach (var (start, end) in yesterday.Windows.Concat(current.Windows))
            if (start <= local && local < end)
                return new GateHoursStatus(Open, current.Note, localAt, Stamp(end), null);

        DateTimeOffset? next = null;
        for (var d = 0; d <= LookAheadDays && next is null; d++)
        {
            var date = today.AddDays(d);
            var (dayWindows, _, _) = d == 0 ? current : Day(date, windows, exceptions, holidays);
            var first = dayWindows.Where(w => w.Start > local).Select(w => (DateTime?)w.Start).Min();
            if (first is { } opens) next = Stamp(opens);
        }

        // Why it is shut: the day's own closure, or the afternoon of a half-day holiday.
        var state = current.ClosedState
            ?? (holidays.TryGetValue(today, out var h) && h.IsHalfDay && !exceptions.ContainsKey(today)
                && TimeOnly.FromDateTime(local) >= HalfDayEnds ? Holiday : OutsideHours);
        var note = state == OutsideHours ? null : current.Note;
        return new GateHoursStatus(state, note, localAt, null, next);
    }

    public static TimeZoneInfo Zone(string? zoneId) =>
        zoneId is { Length: > 0 } && TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone)
            ? zone
            : TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZone);
}
