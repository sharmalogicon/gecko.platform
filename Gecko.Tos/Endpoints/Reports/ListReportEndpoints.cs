using Gecko.Data;
using Gecko.Data.Documents;
using Gecko.Identity.Contracts;
using Gecko.MasterData.Contracts;
using Gecko.SharedKernel;
using Gecko.Tos.Application;
using Gecko.Tos.Application.Reports;
using Gecko.Tos.Domain;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Gecko.Tos.Endpoints.Reports;

/// <summary>
/// Vector's operation reports rebuilt over Gecko (owner 2026-10-08) — the RDL's columns, headings and
/// summary exactly, as an A3 landscape PDF or an Excel sheet:
///
///   GET /reports/gate-in-out.{pdf|xlsx}     TMS.Operation.InBound.GateInOut
///   GET /reports/empty-in-yard.{pdf|xlsx}   TMS.Operation.OutBound.EmptyInYard
///   GET /reports/full-in-yard.{pdf|xlsx}    TMS.Operation.OutBound.FullInYard
///   GET /reports/outbound-container-storage-activity.{pdf|xlsx}   TMS.Operation.OutBound.ContainerStorageActivity
///   GET /reports/daily-import-full-in.{pdf|xlsx}   TMS.Operation.InBound.DailyImportFullIn
///   GET /reports/empty-out.{pdf|xlsx}   TMS.Operation.InBound.EmptyOut ("EMPTY OUT" and "INBOUND Empty Out")
///   GET /reports/import-export-summary.{pdf|xlsx}   TMS.Operation.InBound.ImportExportSummary
///   GET /reports/inbound-full-in.{pdf|xlsx}   TMS.Operation.InBound.NYKExternal (the desktop's "NYK External")
///   GET /reports/customs-import-unstuffing.{pdf|xlsx}   TMS.Operation.InBound.CustomsImportUnStuffing
///   GET /reports/daily-unstuffing.{pdf|xlsx}   TMS.Operation.InBound.DailyUnStuffing (no lines until unstuffing is recorded)
///   GET /reports/import-container-list.{pdf|xlsx}   TMS.Operation.InBound.ImportContainerList
///   GET /reports/pti.{pdf|xlsx}   TMS.Operation.OutBound.PTI (no lines until PTI work orders are recorded)
///   GET /reports/container-repair-summary.{pdf|xlsx}   TMS.Operation.OutBound.ContainerRepairSummaryList (likewise, repair estimates)
///   GET /reports/truck-turn-around.{pdf|xlsx}   TMS.Operation.OutBound.TruckTurnAround
///   GET /reports/stock-report.{pdf|xlsx}   TMS.Operation.OutBound.StockReport
///   GET /reports/stuffing-unstuffing.{pdf|xlsx}   TMS.Operation.OutBound.StuffingUnstuffing
///   GET /reports/shore-pass-ect.{pdf|xlsx}   TMS.Operation.OutBound.ShorePassECT
///   GET /reports/shore-pass-1.{pdf|xlsx}, -2, -3   TMS.Operation.OutBound.ShorePass1 / 2 / 3 (Evergreen B.2, LCMT, Kerry Siam)
///
/// Vector's BranchID is the depot (branchId), its YardLocation the yard (yardId). Read-only;
/// tos.gate.view at that depot.
/// </summary>
internal static class ListReportEndpoints
{
    public static RouteGroupBuilder MapListReportEndpoints(this RouteGroupBuilder tos)
    {
        var reports = tos.MapGroup("/reports").WithTags("TOS — reports");

        reports.MapGet("/gate-in-out.{format}", GateInOutAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("Gate In/Out report (Vector GateInOut): the depot's moves over a range of days, as PDF or Excel");
        reports.MapGet("/empty-in-yard.{format}", EmptyInYardAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("Outbound Empty Container In Yard (Vector EmptyInYard), as PDF or Excel");
        reports.MapGet("/full-in-yard.{format}", FullInYardAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("Full Container In Yard (Vector FullInYard), as PDF or Excel");

        reports.MapGet("/outbound-container-storage-activity.{format}", StorageActivityAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("Outbound Container Storage Activity (Vector OutBound.ContainerStorageActivity): export boxes by vessel ETA, as PDF or Excel")
            .WithDescription("dateFrom/dateTo: the depot days the vessel's ETA falls in (Vector's FromETD/ToETD).");
        reports.MapOperation("/daily-import-full-in.{format}",
                r => InboundReports.DailyImportFullInAsync(r.Db, r.Master, r.Heading, r.From, r.To, r.Q.AgentCode, r.Ct))
            .WithSummary("Daily IMPORT FULL-IN (Vector InBound.DailyImportFullIn): import full gate-ins per agent and day by CY/CYD/CFS and size");
        reports.MapOperation("/empty-out.{format}",
                r => InboundReports.EmptyOutAsync(r.Db, r.Master, r.Heading, r.From, r.To, r.Q.AgentCode, r.Ct))
            .WithSummary("INBOUND EMPTY OUT REPORT (Vector InBound.EmptyOut): every empty gate-out in the days asked for, and its size count");
        reports.MapOperation("/import-export-summary.{format}",
                r => InboundReports.ImportExportSummaryAsync(r.Db, r.Master, r.Heading, r.Q.Year!.Value, r.Q.MovementCode, r.Q.BookingType, r.Ct),
                range: false, check: q =>
                    q.Year is not (>= 2000 and <= 2100) ? TosSupport.Invalid("year", "Which year?")
                    : q.MovementCode is { Length: > 0 } m && OperationReportKit.Movement(m) is null
                        ? TosSupport.Invalid("movementCode", "FULL IN, MTY IN, FULL OUT or MTY OUT.") : null)
            .WithSummary("SUMMARY (IMPORT - EXPORT) (Vector InBound.ImportExportSummary): a year's moves per agent and month, CONT and TEU");
        reports.MapOperation("/inbound-full-in.{format}",
                r => InboundReports.NykExternalAsync(r.Db, r.Master, r.Heading, r.From, r.To, r.Q.AgentCode, r.Q.BookingType, r.Q.OrderType, r.Ct))
            .WithSummary("INBOUND FULL IN REPORT (Vector InBound.NYKExternal, the desktop's NYK External menu): full gate-ins with seals and weight");
        reports.MapOperation("/customs-import-unstuffing.{format}",
                r => InboundReports.CustomsImportUnstuffingAsync(r.Db, r.Master, r.Heading, r.From, r.To, r.Q.AgentCode, r.Q.VesselCode, r.Q.VoyageNo, r.Ct))
            .WithSummary("IMPORT - Unstuffing Customs (Vector InBound.CustomsImportUnStuffing, กศก 84): import full gate-ins with their gate-out");
        reports.MapOperation("/daily-unstuffing.{format}", r => Task.FromResult(InboundReports.DailyUnstuffing(r.Heading, r.From, r.To)))
            .WithSummary("DAILY UNSTUFFING REPORT (Vector InBound.DailyUnStuffing): heading and columns only until Gecko records unstuffing");
        reports.MapOperation("/import-container-list.{format}",
                r => InboundReports.ImportContainerListAsync(r.Db, r.Master, r.Heading, r.From, r.To,
                    r.Q.AgentCode, r.Q.CustomerCode, r.Q.VesselCode, r.Q.VoyageNo, r.Ct))
            .WithSummary("IMPORT Container List (Vector InBound.ImportContainerList): import boxes by vessel ETA, a page per agent/vessel/voyage")
            .WithDescription("dateFrom/dateTo: the depot days the vessel's ETA falls in (Vector's BookingDateFrom/To).");
        reports.MapOperation("/pti.{format}", r => Task.FromResult(OutboundReports.Pti(r.Heading, r.From, r.To)))
            .WithSummary("PTI REPORT (Vector OutBound.PTI): heading and columns only until Gecko records PTI work orders");
        reports.MapOperation("/container-repair-summary.{format}", r => Task.FromResult(OutboundReports.ContainerRepairSummary(r.Heading, r.From, r.To)))
            .WithSummary("Container Repair Summary (Vector OutBound.ContainerRepairSummaryList): heading, columns and TOTAL until Gecko records repair estimates");
        reports.MapOperation("/truck-turn-around.{format}",
                r => OutboundReports.TruckTurnAroundAsync(r.Db, r.Master, r.Heading, r.From, r.To, r.Q.AgentCode, r.Ct))
            .WithSummary("Truck Turn Around (Vector OutBound.TruckTurnAround, มาตรฐานปล่อยตู้เปล่า CY): empty gate-outs with the truck's in/out minutes");
        reports.MapOperation("/stock-report.{format}",
                r => OutboundReports.StockReportAsync(r.Db, r.Master, r.Heading, r.From, r.To, r.Q.AgentCode, r.Ct))
            .WithSummary("STOCK REPORT (Vector OutBound.StockReport): empty releases to export bookings with the box's in/out dates");
        reports.MapOperation("/stuffing-unstuffing.{format}",
                r => OutboundReports.StuffingUnstuffingAsync(r.Db, r.Master, r.Heading, r.From, r.To,
                    new StuffingFilter(r.Q.AgentCode, r.Q.CustomerCode, r.Q.BookingBlNo, r.Q.VesselCode, r.Q.VoyageNo, r.Q.BookingType, r.Q.OrderType), r.Ct))
            .WithSummary("Stuffing - UnStuffing (Vector OutBound.StuffingUnstuffing): full gate-ins of booked boxes with their booking dates");
        reports.MapOperation("/shore-pass-ect.{format}",
                r => OutboundReports.ShorePassEctAsync(r.Db, r.Master, r.Heading, r.From, r.To, r.Q.AgentCode, r.Q.BookingType, r.Q.LoadingPort, r.Ct))
            .WithSummary("Shore Pass ECT (Vector OutBound.ShorePassECT): booked boxes by required date, with weight and destination")
            .WithDescription("dateFrom/dateTo: the boxes' required dates. loadingPort: the booking's port of loading.");
        reports.MapOperation("/shore-pass-1.{format}",
                r => ShorePassReports.EvergreenAsync(r.Db, r.Master, r.Heading, r.From, r.To, r.Q.AgentCode, r.Ct))
            .WithSummary("Shore Pass #1 (Vector OutBound.ShorePass1): the full gate-ins on Evergreen's B.2 request form");
        reports.MapOperation("/shore-pass-2.{format}",
                r => ShorePassReports.LcmtAsync(r.Db, r.Master, r.Heading, r.From, r.To, r.Q.AgentCode, r.Ct))
            .WithSummary("Shore Pass #2 (Vector OutBound.ShorePass2): the full gate-ins on LCMT's request form");
        reports.MapOperation("/shore-pass-3.{format}",
                r => ShorePassReports.KerryAsync(r.Db, r.Master, r.Heading, r.From, r.To, r.Q.AgentCode, r.Ct))
            .WithSummary("Shore Pass #3 (Vector OutBound.ShorePass3): the full gate-ins on Kerry Siam Seaport's request form");

        return tos;
    }

    private sealed record Context(BranchClock.Branch Branch, string BranchName, string PrintedBy, DateTimeOffset Now)
    {
        public ReportHeading Heading => new(Branch, BranchName, PrintedBy, Now);
    }

    private static async Task<(Context? Context, IResult? Refused)> ContextAsync(
        Guid? branchId, string format, BranchClock clock, IUserDirectory users, ITenantContext caller, ICallerPermissions scope, CancellationToken ct)
    {
        if (format is not ("pdf" or "xlsx")) return (null, TosSupport.Invalid("format", "Ask for .pdf or .xlsx."));
        if (branchId is null) return (null, TosSupport.Invalid("branchId", "Which depot? The report is one depot's (Vector's BranchID)."));
        var branch = (await clock.BranchesAsync([branchId.Value], ct)).GetValueOrDefault(branchId.Value);
        if (branch is null) return (null, TypedResults.NotFound());
        if (!scope.HasAt(TosPermissions.GateView, branch.BranchId)) return (null, TosScope.OutsideYourBranches("That depot is not one you cover."));

        var name = (await users.BranchNamesAsync([branch.BranchId], ct)).GetValueOrDefault(branch.BranchId) ?? branch.BranchCode;
        var by = caller.UserId() is { } me ? (await users.DisplayNamesAsync([me], ct)).GetValueOrDefault(me) : null;
        return (new Context(branch, name, by ?? "", clock.LocalNow(branch)), null);
    }

    private static IResult File(ListReport report, string format) => format == "pdf"
        ? TypedResults.File(report.Pdf(), ListReport.PdfType, report.FileName + ".pdf")
        : TypedResults.File(report.Xlsx(), ListReport.XlsxType, report.FileName + ".xlsx");

    private static IResult File(TabularReport report, string format) => format == "pdf"
        ? TypedResults.File(report.Pdf(), TabularReport.PdfType, report.FileName + ".pdf")
        : TypedResults.File(report.Xlsx(), TabularReport.XlsxType, report.FileName + ".xlsx");

    /// <summary>The inclusive depot-day range most Operation reports take; null when it is fine.</summary>
    private static IResult? InvalidRange(DateOnly? from, DateOnly? to, string fromName = "dateFrom", string toName = "dateTo")
    {
        if (from is null) return TosSupport.Invalid(fromName, "The first day of the report.");
        if (to is null) return TosSupport.Invalid(toName, "The last day of the report.");
        if (to < from) return TosSupport.Invalid(toName, "The last day is before the first.");
        return to.Value.DayNumber - from.Value.DayNumber + 1 > ReportEndpoints.MaxDays
            ? TosSupport.Invalid(toName, $"At most {ReportEndpoints.MaxDays} days in one report.")
            : null;
    }

    private static async Task<IResult> StorageActivityAsync(
        string format, TosDbContext db, IMasterDataReferences master, BranchClock clock, IUserDirectory users,
        ITenantContext caller, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, DateOnly? dateFrom = null, DateOnly? dateTo = null, string? agentCode = null, string? vesselCode = null,
        string? voyageNo = null, string? orderType = null, string? bookingType = null)
    {
        var (context, refused) = await ContextAsync(branchId, format, clock, users, caller, scope, ct);
        if (refused is not null) return refused;
        if (InvalidRange(dateFrom, dateTo) is { } bad) return bad;
        return File(await StorageActivityReport.BuildAsync(db, master, context!.Branch, context.BranchName, context.PrintedBy, context.Now,
            new StorageActivityFilter(dateFrom!.Value, dateTo!.Value, agentCode, vesselCode, voyageNo, orderType, bookingType), ct), format);
    }

    /// <summary>
    /// An Operation report taking the common query (<see cref="OperationReportQuery"/>): the depot checked, the date range
    /// required and checked unless <paramref name="range"/> is false, then <paramref name="build"/>'s report as PDF or Excel.
    /// </summary>
    private static RouteHandlerBuilder MapOperation(this RouteGroupBuilder reports, string pattern,
        Func<OperationReportCall, Task<TabularReport>> build, bool range = true, Func<OperationReportQuery, IResult?>? check = null) =>
        reports.MapGet(pattern, async (string format, [AsParameters] OperationReportQuery q, TosDbContext db, IMasterDataReferences master,
            BranchClock clock, IUserDirectory users, ITenantContext caller, ICallerPermissions scope, CancellationToken ct) =>
        {
            var (context, refused) = await ContextAsync(q.BranchId, format, clock, users, caller, scope, ct);
            if (refused is not null) return refused;
            if (range && InvalidRange(q.DateFrom, q.DateTo) is { } bad) return bad;
            if (check?.Invoke(q) is { } wrong) return wrong;
            return File(await build(new OperationReportCall(db, master, context!.Heading, q, q.DateFrom ?? default, q.DateTo ?? default, ct)), format);
        }).RequireBranchPermission(TosPermissions.GateView);

    private static async Task<IResult> GateInOutAsync(
        string format, TosDbContext db, IMasterDataReferences master, BranchClock clock, IUserDirectory users,
        ITenantContext caller, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, DateOnly? dateFrom = null, DateOnly? dateTo = null,
        string? agentCode = null, string? forwarderCode = null, string? customerCode = null, string? vesselCode = null,
        string? voyageNo = null, string? size = null, string? type = null, string? fullEmpty = null, string? bookingType = null,
        string? orderType = null, string? bookingBlNo = null, string? haulierCode = null, string? movementCode = null, Guid? yardId = null)
    {
        var (context, refused) = await ContextAsync(branchId, format, clock, users, caller, scope, ct);
        if (refused is not null) return refused;
        if (dateFrom is null) return TosSupport.Invalid("dateFrom", "The first day of the report.");
        if (dateTo is null) return TosSupport.Invalid("dateTo", "The last day of the report.");
        if (dateTo < dateFrom) return TosSupport.Invalid("dateTo", "The last day is before the first.");
        if (dateTo.Value.DayNumber - dateFrom.Value.DayNumber + 1 > ReportEndpoints.MaxDays)
            return TosSupport.Invalid("dateTo", $"At most {ReportEndpoints.MaxDays} days in one report.");
        if (Invalid(fullEmpty) is { } bad) return bad;

        var report = await GateInOutReport.BuildAsync(db, master, context!.Branch, context.BranchName, context.PrintedBy, context.Now,
            new GateInOutFilter(dateFrom.Value, dateTo.Value, agentCode, forwarderCode, customerCode, vesselCode, voyageNo, size, type,
                fullEmpty, bookingType, orderType, bookingBlNo, haulierCode, movementCode, yardId), ct);
        return File(report, format);
    }

    private static async Task<IResult> EmptyInYardAsync(
        string format, TosDbContext db, IMasterDataReferences master, BranchClock clock, IUserDirectory users,
        ITenantContext caller, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, DateOnly? fromDate = null, DateOnly? toDate = null,
        string? agentCode = null, string? ownerCode = null, string? forwarderCode = null, string? customerCode = null,
        string? vesselCode = null, string? voyageNo = null, string? size = null, string? type = null, string? bookingType = null,
        string? orderType = null, string? bookingBlNo = null, string? haulierCode = null, Guid? yardId = null)
    {
        var (context, refused) = await ContextAsync(branchId, format, clock, users, caller, scope, ct);
        if (refused is not null) return refused;

        var report = await InYardReports.EmptyAsync(db, master, context!.Branch, context.BranchName, context.PrintedBy, context.Now,
            clock.Today(context.Branch),
            new InYardFilter(fromDate, toDate, agentCode, ownerCode, forwarderCode, customerCode, vesselCode, voyageNo, size, type,
                null, bookingType, orderType, bookingBlNo, haulierCode, yardId), ct);
        return File(report, format);
    }

    private static async Task<IResult> FullInYardAsync(
        string format, TosDbContext db, IMasterDataReferences master, BranchClock clock, IUserDirectory users,
        ITenantContext caller, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, DateOnly? fromDate = null, DateOnly? toDate = null,
        string? agentCode = null, string? forwarderCode = null, string? customerCode = null, string? vesselCode = null,
        string? voyageNo = null, string? containerSize = null, string? containerType = null, string? containerGrade = null,
        string? bookingType = null, string? orderType = null, string? bookingBlNo = null, string? haulierCode = null, Guid? yardId = null)
    {
        var (context, refused) = await ContextAsync(branchId, format, clock, users, caller, scope, ct);
        if (refused is not null) return refused;

        var report = await InYardReports.FullAsync(db, master, context!.Branch, context.BranchName, context.PrintedBy, context.Now,
            clock.Today(context.Branch),
            new InYardFilter(fromDate, toDate, agentCode, null, forwarderCode, customerCode, vesselCode, voyageNo, containerSize, containerType,
                containerGrade, bookingType, orderType, bookingBlNo, haulierCode, yardId), ct);
        return File(report, format);
    }

    private static IResult? Invalid(string? fullEmpty) =>
        GateInOutReport.Clean(fullEmpty) is { } load && load is not (GateRules.Full or GateRules.Empty)
            ? TosSupport.Invalid("fullEmpty", "Use FULL or EMPTY.")
            : null;
}

/// <summary>
/// The Operation reports' query: Vector's parameters under one name each (BranchID = branchId; the From/To dates =
/// dateFrom/dateTo, depot days, both inclusive). Each report reads the ones its RDL takes and ignores the rest.
/// </summary>
public sealed class OperationReportQuery
{
    public Guid? BranchId { get; init; }
    public DateOnly? DateFrom { get; init; }
    public DateOnly? DateTo { get; init; }
    public string? AgentCode { get; init; }
    public string? CustomerCode { get; init; }
    public string? VesselCode { get; init; }
    public string? VoyageNo { get; init; }
    public string? BookingType { get; init; }
    public string? OrderType { get; init; }
    public string? MovementCode { get; init; }
    public string? BookingBlNo { get; init; }
    public string? LoadingPort { get; init; }
    public int? Year { get; init; }
}

internal sealed record OperationReportCall(
    TosDbContext Db, IMasterDataReferences Master, ReportHeading Heading, OperationReportQuery Q, DateOnly From, DateOnly To, CancellationToken Ct);
