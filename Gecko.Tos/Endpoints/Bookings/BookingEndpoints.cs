using Gecko.Data;
using Gecko.Identity.Contracts;
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

namespace Gecko.Tos.Endpoints.Bookings;

/// <summary>
/// Bookings (PLAN §4.2, batch B) — the order a depot is asked to carry out.
///
/// What Vector did, and what this refuses to repeat:
///   103,165 blank placeholder lines as the booked quantity  → requirement rows with a qty (D-3)
///   ETD and cut-offs COPIED onto every booking, then drifted  → the booking points at the call, copies nothing (D-2)
///   186,326 pending steps for boxes nobody assigned            → steps exist only for an ASSIGNED box (D-4)
///   order type stored as its NAME                              → id + code
///   one box active on two bookings                             → filtered unique + a 409 here
///
/// Assigning a box snapshots its order type's steps (with the rule row each came
/// from) as PENDING movement-plan rows. The gate (Phase 5) completes them; no
/// endpoint here can mark a step DONE, because DONE means "a gate transaction says so".
/// </summary>
internal static class BookingEndpoints
{
    public static RouteGroupBuilder MapBookingEndpoints(this RouteGroupBuilder tos)
    {
        // A booking BELONGS to a branch (its order number carries the branch code),
        // so every handler here scopes rows to the branches the caller's permission
        // covers — tenant-wide grants cover all of them (PLAN Q11).
        var bookings = tos.MapGroup("/bookings").WithTags("TOS — bookings");

        bookings.MapGet("/", ListAsync).RequireBranchPermission(TosPermissions.BookingView).WithSummary("List bookings with their derived progress");
        bookings.MapGet("/{id:guid}", GetAsync).RequireBranchPermission(TosPermissions.BookingView).WithName("GetBooking").WithSummary("One booking: header, requirement lines, boxes and their steps");
        bookings.MapPost("/", CreateAsync).RequireBranchPermission(TosPermissions.BookingManage).Validate<SaveBookingRequest>().WithSummary("Create a booking with its requirement lines (and pre-advised boxes)");
        bookings.MapPut("/{id:guid}", UpdateAsync).RequireBranchPermission(TosPermissions.BookingManage).Validate<SaveBookingRequest>().WithSummary("Update the header of an OPEN booking");
        bookings.MapPut("/{id:guid}/requirements", ReplaceRequirementsAsync).RequireBranchPermission(TosPermissions.BookingManage).Validate<ReplaceRequirementsRequest>().WithSummary("Replace the requirement lines (by line number)");
        bookings.MapPost("/{id:guid}/containers", AssignAsync).RequireBranchPermission(TosPermissions.BookingManage).Validate<AssignContainersRequest>().WithSummary("Assign boxes — each gets the order type's steps as PENDING");
        bookings.MapPost("/{id:guid}/containers/batch", AssignBatchAsync).RequireBranchPermission(TosPermissions.BookingManage).Validate<AssignContainersBatchRequest>().WithSummary("Save container rows one by one: each row stands alone, a retry with the same clientLineId gets the same line");
        bookings.MapPut("/{id:guid}/containers/{bookingContainerId:guid}", UpdateLineAsync).RequireBranchPermission(TosPermissions.BookingManage).Validate<UpdateContainerLineRequest>().WithSummary("Edit one container line's details (seals, weight, reefer, DG, stowage, remarks…) with its rowVersion");
        bookings.MapDelete("/{id:guid}/containers/{bookingContainerId:guid}", UnassignAsync).RequireBranchPermission(TosPermissions.BookingManage).WithSummary("Take a box off the booking (refused once it has done a step)");
        bookings.MapPost("/{id:guid}/cancel", CancelAsync).RequireBranchPermission(TosPermissions.BookingCancel).Validate<EndBookingRequest>().WithSummary("Cancel a booking no box has worked on");
        bookings.MapPost("/{id:guid}/close", CloseAsync).RequireBranchPermission(TosPermissions.BookingManage).Validate<EndBookingRequest>().WithSummary("Close a part-used booking; open boxes are released");

        bookings.MapCutoffExceptionEndpoints();

        return tos;
    }

    // ── reads ───────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<PagedResult<BookingSummaryResponse>>, ValidationProblem>> ListAsync(
        [AsParameters] ListQuery query, TosDbContext db, BranchClock clock, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, string? status = null, string? progress = null, string? orderTypeCode = null,
        string? lineCode = null, string? customerCode = null, Guid? vesselCallId = null)
    {
        // EXPIRED needs a branch calendar; every current tenant is on the default
        // zone, so the list filters on that day and the rows below use each branch's own.
        var today = await clock.TodayForAsync(branchId is { } b ? [b] : [], ct);
        var filterDay = today(branchId ?? Guid.Empty);

        var rows =
            from p in db.VwBookingProgresses.AsNoTracking()
            join bk in db.Bookings on p.BookingId equals bk.BookingId
            join vc in db.VesselCalls on bk.VesselCallId equals vc.VesselCallId into calls
            from vc in calls.DefaultIfEmpty()
            join vl in db.VesselCallLines on bk.VesselCallLineId equals vl.VesselCallLineId into lines
            from vl in lines.DefaultIfEmpty()
            select new { p, bk, CallRef = vc == null ? null : vc.CallRef, Voyage = vl == null ? null : (vl.VoyageOut ?? vl.VoyageIn) };

        if (branchId is not null) rows = rows.Where(r => r.bk.BranchId == branchId);
        // A branch-scoped clerk sees their depot's bookings and no others. Not a 403:
        // a list is a list, it just contains what the caller is allowed to see.
        if (scope.BranchFilter(TosPermissions.BookingView) is { } mine)
        {
            var allowed = mine.ToList();
            rows = rows.Where(r => allowed.Contains(r.bk.BranchId));
        }
        if (status.Clean() is { } s)
        {
            if (!BookingRules.Statuses.Contains(s)) return TosSupport.Invalid("status", $"Use one of: {string.Join(", ", BookingRules.Statuses)}.");
            rows = rows.Where(r => r.bk.Status == s);
        }
        if (progress.Clean() is { } pr)
        {
            if (!BookingRules.Progresses.Contains(pr)) return TosSupport.Invalid("progress", $"Use one of: {string.Join(", ", BookingRules.Progresses)}.");
            rows = pr == BookingRules.Expired
                ? rows.Where(r => (r.p.ProgressStatus == BookingRules.NotStarted || r.p.ProgressStatus == BookingRules.InProgress) && r.p.ValidTo < filterDay)
                : pr is BookingRules.NotStarted or BookingRules.InProgress
                    ? rows.Where(r => r.p.ProgressStatus == pr && (r.p.ValidTo == null || r.p.ValidTo >= filterDay))
                    : rows.Where(r => r.p.ProgressStatus == pr);
        }
        if (orderTypeCode.Clean() is { } ot) rows = rows.Where(r => r.bk.OrderTypeCode == ot);
        if (lineCode.Clean() is { } l) rows = rows.Where(r => r.bk.LinePartyCode == l);
        if (customerCode.Clean() is { } c) rows = rows.Where(r => r.bk.CustomerPartyCode == c);
        if (vesselCallId is not null) rows = rows.Where(r => r.bk.VesselCallId == vesselCallId);
        if (query.Search.Clean() is { } q)
        {
            var box = ContainerNumber.Normalise(q);
            rows = rows.Where(r => r.bk.OrderNo.Contains(q) || r.bk.CarrierRef!.Contains(q) || r.bk.CustomerRef!.Contains(q)
                || db.BookingContainers.Any(x => x.BookingId == r.bk.BookingId && x.ContainerNo == box));
        }

        var page = await rows.OrderByDescending(r => r.bk.CreatedAt).ThenByDescending(r => r.bk.OrderNo)
            .ToPagedAsync(query.Page, query.PageSize, ct);

        var branches = await clock.BranchesAsync(page.Items.Select(r => r.bk.BranchId), ct);
        var todayOf = await clock.TodayForAsync(page.Items.Select(r => r.bk.BranchId), ct);

        return TypedResults.Ok(new PagedResult<BookingSummaryResponse>(
            page.Items.Select(r => new BookingSummaryResponse(
                r.bk.BookingId, r.bk.OrderNo, r.bk.BranchId, branches.GetValueOrDefault(r.bk.BranchId)?.BranchCode, r.bk.CarrierRef,
                r.bk.OrderTypeCode, r.bk.DirectionCode, r.bk.LinePartyCode, r.bk.CustomerPartyCode,
                r.bk.VesselCallId, r.CallRef, r.Voyage,
                r.bk.Status, BookingRules.EffectiveProgress(r.p.ProgressStatus, r.p.ValidTo, todayOf(r.bk.BranchId)),
                r.p.QtyRequired, r.p.QtyAssigned, r.p.QtyCompleted, r.bk.ValidTo, r.bk.Source, r.bk.CreatedAt)).ToList(),
            page.Page, page.PageSize, page.TotalCount));
    }

    private static async Task<Results<Ok<BookingDetailResponse>, NotFound>> GetAsync(
        Guid id, TosDbContext db, BranchClock clock, IMasterDataReferences master, IUserDirectory users, ICallerPermissions scope, CancellationToken ct)
    {
        // Another branch's booking reads as NOT FOUND, the same answer another tenant
        // gets: a read never confirms the existence of a row it may not show.
        if (!await AllowedAsync(db, scope, TosPermissions.BookingView, id, ct)) return TypedResults.NotFound();

        return await DetailAsync(db, clock, master, users, id, ct) is { } detail ? TypedResults.Ok(detail) : TypedResults.NotFound();
    }

    /// <summary>The booking's own branch must be one the caller's permission covers.</summary>
    private static async Task<bool> AllowedAsync(TosDbContext db, ICallerPermissions scope, string permission, Guid bookingId, CancellationToken ct)
    {
        if (scope.IsTenantWide(permission)) return true;

        var branchId = await db.Bookings.AsNoTracking().Where(b => b.BookingId == bookingId).Select(b => (Guid?)b.BranchId).SingleOrDefaultAsync(ct);
        return branchId is { } b && scope.HasAt(permission, b);
    }

    // ── create / update ─────────────────────────────────────────────────────

    private static async Task<Results<CreatedAtRoute<BookingDetailResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SaveBookingRequest request, TosDbContext db, IMasterDataReferences master, IUserDirectory users, BranchClock clock, ITenantContext caller,
        ICallerPermissions scope, HttpContext http, CancellationToken ct)
    {
        // A repeated request (double-click, retry after a lost answer) carries the same
        // Idempotency-Key: it gets the booking the first one made, never a second.
        var (key, badKey) = Idempotency.KeyOf(http.Request);
        if (badKey is not null) return TosSupport.Invalid(Idempotency.Header, badKey);
        var hash = key is null ? null : Idempotency.HashOf(request);
        if (key is not null && await ReplayAsync(db, clock, master, users, key, hash!, ct) is { } replay) return replay;

        var errors = new Dictionary<string, List<string>>();
        var header = await ResolveHeaderAsync(db, master, clock, request, errors, ct);
        // Vector BookingEntry.cs:1773: a BLIND GATE IN booking is made by the gate, never by hand.
        if (string.Equals(request.OrderTypeCode.Clean(), BlindGateIn, StringComparison.Ordinal))
            errors.Add("orderTypeCode", $"{BlindGateIn} bookings are made by the gate itself, not by hand.");

        // 403, not 404: the caller named the branch, so refusing it by name tells them
        // nothing they did not already type.
        if (header is not null && !scope.HasAt(TosPermissions.BookingManage, header.Branch.BranchId))
            return TosScope.OutsideYourBranches($"You cannot raise a booking at {header.Branch.BranchCode}.");
        if (header is not null && request.AllowLateGateIn == true && !scope.HasAt(TosPermissions.CutoffOverride, header.Branch.BranchId))
            return TosScope.OutsideYourBranches(LateGateNeedsOverride);

        // Header first (owner 2026-10-03): the clerk saves the header, gets the order number,
        // and adds requirement lines and boxes afterwards. No box goes on a booking without a line.
        var items = request.Requirements ?? [];
        var requirements = await ResolveRequirementsAsync(master, items, errors, ct);
        if (errors.Count > 0 || header is null) return TosSupport.Invalid(errors);
        if (await CarrierRefTakenAsync(db, header.Branch.BranchId, request.CarrierRef, null, ct) is { } taken) return taken;

        var tenantId = caller.TenantId();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var booking = new Booking
        {
            TenantId = tenantId,
            BranchId = header.Branch.BranchId,
            OrderNo = await TosNumberSeries.NextAsync(db, TosNumberSeries.Booking, header.Branch.BranchId, header.Branch.BranchCode, clock.LocalNow(header.Branch), ct),
            Status = BookingRules.Open,
            Source = "MANUAL",
            IdempotencyKey = key,
            IdempotencyHash = hash,
        };
        ApplyHeader(booking, request, header);
        if (request.AllowLateGateIn == true) SetLateGate(booking, true, caller);
        db.Bookings.Add(booking);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (key is not null && e.InnerException?.Message.Contains("uq_booking__idempotency_key") == true)
        {
            // The same key arrived twice at once and the other request won: answer with its booking.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return await ReplayAsync(db, clock, master, users, key, hash!, ct)
                   ?? TosSupport.Conflict($"A request with {Idempotency.Header} {key} is still being processed.", "Repeat it in a moment.");
        }

        short lineNo = 0;
        var created = new List<EquipmentRequirement>();
        foreach (var r in requirements)
        {
            lineNo = r.Item.LineNo ?? (short)(lineNo + 1);
            var entity = new EquipmentRequirement { TenantId = tenantId, BookingId = booking.BookingId, LineNo = lineNo };
            ApplyRequirement(entity, r);
            db.EquipmentRequirements.Add(entity);
            created.Add(entity);
        }
        if (created.Select(r => r.LineNo).Distinct().Count() != created.Count)
            return TosSupport.Invalid("requirements", "Two requirement lines share a line number.");
        await db.SaveChangesAsync(ct);

        if (request.Containers is { Count: > 0 } boxes)
        {
            var assigned = await AssignBoxesAsync(db, master, caller, booking, header.Plan, created, boxes, "PRE_ADVISED", partial: false, ct);
            if (assigned.Problem?.Invalid is { } invalid) return invalid;
            if (assigned.Problem?.Refused is { } refused) return refused;
        }

        await BookingEvents.QueueChangedAsync(db, booking.BookingId, "CREATED", DateTimeOffset.UtcNow, ct);
        await tx.CommitAsync(ct);
        return TypedResults.CreatedAtRoute((await DetailAsync(db, clock, master, users, booking.BookingId, ct))!, "GetBooking", new { id = booking.BookingId });
    }

    /// <summary>
    /// The booking an earlier request with this key created, answered as that request
    /// was (201, the booking as it stands now) — or a 422 when the key came back with
    /// a different body. Null when the key is new.
    /// </summary>
    private static async Task<Results<CreatedAtRoute<BookingDetailResponse>, ValidationProblem, ProblemHttpResult>?> ReplayAsync(
        TosDbContext db, BranchClock clock, IMasterDataReferences master, IUserDirectory users, string key, byte[] hash, CancellationToken ct)
    {
        var made = await db.Bookings.AsNoTracking().Where(b => b.IdempotencyKey == key)
            .Select(b => new { b.BookingId, b.IdempotencyHash }).SingleOrDefaultAsync(ct);
        if (made is null) return null;
        if (!Idempotency.SameRequest(made.IdempotencyHash, hash)) return Idempotency.DifferentRequest(key);
        return TypedResults.CreatedAtRoute((await DetailAsync(db, clock, master, users, made.BookingId, ct))!, "GetBooking", new { id = made.BookingId });
    }

    /// <summary>
    /// One live booking per carrier reference at a depot: a line's booking number or D/O
    /// number names ONE release. A second booking on it is a duplicate however it was
    /// raised, so it is a 409 that names the booking already there. A CANCELLED
    /// booking frees its reference.
    /// </summary>
    private static async Task<ProblemHttpResult?> CarrierRefTakenAsync(
        TosDbContext db, Guid branchId, string? carrierRef, Guid? exceptBookingId, CancellationToken ct)
    {
        if (carrierRef.Clean() is not { } reference) return null;
        var existing = await db.Bookings.AsNoTracking()
            .Where(b => b.BranchId == branchId && b.CarrierRef == reference && b.Status != BookingRules.Cancelled && b.BookingId != exceptBookingId)
            .OrderBy(b => b.CreatedAt).Select(b => new { b.BookingId, b.OrderNo, b.Status }).FirstOrDefaultAsync(ct);
        return existing is null ? null : TypedResults.Problem(
            title: $"That booking already exists: {existing.OrderNo}.",
            detail: $"Carrier reference {reference} is already on {existing.OrderNo} ({existing.Status}) at this depot. Open that booking instead of raising another.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>
            {
                ["existingOrderNo"] = existing.OrderNo, ["existingBookingId"] = existing.BookingId, ["carrierRef"] = reference,
            });
    }

    private static async Task<Results<Ok<BookingDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, SaveBookingRequest request, TosDbContext db, IMasterDataReferences master, IUserDirectory users, BranchClock clock,
        ITenantContext caller, ICallerPermissions scope, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, id, ct);
        var booking = await db.Bookings.SingleOrDefaultAsync(b => b.BookingId == id, ct);
        if (booking is null || !scope.HasAt(TosPermissions.BookingManage, booking.BranchId)) return TypedResults.NotFound();
        if (booking.Status != BookingRules.Open) return NotOpen(booking);
        if (!db.TrySetExpectedVersion(booking, request.RowVersion))
            return TosSupport.Invalid("rowVersion", "Send the rowVersion you received when reading the booking.");

        var errors = new Dictionary<string, List<string>>();
        if (request.Requirements is { Count: > 0 } || request.Containers is { Count: > 0 })
            errors.Add("requirements", "PUT changes the header only. Use …/requirements and …/containers.");
        if (request.BranchId != booking.BranchId)
            errors.Add("branchId", $"The depot cannot change — {booking.OrderNo} carries it. Cancel and re-book at the other depot.");

        var hasBoxes = await db.BookingContainers.AnyAsync(x => x.BookingId == id && x.EndReason != "UNASSIGNED", ct);
        if (hasBoxes && !string.Equals(request.OrderTypeCode.Clean(), booking.OrderTypeCode, StringComparison.Ordinal))
            errors.Add("orderTypeCode", "Boxes are already on this booking with the current order type's steps. Unassign them before changing the order type.");
        if (hasBoxes && !string.Equals(request.LineCode.Clean(), booking.LinePartyCode, StringComparison.Ordinal))
            errors.Add("lineCode", "Boxes are already on this booking under the current line. Unassign them before changing the line.");

        var header = await ResolveHeaderAsync(db, master, clock, request, errors, ct);
        if (errors.Count > 0 || header is null) return TosSupport.Invalid(errors);
        if (await CarrierRefTakenAsync(db, booking.BranchId, request.CarrierRef, booking.BookingId, ct) is { } taken) return taken;
        if (request.AllowLateGateIn is { } late && late != booking.AllowLateGateIn)
        {
            if (!scope.HasAt(TosPermissions.CutoffOverride, booking.BranchId)) return TosScope.OutsideYourBranches(LateGateNeedsOverride);
            SetLateGate(booking, late, caller);
        }

        ApplyHeader(booking, request, header);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await BookingEvents.QueueChangedAsync(db, id, "HEADER_CHANGED", DateTimeOffset.UtcNow, ct);
        await tx.CommitAsync(ct);
        return TypedResults.Ok((await DetailAsync(db, clock, master, users, id, ct))!);
    }

    /// <summary>
    /// Lines are matched by number, not replaced wholesale: boxes point at their
    /// requirement row, so a line with boxes on it can change quantity but not
    /// type, and cannot disappear.
    /// </summary>
    private static async Task<Results<Ok<BookingDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> ReplaceRequirementsAsync(
        Guid id, ReplaceRequirementsRequest request, TosDbContext db, IMasterDataReferences master, IUserDirectory users, BranchClock clock,
        ICallerPermissions scope, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, id, ct);
        var booking = await db.Bookings.SingleOrDefaultAsync(b => b.BookingId == id, ct);
        if (booking is null || !scope.HasAt(TosPermissions.BookingManage, booking.BranchId)) return TypedResults.NotFound();
        if (booking.Status != BookingRules.Open) return NotOpen(booking);
        if (!db.TrySetExpectedVersion(booking, request.RowVersion))
            return TosSupport.Invalid("rowVersion", "Send the rowVersion you received when reading the booking.");

        var errors = new Dictionary<string, List<string>>();
        var wanted = await ResolveRequirementsAsync(master, request.Requirements, errors, ct);
        var existing = await db.EquipmentRequirements.Where(r => r.BookingId == id).ToListAsync(ct);
        var used = await UsedPerLineAsync(db, id, ct);

        var nextNo = (short)(existing.Select(r => (int)r.LineNo).DefaultIfEmpty(0).Max() + 1);
        var numbered = wanted.Select(w => (Resolved: w, LineNo: w.Item.LineNo ?? nextNo++)).ToList();
        if (numbered.Select(n => n.LineNo).Distinct().Count() != numbered.Count)
            errors.Add("requirements", "Two requirement lines share a line number.");

        for (var i = 0; i < numbered.Count; i++)
        {
            var (resolved, lineNo) = numbered[i];
            var current = existing.SingleOrDefault(e => e.LineNo == lineNo);
            var onLine = current is null ? 0 : used.GetValueOrDefault(current.EquipmentRequirementId);
            if (current is not null && onLine > 0 && resolved.Type is not null
                && !string.Equals(current.EquipmentTypeCode, resolved.Type.TypeCode, StringComparison.OrdinalIgnoreCase))
                errors.Add($"requirements[{i}].equipmentTypeCode", $"Line {lineNo} has {onLine} box(es) of type {current.EquipmentTypeCode} on it; the type cannot change.");
            if (BookingRules.CannotSetQty(resolved.Item.Qty, onLine) is { } qtyProblem)
                errors.Add($"requirements[{i}].qty", qtyProblem);
        }
        foreach (var gone in existing.Where(e => numbered.All(n => n.LineNo != e.LineNo)))
            if (used.GetValueOrDefault(gone.EquipmentRequirementId) > 0)
                errors.Add("requirements", $"Line {gone.LineNo} ({gone.EquipmentTypeCode}) has boxes on it and cannot be removed.");
        if (errors.Count > 0) return TosSupport.Invalid(errors);

        foreach (var gone in existing.Where(e => numbered.All(n => n.LineNo != e.LineNo)))
            db.EquipmentRequirements.Remove(gone);
        foreach (var (resolved, lineNo) in numbered)
        {
            var entity = existing.SingleOrDefault(e => e.LineNo == lineNo);
            if (entity is null)
            {
                entity = new EquipmentRequirement { TenantId = booking.TenantId, BookingId = id, LineNo = lineNo };
                db.EquipmentRequirements.Add(entity);
            }
            ApplyRequirement(entity, resolved);
        }
        booking.UpdatedAt = DateTimeOffset.UtcNow;   // bumps the booking's rowVersion: its lines are part of it

        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await BookingEvents.QueueChangedAsync(db, id, "REQUIREMENTS_CHANGED", booking.UpdatedAt, ct);
        await tx.CommitAsync(ct);
        return TypedResults.Ok((await DetailAsync(db, clock, master, users, id, ct))!);
    }

    // ── boxes ───────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<BookingDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> AssignAsync(
        Guid id, AssignContainersRequest request, TosDbContext db, IMasterDataReferences master, IUserDirectory users, BranchClock clock,
        ITenantContext caller, ICallerPermissions scope, CancellationToken ct)
    {
        // Under the lock: the booking is still OPEN, its order type and lines are the
        // ones these boxes are checked against, and no one else is filling the same line.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, id, ct);
        var booking = await db.Bookings.SingleOrDefaultAsync(b => b.BookingId == id, ct);
        if (booking is null || !scope.HasAt(TosPermissions.BookingManage, booking.BranchId)) return TypedResults.NotFound();
        if (booking.Status != BookingRules.Open) return NotOpen(booking);

        var plan = (await master.OrderTypePlansAsync([booking.OrderTypeCode], ct)).GetValueOrDefault(booking.OrderTypeCode);
        if (plan is null) return TosSupport.Conflict($"Order type {booking.OrderTypeCode} no longer exists in master data.");
        var requirements = await db.EquipmentRequirements.Where(r => r.BookingId == id).ToListAsync(ct);

        var assigned = await AssignBoxesAsync(db, master, caller, booking, plan, requirements, request.Containers, request.Source, partial: false, ct);
        if (assigned.Problem?.Invalid is { } invalid) return invalid;
        if (assigned.Problem?.Refused is { } refused) return refused;
        if (assigned.Created.Count > 0) await BookingEvents.QueueChangedAsync(db, id, "CONTAINERS_ASSIGNED", DateTimeOffset.UtcNow, ct);
        await tx.CommitAsync(ct);

        return TypedResults.Ok((await DetailAsync(db, clock, master, users, id, ct))!);
    }

    /// <summary>
    /// The 100-box entry (BOOKING_ENTRY_GAP_ANALYSIS P2): the UI sends rows as they are keyed,
    /// each with its clientLineId. Every row stands alone — a typo rejects that row, not the
    /// other 99 — and a row resent after a dropped answer comes back REPLAYED, never twice.
    /// The answer is lean: what happened to each row and how full each line is.
    /// </summary>
    private static async Task<Results<Ok<ContainerBatchResponse>, NotFound, ValidationProblem, ProblemHttpResult>> AssignBatchAsync(
        Guid id, AssignContainersBatchRequest request, TosDbContext db, IMasterDataReferences master,
        ITenantContext caller, ICallerPermissions scope, CancellationToken ct)
    {
        var noId = request.Containers.Select((c, i) => (c, i)).Where(x => x.c.ClientLineId is null).Select(x => x.i).ToList();
        if (noId.Count > 0)
            return TosSupport.Invalid(noId.ToDictionary(i => $"containers[{i}].clientLineId",
                _ => new List<string> { "Every row needs its clientLineId (a UUID the UI makes when the row is keyed)." }));

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, id, ct);
        var booking = await db.Bookings.SingleOrDefaultAsync(b => b.BookingId == id, ct);
        if (booking is null || !scope.HasAt(TosPermissions.BookingManage, booking.BranchId)) return TypedResults.NotFound();
        if (booking.Status != BookingRules.Open) return NotOpen(booking);

        var plan = (await master.OrderTypePlansAsync([booking.OrderTypeCode], ct)).GetValueOrDefault(booking.OrderTypeCode);
        if (plan is null) return TosSupport.Conflict($"Order type {booking.OrderTypeCode} no longer exists in master data.");
        var requirements = await db.EquipmentRequirements.Where(r => r.BookingId == id).OrderBy(r => r.LineNo).ToListAsync(ct);

        var assigned = await AssignBoxesAsync(db, master, caller, booking, plan, requirements, request.Containers, request.Source, partial: true, ct);
        if (assigned.Problem?.Invalid is { } invalid) return invalid;
        if (assigned.Problem?.Refused is { } refused) return refused;
        if (assigned.Created.Count > 0) await BookingEvents.QueueChangedAsync(db, id, "CONTAINERS_ASSIGNED", DateTimeOffset.UtcNow, ct);
        await tx.CommitAsync(ct);

        var lineNoOf = requirements.ToDictionary(r => r.EquipmentRequirementId, r => r.LineNo);
        var touched = assigned.Created.Concat(assigned.Replayed).Select(x => x.Box.BookingContainerId).ToList();
        var steps = (await db.MovementPlans.AsNoTracking().Where(m => touched.Contains(m.BookingContainerId)).OrderBy(m => m.SequenceNo).ToListAsync(ct))
            .ToLookup(m => m.BookingContainerId);
        var active = await db.BookingContainers.AsNoTracking().Where(x => x.BookingId == id && x.EndedAt == null)
            .GroupBy(x => x.EquipmentRequirementId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        var created = assigned.Created.ToDictionary(x => x.Index, x => x.Box);
        var replayed = assigned.Replayed.ToDictionary(x => x.Index, x => x.Box);
        var items = request.Containers.Select((c, i) =>
        {
            var box = created.GetValueOrDefault(i) ?? replayed.GetValueOrDefault(i);
            return box is not null
                ? new ContainerBatchItemResponse(i, c.ClientLineId, box.ContainerNo, created.ContainsKey(i) ? "CREATED" : "REPLAYED",
                    ToResponse(box, lineNoOf.GetValueOrDefault(box.EquipmentRequirementId), steps[box.BookingContainerId]), null)
                : new ContainerBatchItemResponse(i, c.ClientLineId, c.ContainerNo, "REJECTED", null, assigned.ErrorsOf(i));
        }).ToList();

        return TypedResults.Ok(new ContainerBatchResponse(
            booking.BookingId, booking.OrderNo, created.Count, replayed.Count, items.Count(x => x.Outcome == "REJECTED"),
            requirements.Select(r => new LineFillResponse(r.LineNo, r.EquipmentTypeCode, r.Qty, active.GetValueOrDefault(r.EquipmentRequirementId))).ToList(),
            items));
    }

    /// <summary>
    /// Edit one line (G5/G6): its own row version, so a second clerk editing the same line gets
    /// a 409 instead of overwriting the first. A box that passed the gate keeps what the gate
    /// checked (Vector BookingEntry.cs:1272-1285 froze the same fields).
    /// </summary>
    private static async Task<Results<Ok<BookingContainerResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateLineAsync(
        Guid id, Guid bookingContainerId, UpdateContainerLineRequest request, TosDbContext db, IMasterDataReferences master,
        ICallerPermissions scope, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, id, ct);
        var booking = await db.Bookings.AsNoTracking().SingleOrDefaultAsync(b => b.BookingId == id, ct);
        var box = await db.BookingContainers.SingleOrDefaultAsync(x => x.BookingContainerId == bookingContainerId && x.BookingId == id, ct);
        if (booking is null || box is null || !scope.HasAt(TosPermissions.BookingManage, booking.BranchId)) return TypedResults.NotFound();
        if (booking.Status != BookingRules.Open) return NotOpen(booking);
        if (box.EndedAt is not null) return TosSupport.Conflict($"{box.ContainerNo} has left this booking ({box.EndReason}); its line can no longer be edited.");
        if (!db.TrySetExpectedVersion(box, request.RowVersion))
            return TosSupport.Invalid("rowVersion", "Send the line's rowVersion you received when reading the booking.");

        var line = await db.EquipmentRequirements.AsNoTracking().SingleAsync(r => r.EquipmentRequirementId == box.EquipmentRequirementId, ct);
        var steps = await db.MovementPlans.AsNoTracking().Where(m => m.BookingContainerId == bookingContainerId).OrderBy(m => m.SequenceNo).ToListAsync(ct);
        var started = steps.Any(s => s.Status == "DONE");

        var errors = new Dictionary<string, List<string>>();

        // Nominating the box (owner 2026-10-04): the same checks as assigning one by number.
        var nominate = ContainerNumber.Normalise(request.ContainerNo ?? "") is { Length: > 0 } wanted && wanted != box.ContainerNo ? wanted : null;
        ContainerRef? nominatedBox = null;
        var nominatedDigitOk = false;
        if (nominate is not null)
        {
            if (started)
                errors.Add("containerNo", $"{box.ContainerNo} has already passed the gate on this booking; the box can no longer change.");
            else if (!ContainerNumber.IsWellFormed(nominate))
                errors.Add("containerNo", $"'{request.ContainerNo}' is not a container number (4 letters ending U/J/Z, 7 digits).");
            else
            {
                nominatedDigitOk = ContainerNumber.IsValid(nominate);
                if (!nominatedDigitOk && await master.GetBoolSettingAsync(TosSettingKeys.EnforceCheckDigit, booking.BranchId, true, ct))
                    errors.Add("containerNo", $"{nominate} fails the ISO 6346 check digit (expected {ContainerNumber.CheckDigitOf(nominate)}).");
                nominatedBox = (await master.ContainersAsync([nominate], ct)).GetValueOrDefault(nominate);
                if (nominatedBox is null && !await master.GetBoolSettingAsync(TosSettingKeys.AllowUnknownContainer, booking.BranchId, true, ct))
                    errors.Add("containerNo", $"{nominate} is not in the container registry, and this depot does not accept unknown boxes.");
                if (BookingRules.CannotAssign(line.Qty, 0, line.EquipmentTypeCode, nominatedBox?.EquipmentTypeCode) is { } wrongType)
                    errors.Add("containerNo", $"{nominate}: {wrongType}");
                var elsewhere = await (
                    from x in db.BookingContainers.AsNoTracking()
                    join b in db.Bookings on x.BookingId equals b.BookingId
                    where x.ContainerNo == nominate && x.EndedAt == null && x.BookingContainerId != box.BookingContainerId
                    select b.OrderNo).FirstOrDefaultAsync(ct);
                if (elsewhere is not null)
                    errors.Add("containerNo", $"{nominate} is active on booking {elsewhere}. A box is on one booking at a time.");
            }
        }

        if (request.DeclaredVgmKg is <= 0) errors.Add("declaredVgmKg", "A weight is positive; leave it out if unknown.");
        if (request.DeclaredVolumeCbm is <= 0) errors.Add("declaredVolumeCbm", "A volume is positive; leave it out if unknown.");
        var cargoCode = request.CargoCategoryCode.Clean();
        if (cargoCode is not null && !(await master.CodeListValuesAsync("CARGO_CATEGORY", [cargoCode], ct)).Contains(cargoCode))
            errors.Add("cargoCategoryCode", $"'{cargoCode}' is not a CARGO_CATEGORY of this depot.");
        if (request.ReeferSetTempC is not null && line.ReeferSetTempC is null)
            errors.Add("reeferSetTempC", $"Line {line.LineNo} ({line.EquipmentTypeCode}) is not a reefer line; a set temperature makes no sense on it.");
        if (request.ReeferSetTempC is < -70 or > 40) errors.Add("reeferSetTempC", "Between −70 °C and +40 °C.");
        if (request.ReeferVentPct is < 0 or > 100) errors.Add("reeferVentPct", "Between 0 and 100 %.");
        if (request.ReeferHumidityPct is < 0 or > 100) errors.Add("reeferHumidityPct", "Between 0 and 100 %.");
        var imdg = request.ImdgClass.Clean();
        var un = request.UnNumber?.Trim() is { Length: > 0 } u ? u : null;
        if (un is not null && (un.Length != 4 || !un.All(char.IsAsciiDigit))) errors.Add("unNumber", "A UN number is four digits, e.g. 1203.");
        else if (un is not null && imdg is null) errors.Add("imdgClass", "A UN number needs its IMDG class.");
        var mode = request.HandoverMode.Clean();
        if (mode is not null && mode != box.HandoverModeCode && BookingRules.HandoverModeProblem(booking.DirectionCode, mode) is { } wrongList)
            errors.Add("handoverMode", wrongList);

        var seal = request.DeclaredSealNo.Clean();
        var customerSeal = request.CustomerSealNo.Clean();
        if (started)
            foreach (var (field, before, after) in new (string, object?, object?)[]
                     {
                         ("declaredSealNo", box.DeclaredSealNo, seal), ("customerSealNo", box.CustomerSealNo, customerSeal),
                         ("cargoCategoryCode", box.CargoCategoryCode, cargoCode), ("imdgClass", box.ImdgClass, imdg),
                         ("unNumber", box.UnNumber, un), ("requiredDate", box.RequiredDate, request.RequiredDate),
                         ("handoverMode", box.HandoverModeCode, mode),
                     })
                if (!Equals(before, after))
                    errors.Add(field, $"{box.ContainerNo} has already passed the gate on this booking; this can no longer change.");
        if (errors.Count > 0) return TosSupport.Invalid(errors);

        if (nominate is not null)
        {
            box.ContainerNo = nominate;
            box.ContainerId = nominatedBox?.ContainerId;
            box.IsCheckDigitValid = nominatedDigitOk;
        }
        box.DeclaredSealNo = seal;
        box.CustomerSealNo = customerSeal;
        box.DeclaredVgmKg = request.DeclaredVgmKg;
        box.DeclaredVolumeCbm = request.DeclaredVolumeCbm;
        box.RequiredDate = request.RequiredDate;
        box.CargoCategoryCode = cargoCode;
        box.ImdgClass = imdg;
        box.UnNumber = un;
        box.ReeferSetTempC = request.ReeferSetTempC;
        box.ReeferVentPct = request.ReeferVentPct;
        box.ReeferHumidityPct = request.ReeferHumidityPct;
        box.StowageCode = request.StowageCode.Clean();
        box.StowageNo = string.IsNullOrWhiteSpace(request.StowageNo) ? null : request.StowageNo.Trim();
        box.IsPreCool = request.IsPreCool;
        box.Remarks = string.IsNullOrWhiteSpace(request.Remarks) ? null : request.Remarks.Trim();
        box.HandoverModeCode = mode;

        try
        {
            if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        }
        catch (DbUpdateException e) when (e.InnerException?.Message.Contains("uq_booking_container__active") == true)
        {
            return TosSupport.Conflict($"{nominate} was put on another booking a moment ago.", "Reload the booking.");
        }
        await BookingEvents.QueueChangedAsync(db, id, "CONTAINER_CHANGED", DateTimeOffset.UtcNow, ct);
        await tx.CommitAsync(ct);
        return TypedResults.Ok(ToResponse(box, line.LineNo, steps));
    }

    private static async Task<Results<Ok<BookingDetailResponse>, NotFound, ProblemHttpResult>> UnassignAsync(
        Guid id, Guid bookingContainerId, TosDbContext db, IMasterDataReferences master, IUserDirectory users, BranchClock clock,
        ITenantContext caller, ICallerPermissions scope, TimeProvider time, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, id, ct);
        var booking = await db.Bookings.AsNoTracking().SingleOrDefaultAsync(b => b.BookingId == id, ct);
        var box = await db.BookingContainers.SingleOrDefaultAsync(x => x.BookingContainerId == bookingContainerId && x.BookingId == id, ct);
        if (booking is null || box is null || !scope.HasAt(TosPermissions.BookingManage, booking.BranchId)) return TypedResults.NotFound();
        if (box.EndedAt is not null) return TosSupport.Conflict($"{box.ContainerNo} already left this booking ({box.EndReason}).");

        var steps = await db.MovementPlans.Where(m => m.BookingContainerId == bookingContainerId).ToListAsync(ct);
        if (steps.Any(s => s.Status == "DONE"))
            return TosSupport.Conflict($"{box.ContainerNo} has already passed the gate on this booking.",
                "A box with history stays on the booking; close the booking when the work is over.");

        End(box, "UNASSIGNED", steps, caller, time);
        // The gate may complete a step of this box at the same moment: the row versions decide, and the loser gets a 409.
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await BookingEvents.QueueChangedAsync(db, id, "CONTAINER_UNASSIGNED", time.GetUtcNow(), ct);
        await tx.CommitAsync(ct);
        return TypedResults.Ok((await DetailAsync(db, clock, master, users, id, ct))!);
    }

    // ── cancel / close ──────────────────────────────────────────────────────

    private static async Task<Results<Ok<BookingDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> CancelAsync(
        Guid id, EndBookingRequest request, TosDbContext db, IMasterDataReferences master, IUserDirectory users, BranchClock clock,
        ITenantContext caller, ICallerPermissions scope, TimeProvider time, CancellationToken ct) =>
        await EndAsync(id, request, db, master, users, clock, caller, scope, time, cancel: true, ct);

    private static async Task<Results<Ok<BookingDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> CloseAsync(
        Guid id, EndBookingRequest request, TosDbContext db, IMasterDataReferences master, IUserDirectory users, BranchClock clock,
        ITenantContext caller, ICallerPermissions scope, TimeProvider time, CancellationToken ct) =>
        await EndAsync(id, request, db, master, users, clock, caller, scope, time, cancel: false, ct);

    private static async Task<Results<Ok<BookingDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> EndAsync(
        Guid id, EndBookingRequest request, TosDbContext db, IMasterDataReferences master, IUserDirectory users, BranchClock clock,
        ITenantContext caller, ICallerPermissions scope, TimeProvider time, bool cancel, CancellationToken ct)
    {
        // Cancelling needs tos.booking.cancel, closing needs manage — the row check
        // uses the same permission the endpoint asked for, at the booking's branch.
        var permission = cancel ? TosPermissions.BookingCancel : TosPermissions.BookingManage;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, id, ct);
        var booking = await db.Bookings.SingleOrDefaultAsync(b => b.BookingId == id, ct);
        if (booking is null || !scope.HasAt(permission, booking.BranchId)) return TypedResults.NotFound();
        if (booking.Status != BookingRules.Open) return NotOpen(booking);
        if (!db.TrySetExpectedVersion(booking, request.RowVersion))
            return TosSupport.Invalid("rowVersion", "Send the rowVersion you received when reading the booking.");

        var boxes = await db.BookingContainers.Where(x => x.BookingId == id).ToListAsync(ct);
        var boxIds = boxes.Select(b => b.BookingContainerId).ToList();
        var steps = await db.MovementPlans.Where(m => boxIds.Contains(m.BookingContainerId)).ToListAsync(ct);

        if (cancel && BookingRules.CannotCancel(booking.Status, steps.Count(s => s.Status == "DONE")) is { } refusal)
            return TosSupport.Conflict(refusal);

        var now = time.GetUtcNow();
        var reason = request.Reason.Trim();
        if (cancel)
        {
            booking.Status = BookingRules.Cancelled;
            booking.CancelledAt = now;
            booking.CancelledBy = caller.UserId();
            booking.CancelReason = reason;
        }
        else
        {
            booking.Status = BookingRules.Closed;
            booking.ClosedAt = now;
            booking.ClosedBy = caller.UserId();
            booking.CloseReason = reason;
        }

        // Open boxes are released so they can go onto another booking; a box that
        // finished keeps its COMPLETED record.
        foreach (var box in boxes.Where(b => b.EndedAt is null))
            End(box, cancel ? "BOOKING_CANCELLED" : "BOOKING_CLOSED", steps.Where(s => s.BookingContainerId == box.BookingContainerId), caller, time);

        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await BookingEvents.QueueChangedAsync(db, id, cancel ? "CANCELLED" : "CLOSED", now, ct);
        await tx.CommitAsync(ct);
        return TypedResults.Ok((await DetailAsync(db, clock, master, users, id, ct))!);
    }

    /// <summary>
    /// Serialises every change to ONE booking's header, lines and boxes. The database
    /// reads under READ_COMMITTED_SNAPSHOT, where nothing waits for anything: two clerks
    /// filling the last place on a line would both see it free, and a box could land on
    /// a booking being cancelled. An update lock on the booking row, taken first in the
    /// transaction, makes the second writer wait for the first to commit, and every read
    /// after it sees that commit. Other bookings are not affected.
    /// </summary>
    private static Task LockAsync(TosDbContext db, Guid bookingId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM booking.booking WITH (UPDLOCK, HOLDLOCK) WHERE booking_id = {bookingId}", ct);

    private static void End(BookingContainer box, string reason, IEnumerable<MovementPlan> steps, ITenantContext caller, TimeProvider time)
    {
        box.EndedAt = time.GetUtcNow();
        box.EndedBy = caller.UserId();
        box.EndReason = reason;
        foreach (var step in steps.Where(s => s.Status == "PENDING"))
            step.Status = "CANCELLED";
    }

    // ── assignment (§5.1) ───────────────────────────────────────────────────

    /// <summary>What an assignment did: the problem to send back (all-or-nothing), or per item what happened.</summary>
    private sealed record AssignResult(
        Problem? Problem,
        List<(int Index, BookingContainer Box)> Created,
        List<(int Index, BookingContainer Box)> Replayed,
        Dictionary<string, List<string>> Errors)
    {
        public static AssignResult Failed(Problem problem) => new(problem, [], [], []);

        /// <summary>The errors of item <paramref name="index"/>, keyed by its field ("containerNo", "lineNo"...).</summary>
        public Dictionary<string, string[]> ErrorsOf(int index)
        {
            var prefix = $"containers[{index}]";
            return Errors.Where(e => e.Key == prefix || e.Key.StartsWith(prefix + ".", StringComparison.Ordinal))
                .ToDictionary(e => e.Key == prefix ? "" : e.Key[(prefix.Length + 1)..], e => e.Value.ToArray());
        }
    }

    /// <summary>
    /// Assigns boxes. <paramref name="partial"/> false: every box or none (a problem comes
    /// back). True: each box stands alone — the good ones are added, the bad ones are
    /// reported per item. A box whose <see cref="AssignContainerItem.ClientLineId"/> this
    /// booking already has is REPLAYED (a retry after a lost answer), never added twice.
    /// Runs inside the caller's transaction, under the booking lock; the filtered unique
    /// index on active container numbers is the backstop against another booking.
    /// </summary>
    private static async Task<AssignResult> AssignBoxesAsync(
        TosDbContext db, IMasterDataReferences master, ITenantContext caller, Booking booking, OrderTypePlanRef plan,
        IReadOnlyList<EquipmentRequirement> requirements, IReadOnlyList<AssignContainerItem> items, string source, bool partial, CancellationToken ct)
    {
        var errors = new Dictionary<string, List<string>>();
        var steps = BookingRules.PlanFor(plan);
        if (steps.Count == 0)
            return AssignResult.Failed(Problem.Of(TosSupport.Conflict($"Order type {plan.OrderTypeCode} has no steps in master data — the gate would not know what to do with a box.")));
        if (requirements.Count == 0)
            return AssignResult.Failed(Problem.Of(TosSupport.Conflict($"{booking.OrderNo} has no requirement lines yet.",
                "Add what equipment and how many (PUT …/requirements) before assigning boxes.")));

        var numbers = items.Select(i => ContainerNumber.Normalise(i.ContainerNo ?? "")).ToList();
        var registry = await master.ContainersAsync(numbers.Where(ContainerNumber.IsWellFormed), ct);
        var allowUnknown = await master.GetBoolSettingAsync(TosSettingKeys.AllowUnknownContainer, booking.BranchId, true, ct);
        var enforceDigit = await master.GetBoolSettingAsync(TosSettingKeys.EnforceCheckDigit, booking.BranchId, true, ct);

        var clientIds = items.Select(i => i.ClientLineId).OfType<Guid>().ToList();
        var already = clientIds.Count == 0 ? []
            : await db.BookingContainers.Where(x => x.BookingId == booking.BookingId && x.ClientLineId != null && clientIds.Contains(x.ClientLineId.Value))
                .ToDictionaryAsync(x => x.ClientLineId!.Value, ct);

        var activeElsewhere = await (
            from x in db.BookingContainers
            join b in db.Bookings on x.BookingId equals b.BookingId
            where x.ContainerNo != null && numbers.Contains(x.ContainerNo) && x.EndedAt == null
            select new { x.ContainerNo, b.OrderNo, x.BookingId, x.ClientLineId }).ToListAsync(ct);

        // Where each box stands now, for the checks a handover mode switches on (Vector BookingEntry.cs:626-655).
        var inYard = items.Any(i => i.HandoverMode.Clean() is not null)
            ? await db.ContainerVisits.AsNoTracking()
                .Where(v => numbers.Contains(v.ContainerNo) && v.GateOutTransactionId == null && v.BranchId == booking.BranchId)
                .Select(v => new { v.ContainerNo, v.FullEmpty }).ToListAsync(ct)
            : [];

        var cargoCodes = items.Select(i => i.CargoCategoryCode.Clean()).OfType<string>().Distinct().ToList();
        var cargo = cargoCodes.Count == 0 ? new HashSet<string>() : (await master.CodeListValuesAsync("CARGO_CATEGORY", cargoCodes, ct)).ToHashSet();

        var used = await UsedPerLineAsync(db, booking.BookingId, ct);
        var adding = new Dictionary<Guid, int>();
        var toAdd = new List<(int Index, BookingContainer Box)>();
        var replayed = new List<(int Index, BookingContainer Box)>();
        var seenClientIds = new HashSet<Guid>();

        for (var i = 0; i < items.Count; i++)
        {
            var key = $"containers[{i}]";
            var no = numbers[i];
            var item = items[i];
            bool Failed() => errors.Keys.Any(k => k == key || k.StartsWith(key + ".", StringComparison.Ordinal));

            // A retry of a line this booking already has: answer with that line.
            if (item.ClientLineId is { } clientId)
            {
                if (!seenClientIds.Add(clientId)) { errors.Add($"{key}.clientLineId", "This clientLineId is listed twice in the request."); continue; }
                if (already.TryGetValue(clientId, out var prior))
                {
                    if ((prior.ContainerNo ?? "") != no)
                        errors.Add($"{key}.clientLineId", $"This clientLineId is already line {prior.ContainerNo} on this booking; a new row needs a new id.");
                    else
                        replayed.Add((i, prior));
                    continue;
                }
            }

            // No number yet (owner 2026-10-04): the row is the promise of a box of the line's type.
            var nominated = no.Length > 0;
            var digitOk = false;
            ContainerRef? known = null;
            if (nominated)
            {
                if (!ContainerNumber.IsWellFormed(no)) { errors.Add($"{key}.containerNo", $"'{item.ContainerNo}' is not a container number (4 letters ending U/J/Z, 7 digits)."); continue; }
                if (numbers.Take(i).Contains(no)) { errors.Add($"{key}.containerNo", $"{no} is listed twice."); continue; }

                digitOk = ContainerNumber.IsValid(no);
                if (!digitOk && enforceDigit)
                    errors.Add($"{key}.containerNo", $"{no} fails the ISO 6346 check digit (expected {ContainerNumber.CheckDigitOf(no)}). Check the number — a misread box is a box released to the wrong truck.");

                known = registry.GetValueOrDefault(no);
                if (known is null && !allowUnknown)
                    errors.Add($"{key}.containerNo", $"{no} is not in the container registry, and this depot does not accept unknown boxes.");

                if (activeElsewhere.FirstOrDefault(a => a.ContainerNo == no) is { } other)
                {
                    errors.Add($"{key}.containerNo", other.BookingId == booking.BookingId
                        ? $"{no} is already on this booking."
                        : $"{no} is active on booking {other.OrderNo}. A box is on one booking at a time.");
                    continue;
                }
            }

            // Which line: the one named, else the only line of the box's type, else the only line.
            var candidates = item.LineNo is { } n ? requirements.Where(r => r.LineNo == n).ToList()
                : known?.EquipmentTypeCode is { } t && requirements.Count(r => string.Equals(r.EquipmentTypeCode, t, StringComparison.OrdinalIgnoreCase)) == 1
                    ? requirements.Where(r => string.Equals(r.EquipmentTypeCode, t, StringComparison.OrdinalIgnoreCase)).ToList()
                : requirements.Count == 1 ? requirements.ToList() : [];
            if (candidates.Count != 1)
            {
                errors.Add($"{key}.lineNo", item.LineNo is null
                    ? "Say which requirement line this box fills — more than one could fit."
                    : $"This booking has no line {item.LineNo}.");
                continue;
            }
            var line = candidates[0];
            var onLine = used.GetValueOrDefault(line.EquipmentRequirementId) + adding.GetValueOrDefault(line.EquipmentRequirementId);
            if (BookingRules.CannotAssign(line.Qty, onLine, line.EquipmentTypeCode, known?.EquipmentTypeCode) is { } refusal)
            {
                errors.Add($"{key}.containerNo", nominated ? $"{no}: {refusal}" : $"Line {line.LineNo}: {refusal}");
                continue;
            }

            if (item.DeclaredSealNo?.Length > 20) errors.Add($"{key}.declaredSealNo", "At most 20 characters.");
            if (item.CustomerSealNo?.Length > 20) errors.Add($"{key}.customerSealNo", "At most 20 characters.");
            if (item.DeclaredVgmKg is <= 0) errors.Add($"{key}.declaredVgmKg", "A weight is positive; leave it out if unknown.");
            if (item.DeclaredVolumeCbm is <= 0) errors.Add($"{key}.declaredVolumeCbm", "A volume is positive; leave it out if unknown.");

            var cargoCode = item.CargoCategoryCode.Clean();
            if (cargoCode is not null && !cargo.Contains(cargoCode))
                errors.Add($"{key}.cargoCategoryCode", $"'{cargoCode}' is not a CARGO_CATEGORY of this depot.");

            // Reefer and DG details: the box's own, else the line's (owner Q2: the line holds the defaults).
            var setTemp = item.ReeferSetTempC ?? line.ReeferSetTempC;
            if (item.ReeferSetTempC is not null && line.ReeferSetTempC is null)
                errors.Add($"{key}.reeferSetTempC", $"Line {line.LineNo} ({line.EquipmentTypeCode}) is not a reefer line; a set temperature makes no sense on it.");
            if (item.ReeferSetTempC is < -70 or > 40) errors.Add($"{key}.reeferSetTempC", "Between −70 °C and +40 °C.");
            if (item.ReeferVentPct is < 0 or > 100) errors.Add($"{key}.reeferVentPct", "Between 0 and 100 %.");
            if (item.ReeferHumidityPct is < 0 or > 100) errors.Add($"{key}.reeferHumidityPct", "Between 0 and 100 %.");
            var imdg = item.ImdgClass.Clean() ?? line.ImdgClass;
            var un = item.UnNumber?.Trim() is { Length: > 0 } u ? u : line.UnNumber;
            if (item.UnNumber?.Trim() is { Length: > 0 } typedUn && (typedUn.Length != 4 || !typedUn.All(char.IsAsciiDigit)))
                errors.Add($"{key}.unNumber", "A UN number is four digits, e.g. 1203.");
            else if (un is not null && imdg is null) errors.Add($"{key}.imdgClass", "A UN number needs its IMDG class.");

            var mode = item.HandoverMode.Clean();
            if (mode is not null)
            {
                if (BookingRules.HandoverModeProblem(booking.DirectionCode, mode) is { } wrongList)
                    errors.Add($"{key}.handoverMode", wrongList);
                else if (nominated && BookingRules.HandoverRefusal(booking.DirectionCode, mode, inYard.Any(v => v.ContainerNo == no),
                             inYard.FirstOrDefault(v => v.ContainerNo == no)?.FullEmpty) is { } refused)
                    errors.Add($"{key}.containerNo", $"{no} {refused}");
            }

            if (Failed()) continue;
            // Only a box that passed every check holds a place on its line.
            adding[line.EquipmentRequirementId] = adding.GetValueOrDefault(line.EquipmentRequirementId) + 1;

            toAdd.Add((i, new BookingContainer
            {
                TenantId = booking.TenantId,
                BookingId = booking.BookingId,
                EquipmentRequirementId = line.EquipmentRequirementId,
                ContainerNo = nominated ? no : null,
                ContainerId = known?.ContainerId,
                IsCheckDigitValid = digitOk,
                AssignedBy = caller.UserId(),
                AssignmentSource = source,
                ClientLineId = item.ClientLineId,
                DeclaredSealNo = item.DeclaredSealNo.Clean(),
                CustomerSealNo = item.CustomerSealNo.Clean(),
                DeclaredVgmKg = item.DeclaredVgmKg,
                DeclaredVolumeCbm = item.DeclaredVolumeCbm,
                RequiredDate = item.RequiredDate,
                CargoCategoryCode = cargoCode,
                ImdgClass = imdg,
                UnNumber = un,
                ReeferSetTempC = setTemp,
                ReeferVentPct = item.ReeferVentPct ?? line.ReeferVentPct,
                ReeferHumidityPct = item.ReeferHumidityPct ?? line.ReeferHumidityPct,
                StowageCode = item.StowageCode.Clean(),
                StowageNo = string.IsNullOrWhiteSpace(item.StowageNo) ? null : item.StowageNo.Trim(),
                IsPreCool = item.IsPreCool,
                Remarks = string.IsNullOrWhiteSpace(item.Remarks) ? null : item.Remarks.Trim(),
                HandoverModeCode = mode,
            }));
        }

        if (errors.Count > 0 && !partial)
            return AssignResult.Failed(errors.Values.SelectMany(v => v).Any(m => m.Contains("is active on booking"))
                   && errors.Count == 1 && errors.Values.Single().Count == 1
                ? Problem.Of(TosSupport.Conflict(errors.Values.Single().Single()))
                : Problem.Of(TosSupport.Invalid(errors)));

        foreach (var (_, box) in toAdd) db.BookingContainers.Add(box);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException?.Message.Contains("uq_booking_container__active") == true)
        {
            return AssignResult.Failed(Problem.Of(TosSupport.Conflict(
                "One of these boxes was assigned to another booking a moment ago.", "Reload the booking and send the rows again; rows already saved come back as they are.")));
        }

        foreach (var (_, box) in toAdd)
            foreach (var step in steps)
                db.MovementPlans.Add(new MovementPlan
                {
                    TenantId = booking.TenantId,
                    BookingContainerId = box.BookingContainerId,
                    SequenceNo = step.SequenceNo,
                    MovementId = step.MovementId,
                    MovementCode = step.MovementCode,
                    OrderTypeMovementId = step.OrderTypeMovementId,
                    IsRequired = step.IsRequired,
                    Status = "PENDING",
                });
        await db.SaveChangesAsync(ct);
        return new AssignResult(null, toAdd, replayed, errors);
    }

    /// <summary>Boxes that hold a place on each line: active now, or finished. An unassigned box frees its place.</summary>
    private static async Task<Dictionary<Guid, int>> UsedPerLineAsync(TosDbContext db, Guid bookingId, CancellationToken ct) =>
        await db.BookingContainers
            .Where(x => x.BookingId == bookingId && (x.EndedAt == null || x.EndReason == "COMPLETED"))
            .GroupBy(x => x.EquipmentRequirementId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    // ── resolution: codes → MDM, and the rules ──────────────────────────────

    private sealed record Header(
        BranchClock.Branch Branch, OrderTypePlanRef Plan, PartyRef Line,
        PartyRef? Agent, PartyRef? Customer, PartyRef? Forwarder, PartyRef? Haulier,
        VesselCall? Call, VesselCallLine? CallLine,
        PortRef? Pol, PortRef? Pod, PortRef? Fpd, CommodityRef? Commodity, string? CargoCategory,
        // An order type that does not require a vessel schedule: the vessel call the clerk entered, stored unchecked.
        Guid? CallAsEntered = null);

    private static async Task<Header?> ResolveHeaderAsync(
        TosDbContext db, IMasterDataReferences master, BranchClock clock, SaveBookingRequest request,
        Dictionary<string, List<string>> errors, CancellationToken ct)
    {
        var branch = (await clock.BranchesAsync([request.BranchId!.Value], ct)).GetValueOrDefault(request.BranchId.Value);
        if (branch is null) errors.Add("branchId", "Unknown branch.");

        var orderTypeCode = request.OrderTypeCode.Clean()!;
        var plan = (await master.OrderTypePlansAsync([orderTypeCode], ct)).GetValueOrDefault(orderTypeCode);
        if (plan is null) errors.Add("orderTypeCode", $"Unknown order type '{orderTypeCode}'.");
        else
        {
            if (!plan.IsActive) errors.Add("orderTypeCode", $"Order type {orderTypeCode} is inactive.");
            if (plan.Steps.Count == 0) errors.Add("orderTypeCode", $"Order type {orderTypeCode} has no steps — the gate would not know what to do.");
            if (plan.BookingTypeCode is null) errors.Add("orderTypeCode", $"Order type {orderTypeCode} has no booking type in master data.");
        }

        var roles = new (string Field, string? Code, Func<PartyRef, bool> Plays, string Role)[]
        {
            ("lineCode", request.LineCode, p => p.IsShippingLine, "a shipping line"),
            ("agentCode", request.AgentCode, _ => true, "an agent"),
            ("customerCode", request.CustomerCode, p => p.IsCustomer, "a customer"),
            ("forwarderCode", request.ForwarderCode, p => p.IsForwarder, "a forwarder"),
            ("haulierCode", request.HaulierCode, p => p.IsHaulier, "a haulier"),
        };
        var parties = await master.PartiesAsync(roles.Select(r => r.Code ?? ""), ct);
        var resolved = new Dictionary<string, PartyRef?>();
        foreach (var (field, raw, plays, role) in roles)
        {
            resolved[field] = null;
            if (raw.Clean() is not { } code) continue;
            var party = parties.GetValueOrDefault(code);
            if (party is null) errors.Add(field, $"Unknown party '{code}'.");
            else if (!party.IsActive) errors.Add(field, $"{code} is inactive.");
            else if (!plays(party)) errors.Add(field, $"{code} is not {role}.");
            else resolved[field] = party;
        }

        // D-2: the booking POINTS at the call. Owner 2026-10-04: the order type's "requires vessel
        // schedule" decides; when it is off the call is neither required nor checked (kept as entered),
        // and the ports are optional.
        var scheduled = plan?.RequiresVesselSchedule ?? true;
        VesselCall? call = null;
        VesselCallLine? callLine = null;
        if (!scheduled) { }
        else if (request.VesselCallId is { } callId)
        {
            call = await db.VesselCalls.AsNoTracking().SingleOrDefaultAsync(c => c.VesselCallId == callId, ct);
            if (call is null) errors.Add("vesselCallId", "Unknown vessel call.");
            else if (call.IsCancelled) errors.Add("vesselCallId", $"{call.CallRef} is cancelled — pick the call the line moved the cargo to.");
            else if (resolved["lineCode"] is { } line)
            {
                callLine = await db.VesselCallLines.AsNoTracking()
                    .SingleOrDefaultAsync(l => l.VesselCallId == callId && l.LinePartyId == line.PartyId, ct);
                if (callLine is null)
                    errors.Add("vesselCallId", $"{line.PartyCode} is not on {call.CallRef}. Add the line and its voyage to the call first.");
            }
        }
        else if (plan is not null)
            errors.Add("vesselCallId", $"{plan.OrderTypeCode} requires a vessel schedule: pick the vessel call.");

        // Ports are optional when the order type is not tied to a vessel, but one that IS entered
        // must be a real port: the booking stores a port as id + code (ck_booking__*_pair).
        var ports = await master.PortsAsync(new[] { request.PolPortCode, request.PodPortCode, request.FpdPortCode }.Select(p => p ?? ""), ct);
        PortRef? Port(string field, string? raw)
        {
            if (raw.Clean() is not { } code) return null;
            var port = ports.GetValueOrDefault(code);
            if (port is null) errors.Add(field, $"Unknown port '{code}'.");
            return port;
        }
        var (pol, pod, fpd) = (Port("polPortCode", request.PolPortCode), Port("podPortCode", request.PodPortCode), Port("fpdPortCode", request.FpdPortCode));

        CommodityRef? commodity = null;
        if (request.CommodityCode.Clean() is { } commodityCode)
        {
            commodity = (await master.CommoditiesAsync([commodityCode], ct)).GetValueOrDefault(commodityCode);
            if (commodity is null) errors.Add("commodityCode", $"Unknown commodity '{commodityCode}'.");
        }

        var category = request.CargoCategoryCode.Clean();
        if (category is not null && !(await master.CodeListValuesAsync("CARGO_CATEGORY", [category], ct)).Contains(category))
            errors.Add("cargoCategoryCode", $"'{category}' is not a CARGO_CATEGORY.");

        if (request.ValidFrom is { } from && request.ValidTo is { } to && to < from)
            errors.Add("validTo", "The release ends before it starts.");

        // Vector BookingEntry.cs:168-207, 2548 — what the desktop makes mandatory (owner 2026-10-03).
        if (request.CarrierRef.Clean() is null)
            errors.Add("carrierRef", "The B/L or booking number is required.");
        if (plan?.BookingTypeCode is ExportBooking or ImportDo && request.CustomerCode.Clean() is null)
            errors.Add("customerCode", "The customer is required on an export booking or an import D/O.");
        if (scheduled && plan?.BookingTypeCode is ExportBooking)
        {
            if (request.PolPortCode.Clean() is null) errors.Add("polPortCode", "The loading port is required on an export booking.");
            if (request.PodPortCode.Clean() is null && request.FpdPortCode.Clean() is null)
                errors.Add("fpdPortCode", "The destination port is required on an export booking.");
        }

        // The shipper's declared totals: stored as declared (not the sum of the boxes).
        if (request.TotalQty is <= 0) errors.Add("totalQty", "A declared quantity is more than zero; leave it out if unknown.");
        if (request.TotalVolumeCbm is <= 0) errors.Add("totalVolumeCbm", "A declared volume is more than zero; leave it out if unknown.");
        if (request.TotalWeightKg is <= 0) errors.Add("totalWeightKg", "A declared weight is more than zero; leave it out if unknown.");
        var uom = request.UomCode.Clean();
        if (uom is not null && !(await master.CodeListValuesAsync("UOM", [uom], ct)).Contains(uom))
            errors.Add("uomCode", $"'{uom}' is not a UOM of this depot.");
        if (request.TotalQty is not null && uom is null) errors.Add("uomCode", "Say what the quantity counts (UOM).");

        return branch is null || plan is null || resolved["lineCode"] is null
            ? null
            : new Header(branch, plan, resolved["lineCode"]!, resolved["agentCode"], resolved["customerCode"],
                resolved["forwarderCode"], resolved["haulierCode"], call, callLine, pol, pod, fpd, commodity, category,
                scheduled ? null : request.VesselCallId);
    }

    private static void ApplyHeader(Booking b, SaveBookingRequest request, Header h)
    {
        b.OrderTypeId = h.Plan.OrderTypeId;
        b.OrderTypeCode = h.Plan.OrderTypeCode;
        b.BookingTypeCode = h.Plan.BookingTypeCode!;
        b.DirectionCode = h.Plan.DirectionCode;
        b.CargoClassCode = h.Plan.CargoClassCode;
        b.CarrierRef = request.CarrierRef.Clean();
        b.LinePartyId = h.Line.PartyId; b.LinePartyCode = h.Line.PartyCode;
        b.AgentPartyId = h.Agent?.PartyId; b.AgentPartyCode = h.Agent?.PartyCode;
        b.CustomerPartyId = h.Customer?.PartyId; b.CustomerPartyCode = h.Customer?.PartyCode;
        b.ForwarderPartyId = h.Forwarder?.PartyId; b.ForwarderPartyCode = h.Forwarder?.PartyCode;
        b.HaulierPartyId = h.Haulier?.PartyId; b.HaulierPartyCode = h.Haulier?.PartyCode;
        b.VesselCallId = h.Call?.VesselCallId ?? h.CallAsEntered;
        b.VesselCallLineId = h.CallLine?.VesselCallLineId;
        b.PolPortId = h.Pol?.PortId; b.PolPortCode = h.Pol?.PortCode;
        b.PodPortId = h.Pod?.PortId; b.PodPortCode = h.Pod?.PortCode;
        b.FpdPortId = h.Fpd?.PortId; b.FpdPortCode = h.Fpd?.PortCode;
        b.CargoCategoryCode = h.CargoCategory;
        b.CommodityId = h.Commodity?.CommodityId; b.CommodityCode = h.Commodity?.CommodityCode;
        b.ValidFrom = request.ValidFrom;
        b.ValidTo = request.ValidTo;
        b.CustomerRef = string.IsNullOrWhiteSpace(request.CustomerRef) ? null : request.CustomerRef.Trim();
        b.Remarks = string.IsNullOrWhiteSpace(request.Remarks) ? null : request.Remarks.Trim();
        b.PaperlessCode = request.PaperlessCode.Clean();
        b.SubBlNo = request.SubBlNo.Clean();
        b.NextPrevLocation = string.IsNullOrWhiteSpace(request.NextPrevLocation) ? null : request.NextPrevLocation.Trim();
        b.TotalQty = request.TotalQty;
        b.UomCode = request.UomCode.Clean();
        b.TotalVolumeCbm = request.TotalVolumeCbm;
        b.TotalWeightKg = request.TotalWeightKg;
        b.MarksAndNos = string.IsNullOrWhiteSpace(request.MarksAndNos) ? null : request.MarksAndNos.Trim();
        b.SpecialInstruction = string.IsNullOrWhiteSpace(request.SpecialInstruction) ? null : request.SpecialInstruction.Trim();
    }

    private const string BlindGateIn = "BLIND GATE IN";
    private const string ExportBooking = "EXPORT_BOOKING";
    private const string ImportDo = "IMPORT_DO";
    private const string LateGateNeedsOverride =
        "Only a user who may override cut-offs (tos.cutoff.override) can allow a late gate-in on a booking.";

    /// <summary>Vector "Allow Late Gate-In": who set it and when are kept with it.</summary>
    private static void SetLateGate(Booking b, bool allow, ITenantContext caller)
    {
        b.AllowLateGateIn = allow;
        b.LateGateSetBy = caller.UserId();
        b.LateGateSetAt = DateTimeOffset.UtcNow;
    }

    private sealed record ResolvedRequirement(RequirementItem Item, EquipmentTypeRef? Type, string? Grade);

    private static async Task<List<ResolvedRequirement>> ResolveRequirementsAsync(
        IMasterDataReferences master, IReadOnlyList<RequirementItem> items, Dictionary<string, List<string>> errors, CancellationToken ct)
    {
        var types = await master.EquipmentTypesAsync(items.Select(i => i.EquipmentTypeCode ?? ""), ct);
        var grades = await master.ContainerGradesAsync(items.Select(i => i.MinGradeCode ?? ""), ct);
        var result = new List<ResolvedRequirement>();

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var key = $"requirements[{i}]";
            var code = item.EquipmentTypeCode.Clean();
            var type = code is null ? null : types.GetValueOrDefault(code);
            if (code is null) errors.Add($"{key}.equipmentTypeCode", "Equipment type is required.");
            else if (type is null) errors.Add($"{key}.equipmentTypeCode", $"Unknown equipment type '{code}'.");
            else if (!type.IsActive) errors.Add($"{key}.equipmentTypeCode", $"{code} is inactive.");

            if (item.Qty is < 1 or > 999) errors.Add($"{key}.qty", "Between 1 and 999 boxes per line.");
            if (item.LineNo is < 1) errors.Add($"{key}.lineNo", "Line numbers start at 1.");

            var grade = item.MinGradeCode.Clean();
            if (grade is not null && !grades.ContainsKey(grade)) errors.Add($"{key}.minGradeCode", $"Unknown container grade '{grade}'.");

            // A reefer box without a set point is a box the gate cannot check (PLAN §4.2, C#).
            if (type is { IsReefer: true } && item.ReeferSetTempC is null)
                errors.Add($"{key}.reeferSetTempC", $"{type.TypeCode} is a reefer — give the set temperature.");
            if (type is { IsReefer: false } && item.ReeferSetTempC is not null)
                errors.Add($"{key}.reeferSetTempC", $"{type.TypeCode} is not a reefer; a set temperature makes no sense on it.");
            if (item.ReeferSetTempC is < -70 or > 40) errors.Add($"{key}.reeferSetTempC", "Between −70 °C and +40 °C.");

            var un = item.UnNumber?.Trim();
            if (un is not null && (un.Length != 4 || !un.All(char.IsAsciiDigit))) errors.Add($"{key}.unNumber", "A UN number is four digits, e.g. 1203.");
            if (un is not null && string.IsNullOrWhiteSpace(item.ImdgClass)) errors.Add($"{key}.imdgClass", "A UN number needs its IMDG class.");

            foreach (var (field, value) in new[]
                     {
                         ("oogOverHeightCm", item.OogOverHeightCm), ("oogOverWidthLeftCm", item.OogOverWidthLeftCm),
                         ("oogOverWidthRightCm", item.OogOverWidthRightCm), ("oogOverLengthFrontCm", item.OogOverLengthFrontCm),
                         ("oogOverLengthBackCm", item.OogOverLengthBackCm),
                     })
                if (value is < 0) errors.Add($"{key}.{field}", "Over-dimension is zero or more.");

            // V-15: 374K Vector lines said 0 kg. Unknown is null, not zero.
            if (item.DeclaredGrossWeightKg is <= 0) errors.Add($"{key}.declaredGrossWeightKg", "Leave the weight out if unknown — zero is not a weight.");
            if (item.Remarks?.Length > 500) errors.Add($"{key}.remarks", "At most 500 characters.");

            result.Add(new ResolvedRequirement(item, type, grade));
        }
        return result;
    }

    private static void ApplyRequirement(EquipmentRequirement e, ResolvedRequirement r)
    {
        e.EquipmentTypeId = r.Type!.EquipmentTypeId;
        e.EquipmentTypeCode = r.Type.TypeCode;
        e.Qty = r.Item.Qty;
        e.MinGradeCode = r.Grade;
        e.ReeferSetTempC = r.Item.ReeferSetTempC;
        e.ReeferVentPct = r.Item.ReeferVentPct;
        e.ReeferHumidityPct = r.Item.ReeferHumidityPct;
        e.ImdgClass = r.Item.ImdgClass.Clean();
        e.UnNumber = r.Item.UnNumber?.Trim();
        e.OogOverHeightCm = r.Item.OogOverHeightCm;
        e.OogOverWidthLeftCm = r.Item.OogOverWidthLeftCm;
        e.OogOverWidthRightCm = r.Item.OogOverWidthRightCm;
        e.OogOverLengthFrontCm = r.Item.OogOverLengthFrontCm;
        e.OogOverLengthBackCm = r.Item.OogOverLengthBackCm;
        e.DeclaredGrossWeightKg = r.Item.DeclaredGrossWeightKg;
        e.Remarks = string.IsNullOrWhiteSpace(r.Item.Remarks) ? null : r.Item.Remarks.Trim();
    }

    private static ProblemHttpResult NotOpen(Booking b) =>
        TosSupport.Conflict($"{b.OrderNo} is {b.Status} and can no longer be changed.", b.CancelReason ?? b.CloseReason);

    /// <summary>Why an assignment was refused: a 400 (<see cref="Invalid"/>) or a 409 (<see cref="Refused"/>).</summary>
    private sealed record Problem(ValidationProblem? Invalid, ProblemHttpResult? Refused)
    {
        public static Problem Of(ValidationProblem v) => new(v, null);
        public static Problem Of(ProblemHttpResult p) => new(null, p);
    }

    // ── projection ──────────────────────────────────────────────────────────

    private static async Task<BookingDetailResponse?> DetailAsync(
        TosDbContext db, BranchClock clock, IMasterDataReferences master, IUserDirectory users, Guid id, CancellationToken ct)
    {
        var row = await (
            from bk in db.Bookings.AsNoTracking()
            join p in db.VwBookingProgresses on bk.BookingId equals p.BookingId
            join vc in db.VesselCalls on bk.VesselCallId equals vc.VesselCallId into calls
            from vc in calls.DefaultIfEmpty()
            join st in db.VwVesselCallStatuses on bk.VesselCallId equals st.VesselCallId into statuses
            from st in statuses.DefaultIfEmpty()
            join vl in db.VesselCallLines on bk.VesselCallLineId equals vl.VesselCallLineId into lines
            from vl in lines.DefaultIfEmpty()
            where bk.BookingId == id
            select new
            {
                bk, p,
                CallRef = vc == null ? null : vc.CallRef, VesselCode = vc == null ? null : vc.VesselCode,
                Etd = vc == null ? (DateTimeOffset?)null : vc.Etd, CallStatus = st == null ? null : st.CallStatus,
                VoyageIn = vl == null ? null : vl.VoyageIn, VoyageOut = vl == null ? null : vl.VoyageOut,
            }).SingleOrDefaultAsync(ct);
        if (row is null) return null;

        var b = row.bk;
        var branch = (await clock.BranchesAsync([b.BranchId], ct)).GetValueOrDefault(b.BranchId);
        var today = (await clock.TodayForAsync([b.BranchId], ct))(b.BranchId);

        // Vector showed these, never stored them: the line's operator code and the destination's trade mode.
        var owner = (await master.PartiesAsync([b.LinePartyCode], ct)).GetValueOrDefault(b.LinePartyCode)?.OperatorCode;
        var destination = b.FpdPortCode ?? b.PodPortCode;
        var tradeMode = destination is null ? null : (await master.PortsAsync([destination], ct)).GetValueOrDefault(destination)?.TradeMode;
        var names = await users.DisplayNamesAsync(new[] { b.CreatedBy, b.UpdatedBy }.OfType<Guid>().Distinct(), ct);

        var requirements = await db.EquipmentRequirements.AsNoTracking().Where(r => r.BookingId == id).OrderBy(r => r.LineNo).ToListAsync(ct);
        var boxes = await db.BookingContainers.AsNoTracking().Where(x => x.BookingId == id)
            .OrderBy(x => x.EndedAt != null).ThenBy(x => x.AssignedAt).ThenBy(x => x.ContainerNo).ToListAsync(ct);
        var boxIds = boxes.Select(x => x.BookingContainerId).ToList();
        var steps = (await db.MovementPlans.AsNoTracking().Where(m => boxIds.Contains(m.BookingContainerId)).OrderBy(m => m.SequenceNo).ToListAsync(ct))
            .ToLookup(m => m.BookingContainerId);
        var lineNoOf = requirements.ToDictionary(r => r.EquipmentRequirementId, r => r.LineNo);

        return new BookingDetailResponse(
            new BookingResponse(
                b.BookingId, b.OrderNo, b.BranchId, branch?.BranchCode, b.CarrierRef,
                b.OrderTypeCode, b.BookingTypeCode, b.DirectionCode, b.CargoClassCode,
                b.LinePartyCode, b.AgentPartyCode, b.CustomerPartyCode, b.ForwarderPartyCode, b.HaulierPartyCode,
                b.VesselCallId, row.CallRef, row.VesselCode, row.Etd, row.CallStatus, row.VoyageIn, row.VoyageOut,
                b.PolPortCode, b.PodPortCode, b.FpdPortCode, b.CargoCategoryCode, b.CommodityCode, b.ValidFrom, b.ValidTo,
                b.Status, BookingRules.EffectiveProgress(row.p.ProgressStatus, b.ValidTo, today),
                b.CancelledAt, b.CancelReason, b.ClosedAt, b.CloseReason, b.Source, b.CustomerRef, b.Remarks, b.CreatedAt,
                Convert.ToBase64String(b.RowVersion),
                b.AllowLateGateIn, b.LateGateSetAt, b.PaperlessCode, b.SubBlNo, b.NextPrevLocation, b.TotalQty, b.UomCode,
                b.TotalVolumeCbm, b.TotalWeightKg, b.MarksAndNos, b.SpecialInstruction,
                owner, tradeMode,
                b.CreatedBy, b.CreatedBy is { } cb ? names.GetValueOrDefault(cb) : null,
                b.UpdatedAt, b.UpdatedBy, b.UpdatedBy is { } ub ? names.GetValueOrDefault(ub) : null),
            row.p.QtyRequired, row.p.QtyAssigned, row.p.QtyCompleted, row.p.StepsDone,
            requirements.Select(r => new RequirementResponse(
                r.EquipmentRequirementId, r.LineNo, r.EquipmentTypeCode, r.Qty,
                boxes.Count(x => x.EquipmentRequirementId == r.EquipmentRequirementId && x.EndedAt == null),
                boxes.Count(x => x.EquipmentRequirementId == r.EquipmentRequirementId && x.EndReason == "COMPLETED"),
                r.MinGradeCode, r.ReeferSetTempC, r.ReeferVentPct, r.ReeferHumidityPct, r.ImdgClass, r.UnNumber,
                r.OogOverHeightCm, r.OogOverWidthLeftCm, r.OogOverWidthRightCm, r.OogOverLengthFrontCm, r.OogOverLengthBackCm,
                r.DeclaredGrossWeightKg, r.Remarks)).ToList(),
            boxes.Select(x => ToResponse(x, lineNoOf.GetValueOrDefault(x.EquipmentRequirementId), steps[x.BookingContainerId])).ToList());
    }

    private static BookingContainerResponse ToResponse(BookingContainer x, short lineNo, IEnumerable<MovementPlan> steps) => new(
        x.BookingContainerId, x.EquipmentRequirementId, lineNo, x.ContainerNo,
        x.ContainerId is not null, x.IsCheckDigitValid, x.AssignmentSource, x.DeclaredSealNo, x.DeclaredVgmKg,
        x.AssignedAt, x.EndedAt, x.EndReason,
        steps.Select(m => new StepResponse(m.MovementPlanId, m.SequenceNo, m.MovementCode, m.IsRequired, m.Status, m.GateTransactionId, m.SkipReason)).ToList(),
        x.HandoverModeCode,
        x.ClientLineId, x.CustomerSealNo, x.DeclaredVolumeCbm, x.RequiredDate, x.CargoCategoryCode, x.ImdgClass, x.UnNumber,
        x.ReeferSetTempC, x.ReeferVentPct, x.ReeferHumidityPct, x.StowageCode, x.StowageNo, x.IsPreCool, x.Remarks,
        x.RowVersion is { Length: > 0 } v ? Convert.ToBase64String(v) : null);
}
