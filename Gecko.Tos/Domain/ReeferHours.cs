namespace Gecko.Tos.Domain;

/// <summary>
/// Reefer power time (TIER3_DESIGN_NOTES §6). Hours are computed from the
/// session's plug-in and plug-out, never stored: a stored number would be a
/// second truth that drifts the first time a time is corrected.
/// TOS reports the hours; Revenue prices them.
/// </summary>
public static class ReeferHours
{
    /// <summary>Per STARTED hour: 60 min = 1, 61 min = 2, 0 s = 0. Negative (clock skew) counts as 0.</summary>
    public static int BillableHours(TimeSpan plugged) =>
        plugged <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(plugged.TotalSeconds / 3600d);

    /// <summary>Whole minutes plugged in, never negative.</summary>
    public static int Minutes(TimeSpan plugged) =>
        plugged <= TimeSpan.Zero ? 0 : (int)Math.Floor(plugged.TotalMinutes);
}
