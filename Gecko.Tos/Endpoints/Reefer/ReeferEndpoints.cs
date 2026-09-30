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

namespace Gecko.Tos.Endpoints.Reefer;

/// <summary>
/// The reefer plug log (TIER3_DESIGN_NOTES §6): when a reefer in the yard was
/// plugged in and out — the physical fact a power charge is priced from. TOS
/// records it and says how long; Revenue prices it from the outbox events
/// (<see cref="ReeferLog"/>). Readings, PTI and pre-cool are not here.
///
/// Every row belongs to the depot the box is standing in, so every read and write
/// is branch-scoped: the permission is the door, <c>HasAt(branch)</c> is the row.
/// </summary>
internal static class ReeferEndpoints
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    public static RouteGroupBuilder MapReeferEndpoints(this RouteGroupBuilder tos)
    {
        var reefer = tos.MapGroup("/reefer").WithTags("TOS — reefer");

        reefer.MapGet("/sessions", ListAsync).RequireBranchPermission(TosPermissions.ReeferView)
            .WithSummary("Plug-in/out sessions: plugged in now (OPEN), the closed log, or ALL — with hours so far");
        reefer.MapGet("/sessions/{id:guid}", GetAsync).RequireBranchPermission(TosPermissions.ReeferView)
            .WithName("GetReeferSession").WithSummary("One plug-in/out session");
        reefer.MapGet("/candidates", CandidatesAsync).RequireBranchPermission(TosPermissions.ReeferManage)
            .WithSummary("Reefers standing in the yard with nothing plugged in");
        reefer.MapPost("/sessions", PlugInAsync).RequireBranchPermission(TosPermissions.ReeferManage)
            .Validate<PlugInRequest>().WithSummary("Plug a reefer in");
        reefer.MapPost("/sessions/{id:guid}/plug-out", PlugOutAsync).RequireBranchPermission(TosPermissions.ReeferManage)
            .Validate<PlugOutRequest>().WithSummary("Plug a reefer out");
        reefer.MapPut("/sessions/{id:guid}", CorrectAsync).RequireBranchPermission(TosPermissions.ReeferManage)
            .Validate<CorrectReeferSessionRequest>().WithSummary("Correct a session's times or details");
        reefer.MapDelete("/sessions/{id:guid}", VoidAsync).RequireBranchPermission(TosPermissions.ReeferManage)
            .WithSummary("Void a session entered by mistake (only while the box is still in the yard)");

        return tos;
    }

    // ── reads ───────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<PagedResult<ReeferSessionResponse>>, ValidationProblem, ProblemHttpResult>> ListAsync(
        [AsParameters] ListQuery query, TosDbContext db, BranchClock clock, ICallerPermissions scope, TimeProvider time,
        CancellationToken ct, Guid? branchId = null, string? status = null, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var state = status.Clean() ?? "OPEN";
        if (state is not ("OPEN" or "CLOSED" or "ALL")) return TosSupport.Invalid("status", "Use OPEN, CLOSED or ALL.");
        if (from is not null && to is not null && from > to) return TosSupport.Invalid("from", "from is after to.");
        if (branchId is { } asked && !scope.HasAt(TosPermissions.ReeferView, asked))
            return TosScope.OutsideYourBranches("That depot is not one you cover.");

        var rows = Visible(db, scope);
        if (branchId is not null) rows = rows.Where(r => r.Session.BranchId == branchId);
        if (state == "OPEN") rows = rows.Where(r => r.Session.PluggedOutAt == null);
        if (state == "CLOSED") rows = rows.Where(r => r.Session.PluggedOutAt != null);
        if (from is not null) rows = rows.Where(r => r.Session.PluggedInAt >= from);
        if (to is not null) rows = rows.Where(r => r.Session.PluggedInAt <= to);
        if (query.Search.Clean() is { } q) rows = rows.Where(r => r.Session.ContainerNo.Contains(q));

        // Plugged in now: longest first (the one drawing power longest is the one to ask
        // about). The log: most recent first.
        var ordered = state == "OPEN"
            ? rows.OrderBy(r => r.Session.PluggedInAt)
            : rows.OrderByDescending(r => r.Session.PluggedInAt);
        var page = await ordered.ToPagedAsync(query.Page, query.PageSize, ct);

        var branches = await clock.BranchesAsync(page.Items.Select(r => r.Session.BranchId), ct);
        var now = time.GetUtcNow();
        return TypedResults.Ok(new PagedResult<ReeferSessionResponse>(
            page.Items.Select(r => Project(r, branches, scope, now)).ToList(), page.Page, page.PageSize, page.TotalCount));
    }

    private static async Task<Results<Ok<ReeferSessionResponse>, NotFound>> GetAsync(
        Guid id, TosDbContext db, BranchClock clock, ICallerPermissions scope, TimeProvider time, CancellationToken ct) =>
        await DetailAsync(db, clock, scope, time, id, ct) is { } row ? TypedResults.Ok(row) : TypedResults.NotFound();

    private static async Task<Results<Ok<PagedResult<ReeferCandidateResponse>>, ProblemHttpResult>> CandidatesAsync(
        [AsParameters] ListQuery query, TosDbContext db, IMasterDataReferences master, BranchClock clock,
        ICallerPermissions scope, CancellationToken ct, Guid? branchId = null)
    {
        if (branchId is { } asked && !scope.HasAt(TosPermissions.ReeferManage, asked))
            return TosScope.OutsideYourBranches("That depot is not one you cover.");

        var visits = db.ContainerVisits.AsNoTracking().Where(v => v.GateOutTransactionId == null
            && !db.ReeferPowerSessions.Any(s => s.ContainerVisitId == v.ContainerVisitId && s.PluggedOutAt == null));
        if (scope.BranchFilter(TosPermissions.ReeferManage) is { } mine)
        {
            var allowed = mine.ToList();
            visits = visits.Where(v => allowed.Contains(v.BranchId));
        }
        if (branchId is not null) visits = visits.Where(v => v.BranchId == branchId);
        if (query.Search.Clean() is { } q) visits = visits.Where(v => v.ContainerNo.Contains(q));

        // Reefer-ness is MDM's (equipment_type.is_reefer): ask it about the codes in the yard.
        var codes = await visits.Where(v => v.EquipmentTypeCode != null).Select(v => v.EquipmentTypeCode!).Distinct().ToListAsync(ct);
        var types = await master.EquipmentTypesAsync(codes, ct);
        var reefers = types.Values.Where(t => t.IsReefer).Select(t => t.TypeCode).ToList();
        visits = visits.Where(v => reefers.Contains(v.EquipmentTypeCode!));

        var rows =
            from v in visits
            join g in db.GateTransactions on v.GateInTransactionId equals g.GateTransactionId into gates
            from g in gates.DefaultIfEmpty()
            orderby v.ContainerNo
            select new
            {
                v.ContainerVisitId, v.ContainerNo, v.BranchId, v.EquipmentTypeCode,
                GateInAt = g == null ? (DateTimeOffset?)null : g.TransactionAt,
                // The booking line's set point, else what the gate read off the display.
                SetPoint = db.BookingContainers.Where(bc => bc.BookingContainerId == v.CurrentBookingContainerId)
                               .Join(db.EquipmentRequirements, bc => bc.EquipmentRequirementId, r => r.EquipmentRequirementId, (bc, r) => r.ReeferSetTempC)
                               .FirstOrDefault()
                           ?? (g == null ? null : g.TempObservedC),
            };
        var page = await rows.ToPagedAsync(query.Page, query.PageSize, ct);
        var branches = await clock.BranchesAsync(page.Items.Select(r => r.BranchId), ct);

        return TypedResults.Ok(new PagedResult<ReeferCandidateResponse>(
            page.Items.Select(r => new ReeferCandidateResponse(
                r.ContainerVisitId, r.ContainerNo, r.BranchId, branches.GetValueOrDefault(r.BranchId)?.BranchCode,
                r.EquipmentTypeCode, r.GateInAt, r.SetPoint)).ToList(),
            page.Page, page.PageSize, page.TotalCount));
    }

    // ── plug in ─────────────────────────────────────────────────────────────

    private static async Task<Results<CreatedAtRoute<ReeferSessionResponse>, ValidationProblem, ProblemHttpResult>> PlugInAsync(
        PlugInRequest request, TosDbContext db, IMasterDataReferences master, BranchClock clock,
        ITenantContext caller, ICallerPermissions scope, TimeProvider time, CancellationToken ct)
    {
        var box = ContainerNumber.Normalise(request.ContainerNo);
        var visit = await db.ContainerVisits.SingleOrDefaultAsync(v => v.ContainerNo == box && v.GateOutTransactionId == null, ct);
        if (visit is null) return TosSupport.Invalid("containerNo", $"{box} is not in the yard. Only a box standing in the yard can be plugged in.");
        if (!scope.HasAt(TosPermissions.ReeferManage, visit.BranchId))
            return TosScope.OutsideYourBranches($"{box} is standing at a depot you do not cover.");

        var type = visit.EquipmentTypeCode is { } code ? (await master.EquipmentTypesAsync([code], ct)).GetValueOrDefault(code) : null;
        if (type is not { IsReefer: true })
            return TosSupport.Invalid("containerNo", $"{box} is a {visit.EquipmentTypeCode ?? "box of unknown type"}, not a reefer. Only a reefer draws power.");

        if (await db.ReeferPowerSessions.AnyAsync(s => s.ContainerVisitId == visit.ContainerVisitId && s.PluggedOutAt == null, ct))
            return AlreadyPluggedIn(box);

        var now = time.GetUtcNow();
        var at = request.PluggedInAt ?? now;
        var gateInAt = await GateTimeAsync(db, visit.GateInTransactionId, ct);
        if (at > now + ClockSkew) return TosSupport.Invalid("pluggedInAt", "A reefer cannot be plugged in in the future.");
        if (gateInAt is { } gi && at < gi)
            return TosSupport.Invalid("pluggedInAt", $"{box} came through the gate at {gi:u}; it cannot have been plugged in before that.");

        var session = new ReeferPowerSession
        {
            TenantId = caller.TenantId(),
            BranchId = visit.BranchId,
            ContainerVisitId = visit.ContainerVisitId,
            ContainerNo = box,
            PluggedInAt = at,
            PluggedInBy = caller.UserId(),
            PlugPointCode = Text(request.PlugPointCode),
            SetPointC = request.SetPointC,
            Remarks = Text(request.Remarks),
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ReeferPowerSessions.Add(session);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (IsUniqueViolation(e)) { return AlreadyPluggedIn(box); }

        VisitJournal.Add(db, visit, "PLUG_IN", null, session.PlugPointCode, at, caller.UserId(), session.ReeferPowerSessionId, session.Remarks);
        if (at > visit.LastEventAt) visit.LastEventAt = at;
        await ReeferLog.EnqueueAsync(db, session, visit, ReeferLog.Plugged, now, ct);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await tx.CommitAsync(ct);

        return TypedResults.CreatedAtRoute(
            (await DetailAsync(db, clock, scope, time, session.ReeferPowerSessionId, ct))!,
            "GetReeferSession", new { id = session.ReeferPowerSessionId });
    }

    // ── plug out ────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<ReeferSessionResponse>, NotFound, ValidationProblem, ProblemHttpResult>> PlugOutAsync(
        Guid id, PlugOutRequest request, TosDbContext db, BranchClock clock, ITenantContext caller,
        ICallerPermissions scope, TimeProvider time, CancellationToken ct)
    {
        var (session, visit, denied) = await LoadForWriteAsync(db, scope, id, ct);
        if (session is null || visit is null) return TypedResults.NotFound();
        if (denied is not null) return denied;
        if (!db.TrySetExpectedVersion(session, request.RowVersion))
            return TosSupport.Invalid("rowVersion", "Send the rowVersion you read, so a plug-out cannot overwrite a change you have not seen.");
        if (session.PluggedOutAt is not null)
            return TosSupport.Conflict($"{session.ContainerNo} was already plugged out at {session.PluggedOutAt:u}.",
                session.CloseReason == ReeferLog.GateOutClose ? "It was unplugged when it went out of the gate." : null);

        var now = time.GetUtcNow();
        var at = request.PluggedOutAt ?? now;
        if (at > now + ClockSkew) return TosSupport.Invalid("pluggedOutAt", "A reefer cannot be plugged out in the future.");
        if (at < session.PluggedInAt)
            return TosSupport.Invalid("pluggedOutAt", $"It was plugged in at {session.PluggedInAt:u}; it cannot come out before that.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        session.PluggedOutAt = at;
        session.PluggedOutBy = caller.UserId();
        session.CloseReason = ReeferLog.ManualClose;
        if (request.Remarks is not null) session.Remarks = Text(request.Remarks);
        VisitJournal.Add(db, visit, "PLUG_OUT", session.PlugPointCode, ReeferLog.ManualClose, at, caller.UserId(), session.ReeferPowerSessionId);
        if (at > visit.LastEventAt) visit.LastEventAt = at;
        await ReeferLog.EnqueueAsync(db, session, visit, ReeferLog.Unplugged, now, ct);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await tx.CommitAsync(ct);

        return TypedResults.Ok((await DetailAsync(db, clock, scope, time, id, ct))!);
    }

    // ── correct ─────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<ReeferSessionResponse>, NotFound, ValidationProblem, ProblemHttpResult>> CorrectAsync(
        Guid id, CorrectReeferSessionRequest request, TosDbContext db, BranchClock clock, ITenantContext caller,
        ICallerPermissions scope, TimeProvider time, CancellationToken ct)
    {
        var (session, visit, denied) = await LoadForWriteAsync(db, scope, id, ct);
        if (session is null || visit is null) return TypedResults.NotFound();
        if (denied is not null) return denied;
        if (!db.TrySetExpectedVersion(session, request.RowVersion))
            return TosSupport.Invalid("rowVersion", "Send the rowVersion you read, so a correction cannot overwrite a change you have not seen.");

        var errors = new Dictionary<string, List<string>>();
        var now = time.GetUtcNow();
        var pluggedIn = request.PluggedInAt!.Value;
        var pluggedOut = request.PluggedOutAt;

        if (session.PluggedOutAt is null && pluggedOut is not null)
            errors.Add("pluggedOutAt", "The session is still open. Plug it out to close it; a correction does not close it.");
        if (session.PluggedOutAt is not null && pluggedOut is null)
            errors.Add("pluggedOutAt", "The session is closed. A correction cannot re-open it; give the plug-out time.");

        if (pluggedIn > now + ClockSkew) errors.Add("pluggedInAt", "A reefer cannot be plugged in in the future.");
        if (await GateTimeAsync(db, visit.GateInTransactionId, ct) is { } gateIn && pluggedIn < gateIn)
            errors.Add("pluggedInAt", $"{session.ContainerNo} came through the gate at {gateIn:u}; it cannot have been plugged in before that.");
        if (pluggedOut is { } po)
        {
            if (po > now + ClockSkew) errors.Add("pluggedOutAt", "A reefer cannot be plugged out in the future.");
            if (po < pluggedIn) errors.Add("pluggedOutAt", "Plugged out before it was plugged in.");
        }
        if (visit.GateOutTransactionId is { } gateOutId && await GateTimeAsync(db, gateOutId, ct) is { } gateOut)
        {
            if (pluggedIn > gateOut) errors.Add("pluggedInAt", $"{session.ContainerNo} went out of the gate at {gateOut:u}.");
            if (pluggedOut > gateOut) errors.Add("pluggedOutAt", $"{session.ContainerNo} went out of the gate at {gateOut:u}; it was unplugged by then.");
        }
        if (errors.Count > 0) return TosSupport.Invalid(errors);

        var before = Span(session.PluggedInAt, session.PluggedOutAt);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        session.PluggedInAt = pluggedIn;
        session.PluggedOutAt = pluggedOut;
        session.PlugPointCode = Text(request.PlugPointCode);
        session.SetPointC = request.SetPointC;
        session.Remarks = Text(request.Remarks);

        // The house habit (a void re-opening the visit): a correction is journalled as CORRECTION.
        VisitJournal.Add(db, visit, "CORRECTION", before, Span(pluggedIn, pluggedOut), now, caller.UserId(), session.ReeferPowerSessionId,
            "Reefer plug session corrected.");
        if (visit.GateOutTransactionId is null && now > visit.LastEventAt) visit.LastEventAt = now;
        await ReeferLog.EnqueueAsync(db, session, visit, ReeferLog.Corrected, now, ct);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await tx.CommitAsync(ct);

        return TypedResults.Ok((await DetailAsync(db, clock, scope, time, id, ct))!);
    }

    // ── void ────────────────────────────────────────────────────────────────

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> VoidAsync(
        Guid id, TosDbContext db, ITenantContext caller, ICallerPermissions scope, TimeProvider time,
        CancellationToken ct, string? rowVersion = null)
    {
        var (session, visit, denied) = await LoadForWriteAsync(db, scope, id, ct);
        if (session is null || visit is null) return TypedResults.NotFound();
        if (denied is not null) return denied;
        if (!db.TrySetExpectedVersion(session, rowVersion))
            return TosSupport.Invalid("rowVersion", "Send the rowVersion you read (?rowVersion=), so a void cannot remove a change you have not seen.");
        if (visit.GateOutTransactionId is not null)
            return TosSupport.Conflict($"{session.ContainerNo} has already gone out of the gate.",
                "Its power time may already have been billed. Correct the session's times instead of voiding it.");

        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        session.DeletedAt = now;
        session.DeletedBy = caller.UserId();
        VisitJournal.Add(db, visit, "CORRECTION", Span(session.PluggedInAt, session.PluggedOutAt), null, now, caller.UserId(),
            session.ReeferPowerSessionId, "Reefer plug session voided (entered by mistake).");
        if (now > visit.LastEventAt) visit.LastEventAt = now;
        await ReeferLog.EnqueueAsync(db, session, visit, ReeferLog.Voided, now, ct);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await tx.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private sealed class SessionSource
    {
        public ReeferPowerSession Session { get; init; } = null!;
        public string? EquipmentTypeCode { get; init; }
        public string? IsoCode { get; init; }
    }

    /// <summary>Sessions with their box's type, narrowed to the depots the caller may view.</summary>
    private static IQueryable<SessionSource> Visible(TosDbContext db, ICallerPermissions scope)
    {
        var rows =
            from s in db.ReeferPowerSessions.AsNoTracking()
            join v in db.ContainerVisits on s.ContainerVisitId equals v.ContainerVisitId
            join g in db.GateTransactions on v.GateInTransactionId equals g.GateTransactionId into gates
            from g in gates.DefaultIfEmpty()
            select new SessionSource { Session = s, EquipmentTypeCode = v.EquipmentTypeCode, IsoCode = g == null ? null : g.IsoCode };

        if (scope.BranchFilter(TosPermissions.ReeferView) is { } mine)
        {
            var allowed = mine.ToList();
            rows = rows.Where(r => allowed.Contains(r.Session.BranchId));
        }
        return rows;
    }

    private static async Task<ReeferSessionResponse?> DetailAsync(
        TosDbContext db, BranchClock clock, ICallerPermissions scope, TimeProvider time, Guid id, CancellationToken ct)
    {
        var row = await Visible(db, scope).SingleOrDefaultAsync(r => r.Session.ReeferPowerSessionId == id, ct);
        if (row is null) return null;
        var branches = await clock.BranchesAsync([row.Session.BranchId], ct);
        return Project(row, branches, scope, time.GetUtcNow());
    }

    private static ReeferSessionResponse Project(
        SessionSource r, IReadOnlyDictionary<Guid, BranchClock.Branch> branches, ICallerPermissions scope, DateTimeOffset now)
    {
        var s = r.Session;
        var plugged = (s.PluggedOutAt ?? now) - s.PluggedInAt;
        return new ReeferSessionResponse(
            s.ReeferPowerSessionId, s.ContainerVisitId, s.ContainerNo, s.BranchId, branches.GetValueOrDefault(s.BranchId)?.BranchCode,
            r.EquipmentTypeCode, r.IsoCode,
            s.PluggedInAt, s.PluggedInBy, s.PluggedOutAt, s.PluggedOutBy, s.CloseReason, s.CloseGateTransactionId,
            s.PlugPointCode, s.SetPointC, s.Remarks,
            ReeferHours.Minutes(plugged), ReeferHours.BillableHours(plugged), s.PluggedOutAt is null,
            scope.HasAt(TosPermissions.ReeferManage, s.BranchId), Convert.ToBase64String(s.RowVersion));
    }

    /// <summary>
    /// The tracked session and its visit. Not viewable = 404 (an id-guessing caller learns
    /// nothing); viewable but not manageable at that depot = 403.
    /// </summary>
    private static async Task<(ReeferPowerSession? Session, ContainerVisit? Visit, ProblemHttpResult? Denied)> LoadForWriteAsync(
        TosDbContext db, ICallerPermissions scope, Guid id, CancellationToken ct)
    {
        var session = await db.ReeferPowerSessions.SingleOrDefaultAsync(s => s.ReeferPowerSessionId == id, ct);
        if (session is null || !scope.HasAt(TosPermissions.ReeferView, session.BranchId)) return (null, null, null);
        var visit = await db.ContainerVisits.SingleOrDefaultAsync(v => v.ContainerVisitId == session.ContainerVisitId, ct);
        if (visit is null) return (null, null, null);
        return scope.HasAt(TosPermissions.ReeferManage, session.BranchId)
            ? (session, visit, null)
            : (session, visit, TosScope.OutsideYourBranches($"{session.ContainerNo} is at a depot where you may only view the plug log."));
    }

    private static Task<DateTimeOffset?> GateTimeAsync(TosDbContext db, Guid gateTransactionId, CancellationToken ct) =>
        db.GateTransactions.AsNoTracking().Where(g => g.GateTransactionId == gateTransactionId)
            .Select(g => (DateTimeOffset?)g.TransactionAt).FirstOrDefaultAsync(ct);

    private static ProblemHttpResult AlreadyPluggedIn(string box) =>
        TosSupport.Conflict($"{box} is already plugged in.", "Plug it out first; one box draws power on one session at a time.");

    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Span(DateTimeOffset pluggedIn, DateTimeOffset? pluggedOut) =>
        $"{pluggedIn.UtcDateTime:yyyy-MM-dd HH:mm}Z–{(pluggedOut is { } o ? $"{o.UtcDateTime:yyyy-MM-dd HH:mm}Z" : "open")}";
}
