using System.ComponentModel.DataAnnotations;
using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Application;
using Gecko.Revenue.Domain;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Endpoints.Tariffs;

public sealed record ScheduleResponse(
    Guid ScheduleId, string ScheduleNo, short VersionNo, Guid LineageId, string Name,
    string ModuleCode, string ScheduleType, byte ScopeRank, Guid? BranchId,
    string? AgentPartyCode, string? ForwarderPartyCode, string? CustomerPartyCode, string? BookingRef,
    string CurrencyCode, bool PricesIncludeTax, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
    DateOnly? EffectiveUntil, string Status, string Lifecycle, bool IsEditable,
    DateTimeOffset? SubmittedAt, DateTimeOffset? ApprovedAt, Guid? ApprovedBy, string? RejectionReason,
    bool WaiveDamagedEmptyStorage, string? Remarks, int RateCount, string RowVersion);

public sealed record SaveScheduleRequest(
    // Header fields only. Rates and free time are replaced as sets (RateEndpoints).
    [property: Required, RegularExpression("^[A-Z0-9][A-Z0-9._/-]{0,29}$", ErrorMessage = "Upper-case letters, digits and . _ / -, up to 30 chars — e.g. CTR-MAEU.")] string ScheduleNo,
    [property: Required, MaxLength(200)] string Name,
    [property: Required, MaxLength(20)] string ModuleCode,
    [property: Required, AllowedValues("PUBLIC", "CONTRACT", "SPOT")] string ScheduleType,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo = null,
    Guid? BranchId = null,
    [property: MaxLength(25)] string? AgentPartyCode = null,
    [property: MaxLength(25)] string? ForwarderPartyCode = null,
    [property: MaxLength(25)] string? CustomerPartyCode = null,
    [property: MaxLength(30)] string? BookingRef = null,
    [property: StringLength(3, MinimumLength = 3)] string CurrencyCode = "THB",
    bool PricesIncludeTax = false,
    bool WaiveDamagedEmptyStorage = false,
    [property: MaxLength(1000)] string? Remarks = null,
    string? RowVersion = null);

/// <summary>Start a new version of an APPROVED tariff. Its rates, tiers, conditions and free time are copied into a DRAFT to edit.</summary>
public sealed record ReviseRequest(
    [property: Required] string RowVersion,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo = null,
    [property: MaxLength(200)] string? Name = null);

public sealed record DecisionRequest([property: Required] string RowVersion, [property: MaxLength(500)] string? Reason = null);

/// <summary>
/// A tariff SCHEDULE is one version of one price agreement: the depot's public
/// list, a contract with a line / agent / forwarder / customer, or a spot price
/// for one booking (PLAN.md §4.1).
///
/// THE RULES THIS FILE ENFORCES, which the database cannot:
///   * Only a DRAFT can change. An approved price is changed by a new version.
///   * The person who drafted or submitted a version cannot approve it, unless
///     the tenant has switched revenue.tariff_self_approval_allowed on.
///   * Two approved agreements with the same scope may not be in force on the
///     same day — the resolver would have no way to choose.
///   * Parties must exist in master data AND play the role they are named for:
///     an "agent" is a shipping line party, a "forwarder" has the forwarder
///     extension, a "customer" the customer extension.
/// </summary>
internal static class ScheduleEndpoints
{
    public static RouteGroupBuilder MapScheduleEndpoints(this RouteGroupBuilder revenue)
    {
        var tariffs = revenue.MapGroup("/tariffs").WithTags("Revenue — tariffs");

        tariffs.MapGet("/", ListAsync).RequirePermission(RevenuePermissions.TariffView).WithSummary("List tariff versions, with their lifecycle today");
        tariffs.MapGet("/{scheduleId:guid}", GetAsync).RequirePermission(RevenuePermissions.TariffView).WithName("GetTariff").WithSummary("Get one tariff version");
        tariffs.MapPost("/", CreateAsync).RequirePermission(RevenuePermissions.TariffManage).Validate<SaveScheduleRequest>().WithSummary("Create a new tariff (version 1, DRAFT)");
        tariffs.MapPut("/{scheduleId:guid}", UpdateAsync).RequirePermission(RevenuePermissions.TariffManage).Validate<SaveScheduleRequest>().WithSummary("Edit a DRAFT tariff's header");
        tariffs.MapPost("/{scheduleId:guid}/submit", SubmitAsync).RequirePermission(RevenuePermissions.TariffManage).Validate<DecisionRequest>().WithSummary("Send a DRAFT for approval");
        tariffs.MapPost("/{scheduleId:guid}/approve", ApproveAsync).RequirePermission(RevenuePermissions.TariffApprove).Validate<DecisionRequest>().WithSummary("Approve a PENDING tariff — its prices go live and are frozen");
        tariffs.MapPost("/{scheduleId:guid}/reject", RejectAsync).RequirePermission(RevenuePermissions.TariffApprove).Validate<DecisionRequest>().WithSummary("Reject a PENDING tariff, with a reason");
        tariffs.MapPost("/{scheduleId:guid}/withdraw", WithdrawAsync).RequirePermission(RevenuePermissions.TariffManage).Validate<DecisionRequest>().WithSummary("Withdraw a DRAFT or PENDING tariff");
        tariffs.MapPost("/{scheduleId:guid}/revise", ReviseAsync).RequirePermission(RevenuePermissions.TariffManage).Validate<ReviseRequest>().WithSummary("Start the next version of an APPROVED tariff (a DRAFT copy of its prices)");
        tariffs.MapDelete("/{scheduleId:guid}", DeleteAsync).RequirePermission(RevenuePermissions.TariffManage).WithSummary("Soft-delete a DRAFT tariff");

        return revenue;
    }

    // ── read ────────────────────────────────────────────────────────────────

    private static async Task<Ok<PagedResult<ScheduleResponse>>> ListAsync(
        [AsParameters] ListQuery query, RevenueDbContext db, BranchCalendar calendar, CancellationToken ct,
        string? moduleCode = null, string? scheduleType = null, string? status = null, string? partyCode = null)
    {
        var schedules = db.Schedules.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(moduleCode)) schedules = schedules.Where(s => s.ModuleCode == moduleCode.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(scheduleType)) schedules = schedules.Where(s => s.ScheduleType == scheduleType.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(status)) schedules = schedules.Where(s => s.Status == status.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(partyCode))
        {
            var party = partyCode.ToUpperInvariant();
            schedules = schedules.Where(s => s.AgentPartyCode == party || s.ForwarderPartyCode == party || s.CustomerPartyCode == party);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
            schedules = schedules.Where(s => s.ScheduleNo.Contains(query.Search) || s.Name.Contains(query.Search));

        var page = await Project(db, schedules
                .OrderBy(s => s.ScopeRank).ThenBy(s => s.ScheduleNo).ThenBy(s => s.VersionNo))
            .ToPagedAsync(query.Page, query.PageSize, ct);

        var today = await calendar.TodayForAsync(page.Items.Select(i => i.Row.BranchId), ct);
        return TypedResults.Ok(new PagedResult<ScheduleResponse>(
            page.Items.Select(i => Map(i, today(i.Row.BranchId))).ToList(), page.Page, page.PageSize, page.TotalCount));
    }

    private static async Task<Results<Ok<ScheduleResponse>, NotFound>> GetAsync(
        Guid scheduleId, RevenueDbContext db, BranchCalendar calendar, CancellationToken ct)
    {
        var row = await Project(db, db.Schedules.AsNoTracking().Where(s => s.ScheduleId == scheduleId)).SingleOrDefaultAsync(ct);
        if (row is null) return TypedResults.NotFound();
        return TypedResults.Ok(Map(row, await calendar.TodayAsync(row.Row.BranchId, ct)));
    }

    // ── write ───────────────────────────────────────────────────────────────

    private static async Task<Results<CreatedAtRoute<ScheduleResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SaveScheduleRequest request, RevenueDbContext db, IMasterDataReferences masterData, BranchCalendar calendar,
        ITenantContext caller, CancellationToken ct)
    {
        var scheduleNo = request.ScheduleNo.ToUpperInvariant();
        if (await db.Schedules.AnyAsync(s => s.ScheduleNo == scheduleNo, ct))
            return RevenueSupport.Conflict($"Tariff '{scheduleNo}' already exists.", "Revise the existing tariff to change its prices.");

        var schedule = new Schedule
        {
            ScheduleId = Guid.CreateVersion7(),
            TenantId = caller.TenantId(),
            ScheduleNo = scheduleNo,
            LineageId = Guid.CreateVersion7(),
            VersionNo = 1,
            Status = ScheduleStatuses.Draft,
            Name = request.Name,
            ModuleCode = request.ModuleCode,
            ScheduleType = request.ScheduleType,
            CurrencyCode = request.CurrencyCode,
        };
        if (await ApplyAsync(schedule, request, db, masterData, ct) is { } problem) return problem;

        db.Schedules.Add(schedule);
        await db.SaveChangesAsync(ct);

        var created = await Project(db, db.Schedules.AsNoTracking().Where(s => s.ScheduleId == schedule.ScheduleId)).SingleAsync(ct);
        return TypedResults.CreatedAtRoute(Map(created, await calendar.TodayAsync(schedule.BranchId, ct)), "GetTariff", new { scheduleId = schedule.ScheduleId });
    }

    private static async Task<Results<Ok<ScheduleResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid scheduleId, SaveScheduleRequest request, RevenueDbContext db, IMasterDataReferences masterData,
        BranchCalendar calendar, CancellationToken ct)
    {
        var schedule = await db.Schedules.SingleOrDefaultAsync(s => s.ScheduleId == scheduleId, ct);
        if (schedule is null) return TypedResults.NotFound();
        if (NotEditable(schedule) is { } locked) return locked;
        if (!db.TrySetExpectedVersion(schedule, request.RowVersion))
            return RevenueSupport.Invalid("rowVersion", "Send the rowVersion you received when reading the tariff.");
        if (!string.Equals(schedule.ScheduleNo, request.ScheduleNo, StringComparison.OrdinalIgnoreCase))
            return RevenueSupport.Invalid("scheduleNo", "The tariff number cannot be changed.");

        // Versions of one agreement keep one scope — a changed party is a different agreement.
        if (schedule.VersionNo > 1 && ScopeChanged(schedule, request))
            return RevenueSupport.Invalid("scheduleType", "A revision keeps the scope of the agreement it revises (type, module, branch, parties, booking). Create a new tariff instead.");

        if (await ApplyAsync(schedule, request, db, masterData, ct) is { } problem) return problem;
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        var row = await Project(db, db.Schedules.AsNoTracking().Where(s => s.ScheduleId == scheduleId)).SingleAsync(ct);
        return TypedResults.Ok(Map(row, await calendar.TodayAsync(row.Row.BranchId, ct)));
    }

    private static async Task<Results<Ok<ScheduleResponse>, NotFound, ValidationProblem, ProblemHttpResult>> SubmitAsync(
        Guid scheduleId, DecisionRequest request, RevenueDbContext db, ScheduleApprovalGuard guard,
        BranchCalendar calendar, ITenantContext caller, TimeProvider clock, CancellationToken ct)
    {
        var schedule = await db.Schedules.SingleOrDefaultAsync(s => s.ScheduleId == scheduleId, ct);
        if (schedule is null) return TypedResults.NotFound();
        if (!ScheduleLifecycle.CanSubmit(schedule.Status))
            return RevenueSupport.Conflict($"A {schedule.Status} tariff cannot be submitted.");
        if (!db.TrySetExpectedVersion(schedule, request.RowVersion))
            return RevenueSupport.Invalid("rowVersion", "Send the rowVersion you received when reading the tariff.");

        if (await guard.SubmitProblemsAsync(schedule, ct) is { Count: > 0 } problems)
            return RevenueSupport.Conflict("This tariff is not ready for approval.", string.Join(" ", problems));

        schedule.Status = ScheduleStatuses.Pending;
        schedule.SubmittedAt = clock.GetUtcNow();
        schedule.SubmittedBy = caller.UserId();
        return await SavedAsync(schedule, db, calendar, ct);
    }

    private static async Task<Results<Ok<ScheduleResponse>, NotFound, ValidationProblem, ProblemHttpResult>> ApproveAsync(
        Guid scheduleId, DecisionRequest request, RevenueDbContext db, ScheduleApprovalGuard guard,
        IMasterDataReferences masterData, BranchCalendar calendar, ITenantContext caller, TimeProvider clock, CancellationToken ct)
    {
        var schedule = await db.Schedules.SingleOrDefaultAsync(s => s.ScheduleId == scheduleId, ct);
        if (schedule is null) return TypedResults.NotFound();
        if (!ScheduleLifecycle.CanDecide(schedule.Status))
            return RevenueSupport.Conflict($"Only a PENDING tariff can be approved; this one is {schedule.Status}.");
        if (!db.TrySetExpectedVersion(schedule, request.RowVersion))
            return RevenueSupport.Invalid("rowVersion", "Send the rowVersion you received when reading the tariff.");

        var approver = caller.UserId();
        if (!ScheduleLifecycle.IsIndependentApprover(approver, schedule.CreatedBy, schedule.SubmittedBy)
            && !await masterData.GetBoolSettingAsync(RevenueSettingKeys.TariffSelfApprovalAllowed, null, fallback: false, ct))
            return TypedResults.Problem(
                title: "You cannot approve a tariff you drafted or submitted.",
                detail: "Ask another approver. A tenant administrator can allow self-approval with the setting "
                        + RevenueSettingKeys.TariffSelfApprovalAllowed + ".",
                statusCode: StatusCodes.Status403Forbidden);

        if (await guard.ApproveProblemsAsync(schedule, ct) is { Count: > 0 } clashes)
            return RevenueSupport.Conflict("Another approved tariff already covers these customers on these dates.", string.Join(" ", clashes));

        schedule.Status = ScheduleStatuses.Approved;
        schedule.ApprovedAt = clock.GetUtcNow();
        schedule.ApprovedBy = approver;
        return await SavedAsync(schedule, db, calendar, ct);
    }

    private static async Task<Results<Ok<ScheduleResponse>, NotFound, ValidationProblem, ProblemHttpResult>> RejectAsync(
        Guid scheduleId, DecisionRequest request, RevenueDbContext db, BranchCalendar calendar,
        ITenantContext caller, TimeProvider clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return RevenueSupport.Invalid("reason", "Say why — the person who drafted it has to fix it.");

        var schedule = await db.Schedules.SingleOrDefaultAsync(s => s.ScheduleId == scheduleId, ct);
        if (schedule is null) return TypedResults.NotFound();
        if (!ScheduleLifecycle.CanDecide(schedule.Status))
            return RevenueSupport.Conflict($"Only a PENDING tariff can be rejected; this one is {schedule.Status}.");
        if (!db.TrySetExpectedVersion(schedule, request.RowVersion))
            return RevenueSupport.Invalid("rowVersion", "Send the rowVersion you received when reading the tariff.");

        schedule.Status = ScheduleStatuses.Rejected;
        schedule.RejectedAt = clock.GetUtcNow();
        schedule.RejectedBy = caller.UserId();
        schedule.RejectionReason = request.Reason;
        return await SavedAsync(schedule, db, calendar, ct);
    }

    private static async Task<Results<Ok<ScheduleResponse>, NotFound, ValidationProblem, ProblemHttpResult>> WithdrawAsync(
        Guid scheduleId, DecisionRequest request, RevenueDbContext db, BranchCalendar calendar, CancellationToken ct)
    {
        var schedule = await db.Schedules.SingleOrDefaultAsync(s => s.ScheduleId == scheduleId, ct);
        if (schedule is null) return TypedResults.NotFound();
        if (!ScheduleLifecycle.CanWithdraw(schedule.Status))
            return RevenueSupport.Conflict($"A {schedule.Status} tariff cannot be withdrawn.",
                schedule.Status == ScheduleStatuses.Approved ? "End an approved tariff with a new version instead." : null);
        if (!db.TrySetExpectedVersion(schedule, request.RowVersion))
            return RevenueSupport.Invalid("rowVersion", "Send the rowVersion you received when reading the tariff.");

        schedule.Status = ScheduleStatuses.Withdrawn;
        if (!string.IsNullOrWhiteSpace(request.Reason)) schedule.Remarks = request.Reason;
        return await SavedAsync(schedule, db, calendar, ct);
    }

    /// <summary>
    /// The only way to change an approved price. The new version supersedes the
    /// old one from its own start date (tariff.vw_schedule_effective); the old
    /// version stays exactly as approved for everything before that day.
    /// </summary>
    private static async Task<Results<CreatedAtRoute<ScheduleResponse>, NotFound, ValidationProblem, ProblemHttpResult>> ReviseAsync(
        Guid scheduleId, ReviseRequest request, RevenueDbContext db, BranchCalendar calendar, CancellationToken ct)
    {
        var source = await db.Schedules.AsNoTracking().SingleOrDefaultAsync(s => s.ScheduleId == scheduleId, ct);
        if (source is null) return TypedResults.NotFound();
        if (source.Status != ScheduleStatuses.Approved)
            return RevenueSupport.Conflict($"Only an APPROVED tariff can be revised; this one is {source.Status}.",
                source.Status == ScheduleStatuses.Draft ? "Edit the draft directly." : null);
        if (Convert.ToBase64String(source.RowVersion) != request.RowVersion)
            return RevenueSupport.Conflict("The tariff changed since you loaded it.");

        var lineage = await db.Schedules.AsNoTracking()
            .Where(s => s.LineageId == source.LineageId)
            .Select(s => new { s.VersionNo, s.Status })
            .ToListAsync(ct);
        if (lineage.FirstOrDefault(v => v.Status is ScheduleStatuses.Draft or ScheduleStatuses.Pending) is { } open)
            return RevenueSupport.Conflict($"Version {open.VersionNo} of {source.ScheduleNo} is already {open.Status}.",
                "Finish or withdraw it before starting another revision.");
        if (lineage.Any(v => v.Status == ScheduleStatuses.Approved && v.VersionNo > source.VersionNo))
            return RevenueSupport.Conflict($"A later version of {source.ScheduleNo} is already approved — revise that one.");
        if (request.EffectiveFrom <= source.EffectiveFrom)
            return RevenueSupport.Invalid("effectiveFrom", $"A revision starts after the version it replaces (which starts {source.EffectiveFrom:yyyy-MM-dd}).");
        if (request.EffectiveTo is { } to && to < request.EffectiveFrom)
            return RevenueSupport.Invalid("effectiveTo", "The end date is before the start date.");

        var revision = new Schedule
        {
            ScheduleId = Guid.CreateVersion7(),
            TenantId = source.TenantId,
            BranchId = source.BranchId,
            ModuleCode = source.ModuleCode,
            ScheduleNo = source.ScheduleNo,
            Name = request.Name ?? source.Name,
            ScheduleType = source.ScheduleType,
            LineageId = source.LineageId,
            VersionNo = (short)(lineage.Max(v => v.VersionNo) + 1),
            SupersedesScheduleId = source.ScheduleId,
            AgentPartyId = source.AgentPartyId,
            AgentPartyCode = source.AgentPartyCode,
            ForwarderPartyId = source.ForwarderPartyId,
            ForwarderPartyCode = source.ForwarderPartyCode,
            CustomerPartyId = source.CustomerPartyId,
            CustomerPartyCode = source.CustomerPartyCode,
            BookingRef = source.BookingRef,
            CurrencyCode = source.CurrencyCode,
            PricesIncludeTax = source.PricesIncludeTax,
            EffectiveFrom = request.EffectiveFrom,
            EffectiveTo = request.EffectiveTo,
            Status = ScheduleStatuses.Draft,
            WaiveDamagedEmptyStorage = source.WaiveDamagedEmptyStorage,
            Remarks = $"Revision of v{source.VersionNo}",
        };
        db.Schedules.Add(revision);
        await RateSetWriter.CopyAsync(db, source.ScheduleId, revision, ct);
        await db.SaveChangesAsync(ct);

        var row = await Project(db, db.Schedules.AsNoTracking().Where(s => s.ScheduleId == revision.ScheduleId)).SingleAsync(ct);
        return TypedResults.CreatedAtRoute(Map(row, await calendar.TodayAsync(revision.BranchId, ct)), "GetTariff", new { scheduleId = revision.ScheduleId });
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        Guid scheduleId, RevenueDbContext db, CancellationToken ct)
    {
        var schedule = await db.Schedules.SingleOrDefaultAsync(s => s.ScheduleId == scheduleId, ct);
        if (schedule is null) return TypedResults.NotFound();
        if (NotEditable(schedule) is { } locked) return locked;

        // The whole draft goes: rates, their tiers and conditions, free time.
        await RateSetWriter.RemoveRatesAsync(db, scheduleId, ct);
        db.FreeTimeRules.RemoveRange(await db.FreeTimeRules.Where(f => f.ScheduleId == scheduleId).ToListAsync(ct));
        db.Schedules.Remove(schedule);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    internal static ProblemHttpResult? NotEditable(Schedule schedule) =>
        ScheduleLifecycle.IsEditable(schedule.Status)
            ? null
            : RevenueSupport.Conflict(
                $"This tariff is {schedule.Status} and can no longer be changed.",
                schedule.Status == ScheduleStatuses.Approved
                    ? "Approved prices are frozen. Create a revision to change them."
                    : "Only a DRAFT can be edited.");

    private static bool ScopeChanged(Schedule s, SaveScheduleRequest r) =>
        s.ScheduleType != r.ScheduleType.ToUpperInvariant()
        || s.ModuleCode != r.ModuleCode.ToUpperInvariant()
        || s.BranchId != r.BranchId
        || !SameCode(s.AgentPartyCode, r.AgentPartyCode)
        || !SameCode(s.ForwarderPartyCode, r.ForwarderPartyCode)
        || !SameCode(s.CustomerPartyCode, r.CustomerPartyCode)
        || !SameCode(s.BookingRef, r.BookingRef);

    private static bool SameCode(string? a, string? b) =>
        string.Equals(Blank(a), Blank(b), StringComparison.OrdinalIgnoreCase);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Validates the header against the replicas and master data, then copies it onto the entity.</summary>
    private static async Task<ValidationProblem?> ApplyAsync(
        Schedule schedule, SaveScheduleRequest request, RevenueDbContext db, IMasterDataReferences masterData, CancellationToken ct)
    {
        var errors = new Dictionary<string, List<string>>();
        var type = request.ScheduleType.ToUpperInvariant();
        var module = request.ModuleCode.ToUpperInvariant();
        var currency = request.CurrencyCode.ToUpperInvariant();
        var agent = Blank(request.AgentPartyCode)?.ToUpperInvariant();
        var forwarder = Blank(request.ForwarderPartyCode)?.ToUpperInvariant();
        var customer = Blank(request.CustomerPartyCode)?.ToUpperInvariant();
        var booking = Blank(request.BookingRef)?.ToUpperInvariant();
        var anyParty = agent is not null || forwarder is not null || customer is not null;

        var moduleRow = await db.Modules.AsNoTracking().Where(m => m.ModuleCode == module && m.IsActive)
            .Select(m => new { m.IsOperational }).SingleOrDefaultAsync(ct);
        if (moduleRow is null) errors.Add("moduleCode", $"Unknown module '{module}'.");
        else if (!moduleRow.IsOperational) errors.Add("moduleCode", $"'{module}' does no depot work, so it has nothing to price.");

        if (!await db.Currencies.AnyAsync(c => c.CurrencyCode == currency && c.IsActive, ct))
            errors.Add("currencyCode", $"Unknown currency '{currency}'.");

        if (request.EffectiveTo is { } to && to < request.EffectiveFrom)
            errors.Add("effectiveTo", "The end date is before the start date.");

        // The same scope rules as the CHECK constraints, said in words.
        switch (type)
        {
            case ScheduleTypes.Public when anyParty || booking is not null:
                errors.Add("scheduleType", "A PUBLIC tariff applies to everyone — it cannot name parties or a booking.");
                break;
            case ScheduleTypes.Contract when !anyParty:
                errors.Add("scheduleType", "A CONTRACT names at least one of agent, forwarder or customer.");
                break;
            case ScheduleTypes.Contract when booking is not null:
                errors.Add("bookingRef", "A booking reference belongs on a SPOT tariff.");
                break;
            case ScheduleTypes.Spot when !anyParty && booking is null:
                errors.Add("scheduleType", "A SPOT tariff names a booking or at least one party.");
                break;
        }

        if (request.BranchId is { } branchId
            && !(await masterData.BranchesAsync([branchId], ct)).ContainsKey(branchId.ToString()))
            errors.Add("branchId", "Unknown branch.");

        var parties = await masterData.PartiesAsync(new[] { agent, forwarder, customer }.OfType<string>(), ct);
        PartyRef? Check(string field, string? code, Func<PartyRef, bool> plays, string role)
        {
            if (code is null) return null;
            if (!parties.TryGetValue(code, out var party)) { errors.Add(field, $"Unknown party '{code}'."); return null; }
            if (!party.IsActive) errors.Add(field, $"'{code}' is inactive.");
            if (!plays(party)) errors.Add(field, $"'{code}' is not set up as {role} in master data.");
            return party;
        }
        var agentRef = Check("agentPartyCode", agent, p => p.IsShippingLine, "a shipping line or agent");
        var forwarderRef = Check("forwarderPartyCode", forwarder, p => p.IsForwarder, "a forwarder");
        var customerRef = Check("customerPartyCode", customer, p => p.IsCustomer, "a customer");

        if (errors.Count > 0) return RevenueSupport.Invalid(errors);

        schedule.Name = request.Name;
        schedule.ModuleCode = module;
        schedule.ScheduleType = type;
        schedule.BranchId = request.BranchId;
        schedule.AgentPartyId = agentRef?.PartyId;
        schedule.AgentPartyCode = agentRef?.PartyCode;
        schedule.ForwarderPartyId = forwarderRef?.PartyId;
        schedule.ForwarderPartyCode = forwarderRef?.PartyCode;
        schedule.CustomerPartyId = customerRef?.PartyId;
        schedule.CustomerPartyCode = customerRef?.PartyCode;
        schedule.BookingRef = booking;
        schedule.CurrencyCode = currency;
        schedule.PricesIncludeTax = request.PricesIncludeTax;
        schedule.EffectiveFrom = request.EffectiveFrom;
        schedule.EffectiveTo = request.EffectiveTo;
        schedule.WaiveDamagedEmptyStorage = request.WaiveDamagedEmptyStorage;
        schedule.Remarks = request.Remarks;
        return null;
    }

    private static async Task<Results<Ok<ScheduleResponse>, NotFound, ValidationProblem, ProblemHttpResult>> SavedAsync(
        Schedule schedule, RevenueDbContext db, BranchCalendar calendar, CancellationToken ct)
    {
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        var row = await Project(db, db.Schedules.AsNoTracking().Where(s => s.ScheduleId == schedule.ScheduleId)).SingleAsync(ct);
        return TypedResults.Ok(Map(row, await calendar.TodayAsync(row.Row.BranchId, ct)));
    }

    internal sealed record ScheduleRow(Schedule Row, DateOnly? EffectiveUntil, bool FullySuperseded, int RateCount);

    // The view is keyless, and EF cannot null-test a keyless row after a left
    // join, so its two columns are read as correlated subqueries instead.
    private static IQueryable<ScheduleRow> Project(RevenueDbContext db, IQueryable<Schedule> schedules) =>
        schedules.Select(s => new ScheduleRow(
            s,
            db.VwScheduleEffectives.Where(e => e.ScheduleId == s.ScheduleId).Select(e => e.EffectiveUntil).FirstOrDefault(),
            db.VwScheduleEffectives.Any(e => e.ScheduleId == s.ScheduleId && e.IsFullySuperseded == true),
            db.TosRates.Count(r => r.ScheduleId == s.ScheduleId)));

    private static ScheduleResponse Map(ScheduleRow row, DateOnly today)
    {
        var s = row.Row;
        return new ScheduleResponse(
            s.ScheduleId, s.ScheduleNo, s.VersionNo, s.LineageId, s.Name,
            s.ModuleCode, s.ScheduleType, s.ScopeRank ?? 99, s.BranchId,
            s.AgentPartyCode, s.ForwarderPartyCode, s.CustomerPartyCode, s.BookingRef,
            s.CurrencyCode, s.PricesIncludeTax, s.EffectiveFrom, s.EffectiveTo,
            row.EffectiveUntil,
            s.Status,
            ScheduleLifecycle.Describe(s.Status, s.EffectiveFrom, row.EffectiveUntil, row.FullySuperseded, today),
            ScheduleLifecycle.IsEditable(s.Status),
            s.SubmittedAt, s.ApprovedAt, s.ApprovedBy, s.RejectionReason,
            s.WaiveDamagedEmptyStorage, s.Remarks, row.RateCount,
            Convert.ToBase64String(s.RowVersion));
    }
}
