using Gecko.Data;
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

        return tos;
    }

    private sealed record Context(BranchClock.Branch Branch, string BranchName, string PrintedBy, DateTimeOffset Now);

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
