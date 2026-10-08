using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.SharedKernel;
using Gecko.Tos.Application;
using Gecko.Tos.Domain;
using Gecko.Tos.Endpoints.Holds;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Endpoints.Gate;

/// <summary>
/// The container inquiry (owner 2026-10-08): every box that has been through the
/// caller's depots, ONE ROW PER BOX — its latest stay, in the yard now or gone out —
/// filterable by size, type, full/empty, in/out, line, agent and the rest; and one
/// box in full for the page's pop-up: that row, the registry, its holds and its story.
///
/// The booking a row names is the one the box is on now (open stay), else the one
/// it left on, else the one it came in on. Read-only; scoped like the gate (tos.gate.view per depot).
/// </summary>
internal static class ContainerInquiryEndpoints
{
    public const string InYard = "IN_YARD";
    public const string OutOfYard = "OUT_OF_YARD";

    public static RouteGroupBuilder MapContainerInquiryEndpoints(this RouteGroupBuilder tos)
    {
        tos.MapGet("/containers", ListAsync)
            .WithTags("TOS — gate")
            .RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("The container inquiry: one row per box, its latest stay at the caller's depots");

        tos.MapGet("/containers/{containerNo}", GetAsync)
            .WithTags("TOS — gate")
            .RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("One box for the inquiry's pop-up: its latest stay, registry entry, holds and story");

        return tos;
    }

    // Init-only, not positional: EF translates filters and sorts on a member-initialised projection, not on a constructor's.
    private sealed class Row
    {
        public Guid ContainerVisitId { get; init; }
        public Guid BranchId { get; init; }
        public string ContainerNo { get; init; } = null!;
        public string? EquipmentTypeCode { get; init; }
        public string FullEmpty { get; init; } = null!;
        public string LineCode { get; init; } = null!;
        public string? ConditionCode { get; init; }
        public string? GradeCode { get; init; }
        public Guid? YardId { get; init; }
        public string? PositionText { get; init; }
        public Guid GateInTransactionId { get; init; }
        public DateTimeOffset GateInAt { get; init; }
        public string GateInEirNo { get; init; } = null!;
        public string GateInMovementCode { get; init; } = null!;
        public Guid? GateOutTransactionId { get; init; }
        public DateTimeOffset? GateOutAt { get; init; }
        public string? GateOutEirNo { get; init; }
        public string? GateOutMovementCode { get; init; }
        public Guid? BookingId { get; init; }
        public string? OrderNo { get; init; }
        public string? CarrierRef { get; init; }
        public string? SubBlNo { get; init; }
        public string? AgentCode { get; init; }
        public string? CustomerCode { get; init; }
        public bool IsHeld { get; init; }
        public DateTimeOffset LastEventAt { get; init; }
    }

    /// <summary>Each box's latest stay (by gate-in time) at the depots asked for and covered.</summary>
    private static IQueryable<Row> Latest(TosDbContext db, ICallerPermissions scope, Guid? branchId)
    {
        var visits = db.ContainerVisits.AsNoTracking().Where(v => v.DeletedAt == null);
        if (branchId is not null) visits = visits.Where(v => v.BranchId == branchId);
        if (scope.BranchFilter(TosPermissions.GateView) is { } mine)
        {
            var allowed = mine.ToList();
            visits = visits.Where(v => allowed.Contains(v.BranchId));
        }

        var stays =
            from v in visits
            join gi in db.GateTransactions on v.GateInTransactionId equals gi.GateTransactionId
            select new { v, gi };
        var latest =
            from s in stays
            group s by s.v.ContainerNo into g
            select new { ContainerNo = g.Key, At = g.Max(x => x.gi.TransactionAt) };

        return
            from s in stays
            join l in latest on new { s.v.ContainerNo, At = s.gi.TransactionAt } equals new { l.ContainerNo, l.At }
            join go in db.GateTransactions on s.v.GateOutTransactionId equals go.GateTransactionId into gos
            from go in gos.DefaultIfEmpty()
            join bc in db.BookingContainers on s.v.CurrentBookingContainerId equals bc.BookingContainerId into bcs
            from bc in bcs.DefaultIfEmpty()
            join b in db.Bookings
                on (bc != null ? bc.BookingId : go != null ? go.BookingId : s.gi.BookingId) equals b.BookingId into bs
            from b in bs.DefaultIfEmpty()
            select new Row
            {
                ContainerVisitId = s.v.ContainerVisitId, BranchId = s.v.BranchId, ContainerNo = s.v.ContainerNo,
                EquipmentTypeCode = s.v.EquipmentTypeCode, FullEmpty = s.v.FullEmpty, LineCode = s.v.LinePartyCode,
                ConditionCode = s.v.ConditionCode, GradeCode = s.v.GradeCode, YardId = s.v.YardId, PositionText = s.v.PositionText,
                GateInTransactionId = s.gi.GateTransactionId, GateInAt = s.gi.TransactionAt,
                GateInEirNo = s.gi.EirNo, GateInMovementCode = s.gi.MovementCode,
                GateOutTransactionId = s.v.GateOutTransactionId,
                GateOutAt = go == null ? null : go.TransactionAt,
                GateOutEirNo = go == null ? null : go.EirNo,
                GateOutMovementCode = go == null ? null : go.MovementCode,
                BookingId = b == null ? null : b.BookingId,
                OrderNo = b == null ? null : b.OrderNo,
                CarrierRef = b == null ? null : b.CarrierRef,
                SubBlNo = b == null ? null : b.SubBlNo,
                AgentCode = b == null ? null : b.AgentPartyCode,
                CustomerCode = b == null ? null : b.CustomerPartyCode,
                IsHeld = db.VwActiveHolds.Any(h => h.ContainerNo == s.v.ContainerNo),
                LastEventAt = s.v.LastEventAt,
            };
    }

    private static async Task<Results<Ok<PagedResult<ContainerInquiryRowResponse>>, ValidationProblem, ProblemHttpResult>> ListAsync(
        [AsParameters] ListQuery query, TosDbContext db, IMasterDataReferences master, BranchClock clock,
        ICallerPermissions scope, TimeProvider time, CancellationToken ct,
        Guid? branchId = null, string? status = null, string? fullEmpty = null,
        string? sizeCode = null, string? equipmentTypeCode = null, bool? reefer = null,
        string? lineCode = null, string? agentCode = null, string? customerCode = null, string? booking = null,
        string? conditionCode = null, string? gradeCode = null, bool? heldOnly = null,
        DateTimeOffset? gateInFrom = null, DateTimeOffset? gateInTo = null, string? sort = null)
    {
        if (branchId is { } asked && !scope.HasAt(TosPermissions.GateView, asked))
            return TosScope.OutsideYourBranches("That depot is not one you cover.");

        var rows = Latest(db, scope, branchId);

        if (status.Clean()?.ToUpperInvariant() is { } where)
        {
            if (where is not (InYard or OutOfYard)) return TosSupport.Invalid("status", $"Use {InYard} or {OutOfYard}.");
            rows = where == InYard ? rows.Where(r => r.GateOutTransactionId == null) : rows.Where(r => r.GateOutTransactionId != null);
        }
        if (fullEmpty.Clean()?.ToUpperInvariant() is { } load)
        {
            if (load is not (GateRules.Full or GateRules.Empty)) return TosSupport.Invalid("fullEmpty", "Use FULL or EMPTY.");
            rows = rows.Where(r => r.FullEmpty == load);
        }
        if (equipmentTypeCode.Clean()?.ToUpperInvariant() is { } type) rows = rows.Where(r => r.EquipmentTypeCode == type);
        // Size and reefer are MDM's facts about a type: narrowed to the type codes that have them.
        if (sizeCode.Clean() is { } size || reefer is not null)
        {
            var known = (await master.ActiveEquipmentTypesAsync(ct)).Select(t => t.TypeCode).ToList();
            var facts = known.Count == 0 ? new Dictionary<string, EquipmentTypeRef>() : await master.EquipmentTypesAsync(known, ct);
            var codes = facts.Values
                .Where(t => sizeCode.Clean() is not { } s || string.Equals(t.SizeCode, s, StringComparison.OrdinalIgnoreCase))
                .Where(t => reefer is not { } r || t.IsReefer == r)
                .Select(t => t.TypeCode).ToList();
            rows = rows.Where(r => r.EquipmentTypeCode != null && codes.Contains(r.EquipmentTypeCode));
        }
        if (lineCode.Clean()?.ToUpperInvariant() is { } line) rows = rows.Where(r => r.LineCode == line);
        if (agentCode.Clean()?.ToUpperInvariant() is { } agent) rows = rows.Where(r => r.AgentCode == agent);
        if (customerCode.Clean()?.ToUpperInvariant() is { } customer) rows = rows.Where(r => r.CustomerCode == customer);
        if (booking.Clean() is { } named)
            rows = rows.Where(r => r.OrderNo!.Contains(named) || r.CarrierRef!.Contains(named) || r.SubBlNo!.Contains(named));
        if (conditionCode.Clean()?.ToUpperInvariant() is { } condition) rows = rows.Where(r => r.ConditionCode == condition);
        if (gradeCode.Clean()?.ToUpperInvariant() is { } grade) rows = rows.Where(r => r.GradeCode == grade);
        if (heldOnly == true) rows = rows.Where(r => r.IsHeld);
        if (gateInFrom is not null) rows = rows.Where(r => r.GateInAt >= gateInFrom);
        if (gateInTo is not null) rows = rows.Where(r => r.GateInAt < gateInTo);
        // The clerk keys part of a number ("MSKU811") as often as all of it.
        if (query.Search.Clean() is { } q && ContainerNumber.Normalise(q) is { Length: > 0 } box)
            rows = rows.Where(r => r.ContainerNo.Contains(box));

        IOrderedQueryable<Row> ordered;
        switch (sort.Clean()?.ToUpperInvariant() ?? "LAST_ACTIVITY")
        {
            case "LAST_ACTIVITY": ordered = rows.OrderByDescending(r => r.LastEventAt); break;
            case "GATE_IN": ordered = rows.OrderByDescending(r => r.GateInAt); break;
            case "OLDEST_IN": ordered = rows.OrderBy(r => r.GateInAt); break;
            case "CONTAINER_NO": ordered = rows.OrderBy(r => r.ContainerNo); break;
            default: return TosSupport.Invalid("sort", "Use LAST_ACTIVITY, GATE_IN, OLDEST_IN or CONTAINER_NO.");
        }

        var page = await ordered.ThenBy(r => r.ContainerNo).ToPagedAsync(query.Page, query.PageSize, ct);
        var items = await ProjectAsync(page.Items, master, clock, time, ct);
        return TypedResults.Ok(new PagedResult<ContainerInquiryRowResponse>(items, page.Page, page.PageSize, page.TotalCount));
    }

    private static async Task<Results<Ok<ContainerInquiryResponse>, NotFound, ValidationProblem, ProblemHttpResult>> GetAsync(
        string containerNo, TosDbContext db, IMasterDataReferences master, BranchClock clock,
        ICallerPermissions scope, TimeProvider time, CancellationToken ct, Guid? branchId = null)
    {
        var box = ContainerNumber.Normalise(containerNo);
        if (!ContainerNumber.IsWellFormed(box))
            return TosSupport.Invalid("containerNo", $"'{containerNo}' is not a container number (4 to 11 letters or digits).");
        if (branchId is { } asked && !scope.HasAt(TosPermissions.GateView, asked))
            return TosScope.OutsideYourBranches("That depot is not one you cover.");

        var latest = await Latest(db, scope, branchId).Where(r => r.ContainerNo == box).ToListAsync(ct);
        var story = await ContainerStoryEndpoints.ReadAsync(box, branchId, db, clock, scope, time, ct);
        // Never through these depots and on none of their bookings: the box is not one this caller can ask about.
        if (latest.Count == 0 && story.Bookings.Count == 0) return TypedResults.NotFound();

        var summary = (await ProjectAsync(latest, master, clock, time, ct)).FirstOrDefault();
        var registered = (await master.ContainersAsync([box], ct)).GetValueOrDefault(box);
        var holds = await HoldEndpoints.ActiveOnAsync(db, master, box, ct);

        var registry = registered is null ? null : new ContainerInquiryRegistryResponse(
            registered.ContainerId, registered.EquipmentTypeCode, registered.Status, registered.IsCheckDigitValid,
            registered.FixedPortCodes ?? []);
        return TypedResults.Ok(new ContainerInquiryResponse(box, summary, registry, holds, story));
    }

    private static async Task<List<ContainerInquiryRowResponse>> ProjectAsync(
        IReadOnlyList<Row> rows, IMasterDataReferences master, BranchClock clock, TimeProvider time, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var typeCodes = rows.Select(r => r.EquipmentTypeCode).OfType<string>().Distinct().ToList();
        var types = typeCodes.Count == 0 ? new Dictionary<string, EquipmentTypeRef>() : await master.EquipmentTypesAsync(typeCodes, ct);
        var partyCodes = rows.Select(r => r.LineCode).Concat(rows.Select(r => r.AgentCode)).Concat(rows.Select(r => r.CustomerCode))
            .OfType<string>().Distinct().ToList();
        var parties = await master.PartiesAsync(partyCodes, ct);
        var branches = await clock.BranchesAsync(rows.Select(r => r.BranchId).Distinct(), ct);
        var today = time.GetUtcNow().UtcDateTime.Date;

        return rows.Select(r =>
        {
            var type = r.EquipmentTypeCode is { } c ? types.GetValueOrDefault(c) : null;
            // Same arithmetic as yard.vw_container_in_yard and the story: calendar days between UTC dates.
            var days = Math.Max(((r.GateOutAt?.UtcDateTime.Date ?? today) - r.GateInAt.UtcDateTime.Date).Days, 0);
            return new ContainerInquiryRowResponse(
                r.ContainerNo, r.GateOutTransactionId is null ? InYard : OutOfYard,
                r.ContainerVisitId, r.BranchId, branches.GetValueOrDefault(r.BranchId)?.BranchCode,
                r.EquipmentTypeCode, type?.SizeCode, type?.HeightClass, type?.IsReefer ?? false, r.FullEmpty,
                r.LineCode, parties.GetValueOrDefault(r.LineCode)?.Name,
                r.AgentCode, r.AgentCode is { } a ? parties.GetValueOrDefault(a)?.Name : null,
                r.CustomerCode, r.CustomerCode is { } cu ? parties.GetValueOrDefault(cu)?.Name : null,
                r.ConditionCode, r.GradeCode, r.YardId, r.PositionText,
                r.GateInTransactionId, r.GateInAt, r.GateInEirNo, r.GateInMovementCode,
                r.GateOutTransactionId, r.GateOutAt, r.GateOutEirNo, r.GateOutMovementCode,
                days, r.IsHeld, r.BookingId, r.OrderNo, r.CarrierRef, r.SubBlNo, r.LastEventAt);
        }).ToList();
    }
}

/// <summary>
/// One box on the inquiry: its latest stay. <c>Status</c> is IN_YARD or OUT_OF_YARD; <c>DaysInYard</c>
/// runs to today while in, to the gate-out when gone. The booking is the one it is on now, else left on, else came in on.
/// </summary>
public sealed record ContainerInquiryRowResponse(
    string ContainerNo, string Status,
    Guid ContainerVisitId, Guid BranchId, string? BranchCode,
    string? EquipmentTypeCode, string? SizeCode, string? HeightClass, bool IsReefer, string FullEmpty,
    string LineCode, string? LineName, string? AgentCode, string? AgentName, string? CustomerCode, string? CustomerName,
    string? ConditionCode, string? GradeCode, Guid? YardId, string? PositionText,
    Guid GateInTransactionId, DateTimeOffset GateInAt, string GateInEirNo, string GateInMovementCode,
    Guid? GateOutTransactionId, DateTimeOffset? GateOutAt, string? GateOutEirNo, string? GateOutMovementCode,
    int DaysInYard, bool IsHeld,
    Guid? BookingId, string? OrderNo, string? CarrierRef, string? SubBlNo, DateTimeOffset LastEventAt);

/// <summary>What the master-data registry says the box is; null when it is not registered.</summary>
public sealed record ContainerInquiryRegistryResponse(
    Guid ContainerId, string? EquipmentTypeCode, string Status, bool IsCheckDigitValid, IReadOnlyList<string> FixedPortCodes);

/// <summary>The pop-up: <c>Latest</c> is null for a box only booked, never gated in.</summary>
public sealed record ContainerInquiryResponse(
    string ContainerNo, ContainerInquiryRowResponse? Latest, ContainerInquiryRegistryResponse? Registry,
    IReadOnlyList<ActiveHoldResponse> Holds, ContainerStoryResponse Story);
