using Gecko.MasterData.Contracts;

namespace Gecko.Revenue.Application;

/// <summary>
/// "Today" for a tariff is the BRANCH's calendar day, not the server's and not
/// UTC (PLAN.md §5.3). This server runs at +05:30, storage is UTC, Laem Chabang
/// is +07:00 — a 23:30 gate-in on the 31st must not be priced from November.
///
/// The zone comes from gecko_master org.branch_profile.timezone. A schedule for
/// every branch (branch_id NULL), or a branch with no zone recorded, uses the
/// platform default, which is where every current tenant is.
/// </summary>
internal sealed class BranchCalendar(IMasterDataReferences masterData, TimeProvider clock)
{
    public const string DefaultTimeZone = "Asia/Bangkok";

    /// <summary>Resolves "today" for each branch in one round trip.</summary>
    public async Task<Func<Guid?, DateOnly>> TodayForAsync(IEnumerable<Guid?> branchIds, CancellationToken ct)
    {
        var ids = branchIds.OfType<Guid>().Distinct().ToList();
        var branches = ids.Count == 0
            ? new Dictionary<string, BranchRef>()
            : await masterData.BranchesAsync(ids, ct);

        var now = clock.GetUtcNow();
        var fallback = LocalDate(now, DefaultTimeZone);

        return branchId =>
            branchId is { } id && branches.TryGetValue(id.ToString(), out var branch) && branch.TimeZone is { Length: > 0 } zone
                ? LocalDate(now, zone)
                : fallback;
    }

    public async Task<DateOnly> TodayAsync(Guid? branchId, CancellationToken ct) =>
        (await TodayForAsync([branchId], ct))(branchId);

    /// <summary>The branch-local calendar date of an instant — what a tariff's dates are compared with.</summary>
    public async Task<DateOnly> LocalDateAsync(Guid? branchId, DateTimeOffset instant, CancellationToken ct)
    {
        var zone = DefaultTimeZone;
        if (branchId is { } id
            && (await masterData.BranchesAsync([id], ct)).TryGetValue(id.ToString(), out var branch)
            && branch.TimeZone is { Length: > 0 } recorded)
            zone = recorded;
        return LocalDate(instant, zone);
    }

    private static DateOnly LocalDate(DateTimeOffset utcNow, string zoneId) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, Zone(zoneId)).DateTime);

    private static TimeZoneInfo Zone(string? zoneId) =>
        zoneId is { Length: > 0 } && TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var found)
            ? found
            : TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZone);

    /// <summary>The branch's code (printed on receipts) and its clock. Null when MDM has no profile for it.</summary>
    public async Task<BranchClockInfo?> BranchAsync(Guid branchId, CancellationToken ct) =>
        (await masterData.BranchesAsync([branchId], ct)).TryGetValue(branchId.ToString(), out var branch)
            ? new BranchClockInfo(branch.BranchId, branch.BranchCode, Zone(branch.TimeZone))
            : null;

    public DateTimeOffset Now => clock.GetUtcNow();
}

/// <summary>A branch and its time zone: local "now", local dates, and the end of a local day.</summary>
internal sealed record BranchClockInfo(Guid BranchId, string BranchCode, TimeZoneInfo Zone)
{
    public DateTimeOffset Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);

    public DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(Local(instant).DateTime);

    /// <summary>The last instant of a local calendar day — when a coupon "paid until the 25th" stops working.</summary>
    public DateTimeOffset EndOfDay(DateOnly day)
    {
        var local = day.ToDateTime(new TimeOnly(23, 59, 59));
        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
