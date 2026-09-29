using Gecko.MasterData.Contracts;

namespace Gecko.Tos.Application;

/// <summary>
/// "Now" and "today" as the DEPOT sees them. A release valid to 30 September
/// expires at midnight in Laem Chabang, not in UTC and not on this server's
/// +05:30 clock; a booking number's YYMM is the depot's month (PLAN carried
/// item 3). Same rule as Revenue's BranchCalendar.
///
/// The zone comes from gecko_master org.branch_profile.timezone; a branch with
/// none recorded uses the platform default, where every current tenant is.
/// </summary>
internal sealed class BranchClock(IMasterDataReferences masterData, TimeProvider clock)
{
    public const string DefaultTimeZone = "Asia/Bangkok";

    public sealed record Branch(Guid BranchId, string BranchCode, TimeZoneInfo Zone);

    public async Task<IReadOnlyDictionary<Guid, Branch>> BranchesAsync(IEnumerable<Guid> branchIds, CancellationToken ct)
    {
        var ids = branchIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, Branch>();
        var found = await masterData.BranchesAsync(ids, ct);
        return found.Values.ToDictionary(b => b.BranchId, b => new Branch(b.BranchId, b.BranchCode, ZoneOf(b.TimeZone)));
    }

    public DateTimeOffset LocalNow(Branch branch) => TimeZoneInfo.ConvertTime(clock.GetUtcNow(), branch.Zone);

    public DateOnly Today(Branch branch) => DateOnly.FromDateTime(LocalNow(branch).DateTime);

    /// <summary>The instant the depot's <paramref name="day"/> begins (its local midnight).</summary>
    public static DateTimeOffset StartOf(Branch branch, DateOnly day)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, branch.Zone.GetUtcOffset(midnight));
    }

    /// <summary>
    /// The zone as SQL Server's AT TIME ZONE names it (sys.time_zone_info holds
    /// Windows ids: "SE Asia Standard Time", not "Asia/Bangkok"), so a query can
    /// bucket by the depot's day and hour itself.
    /// </summary>
    public static string SqlZoneName(Branch branch) =>
        branch.Zone.HasIanaId && TimeZoneInfo.TryConvertIanaIdToWindowsId(branch.Zone.Id, out var windowsId)
            ? windowsId
            : branch.Zone.Id;

    /// <summary>Today per branch, for a page of bookings; unknown branches fall back to the default zone.</summary>
    public async Task<Func<Guid, DateOnly>> TodayForAsync(IEnumerable<Guid> branchIds, CancellationToken ct)
    {
        var branches = await BranchesAsync(branchIds, ct);
        var fallback = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), ZoneOf(null)).DateTime);
        return id => branches.TryGetValue(id, out var b) ? Today(b) : fallback;
    }

    private static TimeZoneInfo ZoneOf(string? zoneId) =>
        zoneId is { Length: > 0 } && TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone)
            ? zone
            : TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZone);
}
