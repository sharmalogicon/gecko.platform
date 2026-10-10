using Gecko.Data;
using Gecko.Data.Documents;
using Gecko.Identity.Contracts;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Application;
using Gecko.Revenue.Application.Reports;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.SharedKernel;
using Gecko.Tos.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Gecko.Revenue.Endpoints.Reports;

/// <summary>
/// Vector's accounting reports (TMS.Accounting.*) rebuilt over Gecko receipts, as PDF or Excel
/// (owner 2026-10-10 — ACCOUNTING_REPORTS_GAP_ANALYSIS.md):
///
///   GET /reports/accounting/sales-tax.{pdf|xlsx}         TMS.Accounting.SalesTax
///   GET /reports/accounting/withholding-tax.{pdf|xlsx}   TMS.Accounting.WithholdingTax
///
/// Vector's BranchID is the depot (branchId), DateFrom/DateTo the depot's days (dateFrom/dateTo, both
/// inclusive). Read by revenue.charge.view at that depot, as /reports/receipts is.
/// </summary>
internal static class AccountingReportEndpoints
{
    public static RouteGroupBuilder MapAccountingReportEndpoints(this RouteGroupBuilder revenue)
    {
        var reports = revenue.MapGroup("/reports/accounting").WithTags("Revenue — reports");

        reports.MapGet("/sales-tax.{format}", SalesTaxAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("Sales Tax report (Vector TMS.Accounting.SalesTax): the cash receipts' value and VAT, as PDF or Excel")
            .WithDescription("bookingType (IMPORT, EXPORT, REPO, INTERNAL) keeps the receipts with a line for a booking of that type.");
        reports.MapGet("/withholding-tax.{format}", WithholdingTaxAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("Withholding Tax report (Vector TMS.Accounting.WithholdingTax): receipts with tax withheld, as PDF or Excel");

        return revenue;
    }

    private static readonly string[] BookingTypes = ["IMPORT", "EXPORT", "REPO", "INTERNAL"];

    /// <summary>The shared parameter checks; a 400 names the field, a depot outside the caller's is 403.</summary>
    private static async Task<(AccountingReportContext? Context, IResult? Refused)> ContextAsync(
        string format, Guid? branchId, DateOnly? dateFrom, DateOnly? dateTo, BranchCalendar calendar, IMasterDataReferences master,
        IUserDirectory users, ITenantContext caller, ICallerPermissions scope, CancellationToken ct)
    {
        if (format is not ("pdf" or "xlsx")) return (null, RevenueSupport.Invalid("format", "Ask for .pdf or .xlsx."));
        if (branchId is null) return (null, RevenueSupport.Invalid("branchId", "Which depot? The report is one depot's (Vector's BranchID)."));
        if (dateFrom is null) return (null, RevenueSupport.Invalid("dateFrom", "The first day of the report."));
        if (dateTo is null) return (null, RevenueSupport.Invalid("dateTo", "The last day of the report."));
        if (dateTo < dateFrom) return (null, RevenueSupport.Invalid("dateTo", "The last day is before the first."));
        if (dateTo.Value.DayNumber - dateFrom.Value.DayNumber + 1 > ReceiptReportEndpoints.MaxDays)
            return (null, RevenueSupport.Invalid("dateTo", $"At most {ReceiptReportEndpoints.MaxDays} days in one report."));

        var branch = await calendar.BranchAsync(branchId.Value, ct);
        if (branch is null) return (null, TypedResults.NotFound());
        if (!scope.HasAt(RevenuePermissions.ChargeView, branch.BranchId))
            return (null, TypedResults.Problem(title: "Outside your branches", detail: "That depot is not one you cover.",
                statusCode: StatusCodes.Status403Forbidden));

        var name = (await users.BranchNamesAsync([branch.BranchId], ct)).GetValueOrDefault(branch.BranchId) ?? branch.BranchCode;
        var me = caller.UserId();
        var by = (await users.DisplayNamesAsync([me], ct)).GetValueOrDefault(me) ?? "";
        var seller = await master.InvoicingCompanyAsync(branch.BranchId, ct);

        return (new AccountingReportContext(branch, name, dateFrom.Value, dateTo.Value,
            branch.StartOfDay(dateFrom.Value), branch.StartOfDay(dateTo.Value.AddDays(1)),
            by, branch.Local(calendar.Now), AccountingReports.LetterheadOf(seller)), null);
    }

    private static IResult File(TabularReport report, string format) => format == "pdf"
        ? TypedResults.File(report.Pdf(), TabularReport.PdfType, report.FileName + ".pdf")
        : TypedResults.File(report.Xlsx(), TabularReport.XlsxType, report.FileName + ".xlsx");

    private static async Task<IResult> SalesTaxAsync(
        string format, RevenueDbContext db, ITosBookingHeaders tos, BranchCalendar calendar, IMasterDataReferences master,
        IUserDirectory users, ITenantContext caller, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, DateOnly? dateFrom = null, DateOnly? dateTo = null, string? bookingType = null)
    {
        var type = string.IsNullOrWhiteSpace(bookingType) ? null : bookingType.Trim().ToUpperInvariant();
        if (type is not null && !BookingTypes.Contains(type))
            return RevenueSupport.Invalid("bookingType", "Use IMPORT, EXPORT, REPO or INTERNAL, or leave it out for all.");
        var (context, refused) = await ContextAsync(format, branchId, dateFrom, dateTo, calendar, master, users, caller, scope, ct);
        if (refused is not null) return refused;

        return File(await AccountingReports.SalesTaxAsync(db, tos, context!, type, ct), format);
    }

    private static async Task<IResult> WithholdingTaxAsync(
        string format, RevenueDbContext db, BranchCalendar calendar, IMasterDataReferences master,
        IUserDirectory users, ITenantContext caller, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, DateOnly? dateFrom = null, DateOnly? dateTo = null)
    {
        var (context, refused) = await ContextAsync(format, branchId, dateFrom, dateTo, calendar, master, users, caller, scope, ct);
        if (refused is not null) return refused;

        return File(await AccountingReports.WithholdingTaxAsync(db, context!, ct), format);
    }
}
