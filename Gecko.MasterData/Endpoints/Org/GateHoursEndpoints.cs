using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Gecko.Data;
using Gecko.MasterData.Application;
using Gecko.MasterData.Contracts;
using Gecko.MasterData.Infrastructure.Persistence;
using Gecko.MasterData.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Endpoints.Org;

/// <param name="IsoWeekday">1 = Monday … 7 = Sunday.</param>
/// <param name="IsOvernight">Closes on the next day (closesAt at or before opensAt).</param>
public sealed record GateHoursWindowResponse(
    Guid GateHoursWindowId, Guid BranchId, byte IsoWeekday, string OpensAt, string ClosesAt,
    bool IsOvernight, int Minutes, string RowVersion);

/// <summary>Times are the depot's local "HH:mm". closesAt at or before opensAt = overnight; "00:00" = midnight.</summary>
public sealed record SaveGateHoursWindowRequest(
    Guid BranchId,
    [property: Range(1, 7, ErrorMessage = "1 = Monday … 7 = Sunday.")] byte IsoWeekday,
    [property: Required, RegularExpression(GateHoursEndpoints.TimePattern, ErrorMessage = "A time is HH:mm (00:00–23:59).")] string OpensAt,
    [property: Required, RegularExpression(GateHoursEndpoints.TimePattern, ErrorMessage = "A time is HH:mm (00:00–23:59).")] string ClosesAt,
    string? RowVersion = null);

public sealed record GateHoursExceptionResponse(
    Guid GateHoursExceptionId, Guid BranchId, DateOnly ExceptionDate, bool IsClosed, string? OpensAt, string? ClosesAt,
    string Reason, string RowVersion);

/// <summary>A one-off date: closed, or open for the one window given instead of the weekday's.</summary>
public sealed record SaveGateHoursExceptionRequest(
    Guid BranchId,
    DateOnly ExceptionDate,
    bool IsClosed,
    [property: RegularExpression(GateHoursEndpoints.TimePattern, ErrorMessage = "A time is HH:mm (00:00–23:59).")] string? OpensAt,
    [property: RegularExpression(GateHoursEndpoints.TimePattern, ErrorMessage = "A time is HH:mm (00:00–23:59).")] string? ClosesAt,
    [property: Required, MaxLength(150)] string Reason,
    string? RowVersion = null);

/// <param name="State">OPEN, OUTSIDE_HOURS, HOLIDAY, CLOSED_DATE — or NOT_CONFIGURED when the depot has no gate hours.</param>
public sealed record GateHoursStatusResponse(
    Guid BranchId, bool IsConfigured, bool IsOpen, string State, string? Note,
    DateTimeOffset? LocalAt, DateTimeOffset? OpenUntil, DateTimeOffset? NextOpensAt);

/// <summary>
/// Gate hours (TIER3_DESIGN_NOTES §1): when each depot's gate is open. Weekly
/// windows per ISO weekday (a break is two windows; overnight allowed), one-off
/// dates, and the tenant's public holidays (read from /public-holidays, never
/// copied). The barrier WARNS outside these hours and never refuses (owner
/// decision). A depot with no windows has no gate hours. Reads need
/// mdm.org.view at the depot (gate clerks hold it); changes need mdm.org.manage
/// AT the depot.
/// </summary>
internal static class GateHoursEndpoints
{
    public const string TimePattern = "^([01][0-9]|2[0-3]):[0-5][0-9]$";
    private static readonly string[] DayNames = ["", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    public static RouteGroupBuilder MapGateHoursEndpoints(this RouteGroupBuilder master)
    {
        var g = master.MapGroup("/gate-hours").WithTags("Master data — organisation");
        g.MapGet("/windows", ListWindowsAsync).RequireBranchPermission(MasterDataPermissions.OrgView).WithSummary("A depot's weekly gate windows (?branchId=)");
        g.MapPost("/windows", CreateWindowAsync).RequireBranchPermission(MasterDataPermissions.OrgManage).Validate<SaveGateHoursWindowRequest>().WithSummary("Add an open window on a weekday (overnight when it closes at or before it opens)");
        g.MapPut("/windows/{windowId:guid}", UpdateWindowAsync).RequireBranchPermission(MasterDataPermissions.OrgManage).Validate<SaveGateHoursWindowRequest>().WithSummary("Change a window (optimistic concurrency on rowVersion)");
        g.MapDelete("/windows/{windowId:guid}", DeleteWindowAsync).RequireBranchPermission(MasterDataPermissions.OrgManage).WithSummary("Soft-delete a window (?rowVersion=)");
        g.MapGet("/exceptions", ListExceptionsAsync).RequireBranchPermission(MasterDataPermissions.OrgView).WithSummary("A depot's one-off dates in a year (?branchId=&year=)");
        g.MapPost("/exceptions", CreateExceptionAsync).RequireBranchPermission(MasterDataPermissions.OrgManage).Validate<SaveGateHoursExceptionRequest>().WithSummary("Add a one-off date: closed, or open for one window");
        g.MapPut("/exceptions/{exceptionId:guid}", UpdateExceptionAsync).RequireBranchPermission(MasterDataPermissions.OrgManage).Validate<SaveGateHoursExceptionRequest>().WithSummary("Change a one-off date (optimistic concurrency on rowVersion)");
        g.MapDelete("/exceptions/{exceptionId:guid}", DeleteExceptionAsync).RequireBranchPermission(MasterDataPermissions.OrgManage).WithSummary("Soft-delete a one-off date (?rowVersion=)");
        g.MapGet("/status", StatusAsync).RequireBranchPermission(MasterDataPermissions.OrgView).WithSummary("Is the depot's gate open now (or ?at=), and when does it open next");
        return master;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string Hm(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
    private static TimeOnly ParseHm(string s) => TimeOnly.ParseExact(s, "HH:mm", CultureInfo.InvariantCulture);
    private static string V(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    private static GateHoursWindowResponse Map(GateHoursWindow w) =>
        new(w.GateHoursWindowId, w.BranchId, w.IsoWeekday, Hm(w.OpensAt), Hm(w.ClosesAt),
            w.ClosesAt <= w.OpensAt, GateHoursCalendar.Minutes(w.OpensAt, w.ClosesAt), V(w.RowVersion));

    private static GateHoursExceptionResponse Map(GateHoursException e) =>
        new(e.GateHoursExceptionId, e.BranchId, e.ExceptionDate, e.IsClosed,
            e.OpensAt is { } o ? Hm(o) : null, e.ClosesAt is { } c ? Hm(c) : null, e.Reason, V(e.RowVersion));

    private static Task<bool> DepotExistsAsync(MasterDataDbContext db, Guid branchId, CancellationToken ct) =>
        db.BranchProfiles.AsNoTracking().AnyAsync(b => b.BranchId == branchId, ct);

    private static ValidationProblem MissingBranch() => MasterDataSupport.InvalidReference("branchId", "Choose the depot.");

    // ── weekly windows ──────────────────────────────────────────────────────

    private static async Task<Results<Ok<IReadOnlyList<GateHoursWindowResponse>>, ForbidHttpResult, ValidationProblem>> ListWindowsAsync(
        MasterDataDbContext db, ICallerPermissions scope, CancellationToken ct, Guid? branchId = null)
    {
        if (branchId is not { } b || b == Guid.Empty) return MissingBranch();
        if (!scope.HasAt(MasterDataPermissions.OrgView, b)) return TypedResults.Forbid();
        var rows = await db.GateHoursWindows.AsNoTracking().Where(w => w.BranchId == b)
            .OrderBy(w => w.IsoWeekday).ThenBy(w => w.OpensAt).ToListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<GateHoursWindowResponse>>(rows.Select(Map).ToList());
    }

    private static async Task<Results<Created<GateHoursWindowResponse>, ForbidHttpResult, ValidationProblem>> CreateWindowAsync(
        SaveGateHoursWindowRequest request, MasterDataDbContext db, ITenantContext caller, ICallerPermissions scope, CancellationToken ct)
    {
        if (request.BranchId == Guid.Empty) return MissingBranch();
        if (!scope.HasAt(MasterDataPermissions.OrgManage, request.BranchId)) return TypedResults.Forbid();
        if (await ValidateWindowAsync(db, request, null, ct) is { } invalid) return invalid;

        var window = new GateHoursWindow { TenantId = caller.TenantId(), BranchId = request.BranchId };
        Apply(window, request);
        db.GateHoursWindows.Add(window);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/master/gate-hours/windows/{window.GateHoursWindowId}", Map(window));
    }

    private static async Task<Results<Ok<GateHoursWindowResponse>, NotFound, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> UpdateWindowAsync(
        Guid windowId, SaveGateHoursWindowRequest request, MasterDataDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var window = await db.GateHoursWindows.SingleOrDefaultAsync(w => w.GateHoursWindowId == windowId, ct);
        if (window is null) return TypedResults.NotFound();
        if (!scope.HasAt(MasterDataPermissions.OrgManage, window.BranchId)) return TypedResults.Forbid();
        if (request.BranchId != window.BranchId)
            return MasterDataSupport.InvalidReference("branchId", "A window stays at its depot. Delete it and add one at the other depot.");
        if (db.ExpectVersion(window, request.RowVersion) is { } missing) return missing;
        if (await ValidateWindowAsync(db, request, windowId, ct) is { } invalid) return invalid;

        Apply(window, request);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(Map(window));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> DeleteWindowAsync(
        Guid windowId, string? rowVersion, MasterDataDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var window = await db.GateHoursWindows.SingleOrDefaultAsync(w => w.GateHoursWindowId == windowId, ct);
        if (window is null) return TypedResults.NotFound();
        if (!scope.HasAt(MasterDataPermissions.OrgManage, window.BranchId)) return TypedResults.Forbid();
        if (db.ExpectVersion(window, rowVersion) is { } missing) return missing;
        db.GateHoursWindows.Remove(window);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    /// <summary>Known depot, a real span, and no overlap with the depot's other windows — across midnight too.</summary>
    private static async Task<ValidationProblem?> ValidateWindowAsync(
        MasterDataDbContext db, SaveGateHoursWindowRequest r, Guid? except, CancellationToken ct)
    {
        if (!await DepotExistsAsync(db, r.BranchId, ct))
            return MasterDataSupport.InvalidReference("branchId", "Unknown depot for this tenant.");
        var opens = ParseHm(r.OpensAt);
        var closes = ParseHm(r.ClosesAt);
        if (opens == closes)
            return MasterDataSupport.InvalidReference("closesAt", "A window must close at a different time than it opens. For 24 hours, add two windows.");

        var candidate = new GateHoursCalendar.Window(r.IsoWeekday, opens, closes);
        var others = await db.GateHoursWindows.AsNoTracking()
            .Where(w => w.BranchId == r.BranchId && w.GateHoursWindowId != except).ToListAsync(ct);
        var clash = others.FirstOrDefault(w => GateHoursCalendar.Overlaps(candidate, new GateHoursCalendar.Window(w.IsoWeekday, w.OpensAt, w.ClosesAt)));
        return clash is null
            ? null
            : MasterDataSupport.InvalidReference("opensAt",
                $"Overlaps {DayNames[clash.IsoWeekday]} {Hm(clash.OpensAt)}–{Hm(clash.ClosesAt)}{(clash.ClosesAt <= clash.OpensAt ? " (overnight)" : "")}.");
    }

    private static void Apply(GateHoursWindow w, SaveGateHoursWindowRequest r)
    {
        w.IsoWeekday = r.IsoWeekday;
        w.OpensAt = ParseHm(r.OpensAt);
        w.ClosesAt = ParseHm(r.ClosesAt);
    }

    // ── one-off dates ───────────────────────────────────────────────────────

    private static async Task<Results<Ok<IReadOnlyList<GateHoursExceptionResponse>>, ForbidHttpResult, ValidationProblem>> ListExceptionsAsync(
        MasterDataDbContext db, ICallerPermissions scope, CancellationToken ct, Guid? branchId = null, int? year = null)
    {
        if (branchId is not { } b || b == Guid.Empty) return MissingBranch();
        if (!scope.HasAt(MasterDataPermissions.OrgView, b)) return TypedResults.Forbid();
        var y = year ?? DateTime.UtcNow.Year;
        var from = new DateOnly(y, 1, 1);
        var to = new DateOnly(y, 12, 31);
        var rows = await db.GateHoursExceptions.AsNoTracking()
            .Where(e => e.BranchId == b && e.ExceptionDate >= from && e.ExceptionDate <= to)
            .OrderBy(e => e.ExceptionDate).ToListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<GateHoursExceptionResponse>>(rows.Select(Map).ToList());
    }

    private static async Task<Results<Created<GateHoursExceptionResponse>, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> CreateExceptionAsync(
        SaveGateHoursExceptionRequest request, MasterDataDbContext db, ITenantContext caller, ICallerPermissions scope, CancellationToken ct)
    {
        if (request.BranchId == Guid.Empty) return MissingBranch();
        if (!scope.HasAt(MasterDataPermissions.OrgManage, request.BranchId)) return TypedResults.Forbid();
        var (invalid, clash) = await ValidateExceptionAsync(db, request, null, ct);
        if (invalid is not null) return invalid;
        if (clash is not null) return clash;

        var exception = new GateHoursException { TenantId = caller.TenantId(), BranchId = request.BranchId };
        Apply(exception, request);
        db.GateHoursExceptions.Add(exception);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/master/gate-hours/exceptions/{exception.GateHoursExceptionId}", Map(exception));
    }

    private static async Task<Results<Ok<GateHoursExceptionResponse>, NotFound, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> UpdateExceptionAsync(
        Guid exceptionId, SaveGateHoursExceptionRequest request, MasterDataDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var exception = await db.GateHoursExceptions.SingleOrDefaultAsync(e => e.GateHoursExceptionId == exceptionId, ct);
        if (exception is null) return TypedResults.NotFound();
        if (!scope.HasAt(MasterDataPermissions.OrgManage, exception.BranchId)) return TypedResults.Forbid();
        if (request.BranchId != exception.BranchId)
            return MasterDataSupport.InvalidReference("branchId", "A date stays at its depot. Delete it and add one at the other depot.");
        if (db.ExpectVersion(exception, request.RowVersion) is { } missing) return missing;
        var (invalid, clash) = await ValidateExceptionAsync(db, request, exceptionId, ct);
        if (invalid is not null) return invalid;
        if (clash is not null) return clash;

        Apply(exception, request);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(Map(exception));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> DeleteExceptionAsync(
        Guid exceptionId, string? rowVersion, MasterDataDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var exception = await db.GateHoursExceptions.SingleOrDefaultAsync(e => e.GateHoursExceptionId == exceptionId, ct);
        if (exception is null) return TypedResults.NotFound();
        if (!scope.HasAt(MasterDataPermissions.OrgManage, exception.BranchId)) return TypedResults.Forbid();
        if (db.ExpectVersion(exception, rowVersion) is { } missing) return missing;
        db.GateHoursExceptions.Remove(exception);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    /// <summary>uq_gate_hours_exception__date: one per depot per date — a 409 naming it, as for holidays.</summary>
    private static async Task<(ValidationProblem? Invalid, ProblemHttpResult? Clash)> ValidateExceptionAsync(
        MasterDataDbContext db, SaveGateHoursExceptionRequest r, Guid? except, CancellationToken ct)
    {
        if (r.ExceptionDate == default)
            return (MasterDataSupport.InvalidReference("exceptionDate", "Give the date."), null);
        if (!await DepotExistsAsync(db, r.BranchId, ct))
            return (MasterDataSupport.InvalidReference("branchId", "Unknown depot for this tenant."), null);
        if (r.IsClosed)
        {
            if (r.OpensAt is not null || r.ClosesAt is not null)
                return (MasterDataSupport.InvalidReference(r.OpensAt is not null ? "opensAt" : "closesAt", "A closed day has no hours. Clear them, or untick closed."), null);
        }
        else
        {
            if (r.OpensAt is null) return (MasterDataSupport.InvalidReference("opensAt", "Give the time the gate opens that day, or mark the day closed."), null);
            if (r.ClosesAt is null) return (MasterDataSupport.InvalidReference("closesAt", "Give the time the gate closes that day, or mark the day closed."), null);
            if (r.OpensAt == r.ClosesAt) return (MasterDataSupport.InvalidReference("closesAt", "The gate must close at a different time than it opens."), null);
        }

        var same = await db.GateHoursExceptions.AsNoTracking()
            .Where(e => e.BranchId == r.BranchId && e.ExceptionDate == r.ExceptionDate && e.GateHoursExceptionId != except)
            .Select(e => e.Reason).FirstOrDefaultAsync(ct);
        return (null, same is null ? null : MasterDataSupport.Conflict("That date is already set.", $"{r.ExceptionDate:yyyy-MM-dd} is already '{same}' at this depot."));
    }

    private static void Apply(GateHoursException e, SaveGateHoursExceptionRequest r)
    {
        e.ExceptionDate = r.ExceptionDate;
        e.IsClosed = r.IsClosed;
        e.OpensAt = r.IsClosed || r.OpensAt is null ? null : ParseHm(r.OpensAt);
        e.ClosesAt = r.IsClosed || r.ClosesAt is null ? null : ParseHm(r.ClosesAt);
        e.Reason = r.Reason.Trim();
    }

    // ── status ──────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<GateHoursStatusResponse>, ForbidHttpResult, ValidationProblem>> StatusAsync(
        IMasterDataReferences references, ICallerPermissions scope, TimeProvider clock, CancellationToken ct,
        Guid? branchId = null, DateTimeOffset? at = null)
    {
        if (branchId is not { } b || b == Guid.Empty) return MissingBranch();
        if (!scope.HasAt(MasterDataPermissions.OrgView, b)) return TypedResults.Forbid();
        var status = await references.GateHoursStatusAsync(b, at ?? clock.GetUtcNow(), ct);
        return TypedResults.Ok(status is null
            ? new GateHoursStatusResponse(b, false, true, "NOT_CONFIGURED", null, null, null, null)
            : new GateHoursStatusResponse(b, true, status.IsOpen, status.State, status.Note, status.LocalAt, status.OpenUntil, status.NextOpensAt));
    }
}
