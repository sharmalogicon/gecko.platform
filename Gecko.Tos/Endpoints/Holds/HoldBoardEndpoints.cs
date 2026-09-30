using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.SharedKernel;
using Gecko.Tos.Application;
using Gecko.Tos.Domain;
using Gecko.Tos.Infrastructure.Persistence;
using Gecko.Tos.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Endpoints.Holds;

/// <summary>
/// The holds board (TIER3 §7, "what can be done now for free"): a read-only view
/// over the hold rows that already exist — no new table, no new vocabulary.
///
/// It differs from <c>GET /holds</c> in one respect that a depot cares about: a
/// hold has a DEPOT. A hold on a booking belongs to the booking's depot; a hold on
/// a box belongs to the depot the box is standing in right now. A hold on a box
/// that is in no yard (pre-advised, or gone) has no depot and is shown to everyone
/// who may view holds, because it will surface at whichever gate the box reaches.
/// A branch-scoped caller therefore sees their own depots' holds plus those.
/// </summary>
internal static class HoldBoardEndpoints
{
    private static readonly string[] Sources = ["MANUAL", "EDI", "AUTO", "MIGRATED"];

    public static RouteGroupBuilder MapHoldBoardEndpoints(this RouteGroupBuilder holds)
    {
        holds.MapGet("/board", BoardAsync).RequireBranchPermission(TosPermissions.HoldView)
            .WithSummary("Holds board rows: depot, age and whether you may release each one");
        holds.MapGet("/summary", SummaryAsync).RequireBranchPermission(TosPermissions.HoldView)
            .WithSummary("Active holds counted by hold, type, scope, source, depot and age, plus releases per day");
        return holds;
    }

    // ── rows ────────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<PagedResult<HoldBoardRow>>, ValidationProblem, ProblemHttpResult>> BoardAsync(
        [AsParameters] ListQuery query, TosDbContext db, IMasterDataReferences master, BranchClock clock,
        ICallerPermissions scope, TimeProvider time, CancellationToken ct,
        string? status = null, Guid? branchId = null, string? holdCode = null, string? holdType = null,
        string? blockingScope = null, string? source = null)
    {
        var errors = new Dictionary<string, List<string>>();
        var state = status.Clean() ?? "ACTIVE";
        if (state is not ("ACTIVE" or "RELEASED")) errors.Add("status", "Use ACTIVE or RELEASED.");
        var from = source.Clean();
        if (from is not null && !Sources.Contains(from)) errors.Add("source", $"Use one of {string.Join(", ", Sources)}.");
        if (errors.Count > 0) return TosSupport.Invalid(errors);

        if (branchId is { } asked && !scope.HasAt(TosPermissions.HoldView, asked))
            return TosScope.OutsideYourBranches("That depot is not one you cover.");

        var rows = Visible(db, scope, TosPermissions.HoldView);
        rows = state == "ACTIVE" ? rows.Where(r => r.Hold.ReleasedAt == null) : rows.Where(r => r.Hold.ReleasedAt != null);
        if (branchId is not null) rows = rows.Where(r => r.DepotId == branchId);
        if (holdCode.Clean() is { } code) rows = rows.Where(r => r.Hold.HoldCode == code);
        if (from is not null) rows = rows.Where(r => r.Hold.Source == from);

        if (holdType.Clean() is not null || blockingScope.Clean() is not null)
        {
            var type = holdType.Clean();
            var blocks = blockingScope.Clean();
            var codes = await rows.Select(r => r.Hold.HoldCode).Distinct().ToListAsync(ct);
            var known = await master.HoldsAsync(codes, ct);
            var matching = known.Values
                .Where(d => (type is null || d.HoldType == type) && (blocks is null || d.BlockingScope == blocks))
                .Select(d => d.HoldCode).ToList();
            rows = rows.Where(r => matching.Contains(r.Hold.HoldCode));
        }

        if (query.Search.Clean() is { } q)
            rows = rows.Where(r => r.Hold.ContainerNo!.Contains(q) || r.Hold.HoldCode.Contains(q)
                                   || r.Hold.ExternalRef!.Contains(q) || r.Hold.ReleaseRef!.Contains(q) || r.OrderNo!.Contains(q));

        // Active: oldest first — the board is about what has been stuck longest.
        // Released: most recent first — the history is about what just moved.
        var ordered = state == "ACTIVE"
            ? rows.OrderBy(r => r.Hold.AppliedAt)
            : rows.OrderByDescending(r => r.Hold.ReleasedAt);
        var page = await ordered.ToPagedAsync(query.Page, query.PageSize, ct);

        var definitions = await master.HoldsAsync(page.Items.Select(r => r.Hold.HoldCode), ct);
        var branches = await clock.BranchesAsync(
            page.Items.SelectMany(r => new[] { r.BookingBranchId, r.DepotId }).OfType<Guid>(), ct);
        var now = time.GetUtcNow();

        return TypedResults.Ok(new PagedResult<HoldBoardRow>(
            page.Items.Select(r =>
            {
                var d = definitions.GetValueOrDefault(r.Hold.HoldCode);
                var hold = HoldEndpoints.Project(r.Hold, d, r.OrderNo, r.BookingBranchId,
                    r.BookingBranchId is { } bb ? branches.GetValueOrDefault(bb)?.BranchCode : null);

                // Exactly the release endpoint's rule, so the button never promises a 403.
                string? permission = null;
                var referenceRequired = false;
                var canRelease = false;
                if (r.Hold.ReleasedAt is null && d is not null)
                {
                    (permission, referenceRequired) = HoldRules.ReleaseRule(d.ReleaseAuthority);
                    canRelease = r.BookingBranchId is { } b ? scope.HasAt(permission, b) : scope.HasAnywhere(permission);
                }

                var until = r.Hold.ReleasedAt ?? now;
                return new HoldBoardRow(
                    hold, r.DepotId, r.DepotId is { } depot ? branches.GetValueOrDefault(depot)?.BranchCode : null,
                    r.Hold.ContainerNo is null ? "BOOKING" : "CONTAINER", r.BoxesOnBooking,
                    Math.Max(0, (int)(until - r.Hold.AppliedAt).TotalDays),
                    canRelease, referenceRequired, permission);
            }).ToList(),
            page.Page, page.PageSize, page.TotalCount));
    }

    // ── counts ──────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<HoldSummaryResponse>, ValidationProblem, ProblemHttpResult>> SummaryAsync(
        TosDbContext db, IMasterDataReferences master, BranchClock clock, ICallerPermissions scope, TimeProvider time,
        CancellationToken ct, Guid? branchId = null, int? days = null)
    {
        var window = days ?? 14;
        if (window is < 1 or > 90) return TosSupport.Invalid("days", "Between 1 and 90 days of release history.");
        if (branchId is { } asked && !scope.HasAt(TosPermissions.HoldView, asked))
            return TosScope.OutsideYourBranches("That depot is not one you cover.");

        var rows = Visible(db, scope, TosPermissions.HoldView);
        if (branchId is not null) rows = rows.Where(r => r.DepotId == branchId);

        var active = await rows.Where(r => r.Hold.ReleasedAt == null)
            .Select(r => new { r.Hold.HoldCode, r.Hold.AppliedAt, r.Hold.Source, r.DepotId }).ToListAsync(ct);

        var now = time.GetUtcNow();
        var since = now.AddDays(-(window + 1));   // a day of slack; the local-day cut is made below
        var released = await rows.Where(r => r.Hold.ReleasedAt != null && r.Hold.ReleasedAt >= since)
            .Select(r => new { ReleasedAt = r.Hold.ReleasedAt!.Value, r.DepotId }).ToListAsync(ct);

        var definitions = await master.HoldsAsync(active.Select(a => a.HoldCode), ct);
        var branches = await clock.BranchesAsync(
            active.Select(a => a.DepotId).Concat(released.Select(r => r.DepotId)).Concat([branchId]).OfType<Guid>(), ct);

        // Released per day, each release on its own depot's calendar; the window
        // ends today as the asked depot (or the platform default) sees it.
        var fallback = TimeZoneInfo.FindSystemTimeZoneById(BranchClock.DefaultTimeZone);
        TimeZoneInfo ZoneOf(Guid? id) => id is { } b && branches.TryGetValue(b, out var br) ? br.Zone : fallback;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, ZoneOf(branchId)).DateTime);
        var perDay = released
            .GroupBy(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.ReleasedAt, ZoneOf(r.DepotId)).DateTime))
            .ToDictionary(g => g.Key, g => g.Count());
        var trend = Enumerable.Range(0, window).Select(i => today.AddDays(i - window + 1))
            .Select(day => new HoldDayCount(day, perDay.GetValueOrDefault(day))).ToList();

        var withDefinition = active.Select(a => new { a, d = definitions.GetValueOrDefault(a.HoldCode) }).ToList();

        var byHold = withDefinition.GroupBy(x => x.a.HoldCode)
            .Select(g =>
            {
                var d = g.First().d;
                return new HoldCodeCount(g.Key, d?.DescriptionEn, d?.HoldType, d?.BlockingScope, d?.ReleaseAuthority,
                    d?.DisplayColorHex, d?.IsActive ?? false, g.Count());
            })
            .OrderByDescending(c => c.Count).ThenBy(c => c.HoldCode).ToList();

        static List<HoldCount> Count(IEnumerable<string?> keys) => keys
            .GroupBy(k => k ?? "UNKNOWN").Select(g => new HoldCount(g.Key, g.Count()))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Key).ToList();

        var byDepot = active.GroupBy(a => a.DepotId)
            .Select(g => new HoldDepotCount(g.Key, g.Key is { } id ? branches.GetValueOrDefault(id)?.BranchCode : null, g.Count()))
            .OrderByDescending(c => c.Count).ToList();

        var ageing = AgeBuckets.Select(bucket => new HoldCount(bucket.Key,
            active.Count(a => (now - a.AppliedAt).TotalDays >= bucket.From && (now - a.AppliedAt).TotalDays < bucket.To))).ToList();

        return TypedResults.Ok(new HoldSummaryResponse(
            now, active.Count, byHold,
            Count(withDefinition.Select(x => x.d?.HoldType)),
            Count(withDefinition.Select(x => x.d?.BlockingScope)),
            Count(active.Select(a => a.Source)),
            byDepot, ageing, trend));
    }

    private static readonly (string Key, double From, double To)[] AgeBuckets =
    [
        ("UNDER_1_DAY", 0, 1), ("1_TO_3_DAYS", 1, 3), ("3_TO_7_DAYS", 3, 7), ("7_TO_30_DAYS", 7, 30), ("30_DAYS_PLUS", 30, double.MaxValue),
    ];

    // ── the visible set ─────────────────────────────────────────────────────

    private sealed class BoardSource
    {
        public ContainerHold Hold { get; init; } = null!;
        public string? OrderNo { get; init; }
        public Guid? BookingBranchId { get; init; }
        public Guid? DepotId { get; init; }
        public int? BoxesOnBooking { get; init; }
    }

    /// <summary>Every hold with its depot, narrowed to what a branch-scoped caller may see.</summary>
    private static IQueryable<BoardSource> Visible(TosDbContext db, ICallerPermissions scope, string permission)
    {
        var rows =
            from h in db.ContainerHolds.AsNoTracking()
            join b in db.Bookings on h.BookingId equals b.BookingId into bookings
            from b in bookings.DefaultIfEmpty()
            select new BoardSource
            {
                Hold = h,
                OrderNo = b == null ? null : b.OrderNo,
                BookingBranchId = b == null ? (Guid?)null : b.BranchId,
                DepotId = b != null
                    ? (Guid?)b.BranchId
                    : db.VwContainerInYards.Where(v => v.ContainerNo == h.ContainerNo).Select(v => (Guid?)v.BranchId).FirstOrDefault(),
                BoxesOnBooking = b == null
                    ? null
                    : (int?)db.BookingContainers.Count(bc => bc.BookingId == b.BookingId && bc.EndedAt == null),
            };

        if (scope.BranchFilter(permission) is { } mine)
        {
            var allowed = mine.ToList();
            rows = rows.Where(r => r.DepotId == null || allowed.Contains(r.DepotId.Value));
        }
        return rows;
    }
}

// ── contracts ───────────────────────────────────────────────────────────────

/// <summary>
/// One board row: the hold as <c>GET /holds</c> returns it, plus its depot, its
/// age in whole days (held so far, or held for, once released) and whether the
/// caller may release it — the same rule the release endpoint applies.
/// </summary>
public sealed record HoldBoardRow(
    HoldResponse Hold, Guid? DepotBranchId, string? DepotCode, string HeldOn, int? BoxesOnBooking, int AgeDays,
    bool CanRelease, bool ReleaseRefRequired, string? ReleasePermission);

public sealed record HoldSummaryResponse(
    DateTimeOffset AsOf, int Active, IReadOnlyList<HoldCodeCount> ByHold, IReadOnlyList<HoldCount> ByHoldType,
    IReadOnlyList<HoldCount> ByBlockingScope, IReadOnlyList<HoldCount> BySource, IReadOnlyList<HoldDepotCount> ByDepot,
    IReadOnlyList<HoldCount> Ageing, IReadOnlyList<HoldDayCount> ReleasedPerDay);

public sealed record HoldCount(string Key, int Count);

public sealed record HoldCodeCount(
    string HoldCode, string? Description, string? HoldType, string? BlockingScope, string? ReleaseAuthority,
    string? DisplayColorHex, bool TypeIsActive, int Count);

/// <summary><c>BranchId</c> null = on a box that is in no yard.</summary>
public sealed record HoldDepotCount(Guid? BranchId, string? BranchCode, int Count);

public sealed record HoldDayCount(DateOnly Day, int Count);
