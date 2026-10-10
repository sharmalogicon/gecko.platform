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
///   GET /reports/accounting/cash-receipt-by-user.{pdf|xlsx}      TMS.Accounting.CashReceiptByUser
///   GET /reports/accounting/cash-receipt-by-liner.{pdf|xlsx}     Tms.Accounting.CashReceiptByLiner
///   GET /reports/accounting/cash-receipt-by-company.{pdf|xlsx}   TMS.Accounting.CashReceiptByCompany
///   GET /reports/accounting/credit-invoice-listing.{pdf|xlsx}    TMS.Accounting.CreditInvoiceListing
///   GET /reports/accounting/lift-off-washing.{pdf|xlsx}          TMS.Accounting.LiftOffWashing
///   GET /reports/accounting/container-storage-activity.{pdf|xlsx}   TMS.Accounting.ContainerStorageActivityStandard
///   GET /reports/accounting/container-storage-activity-by-vessel.{pdf|xlsx}   TMS.Accounting.ContainerStorageActivityByVslVoy
///   GET /reports/accounting/export-full-out.{pdf|xlsx}   TMS.Accounting.ExportFullOut
///   GET /reports/accounting/monitoring-day.{pdf|xlsx}   TMS.Accounting.MonitoringDay
///
/// The User and Liner listings take Vector's date AND time range (dateFrom/dateTo as local date-times, both
/// inclusive); a dateTo with no time means the whole of that day.
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
        reports.MapGet("/cash-receipt-by-user.{format}", ByUserAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("Cash Receipt Listing by user (Vector TMS.Accounting.CashReceiptByUser), as PDF or Excel")
            .WithDescription("dateFrom/dateTo are depot date-times (yyyy-MM-ddTHH:mm). cashierUserId keeps one cashier's receipts; bookingType as for sales-tax.");
        reports.MapGet("/cash-receipt-by-liner.{format}", ByLinerAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("Cash Receipt Listing by shipping line (Vector CashReceiptByLiner), as PDF or Excel")
            .WithDescription("dateFrom/dateTo are depot date-times (yyyy-MM-ddTHH:mm). agentCode keeps one shipping line's receipts.");
        reports.MapGet("/cash-receipt-by-company.{format}", ByCompanyAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("Cash Receipt Detail List by company (Vector CashReceiptByCompany), as PDF or Excel");
        reports.MapGet("/credit-invoice-listing.{format}", CreditInvoiceListingAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("Credit Invoice Listing (Vector TMS.Accounting.CreditInvoiceListing): issued credit invoices, EXS and IMS, as PDF or Excel")
            .WithDescription("agentCode = a shipping line on the invoice's bookings; customerCode = the payer; bookingType as for sales-tax.");
        reports.MapGet("/container-storage-activity.{format}", StorageActivityAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("Container Storage Activity (Vector TMS.Accounting.ContainerStorageActivityStandard), as PDF or Excel")
            .WithDescription("dateFrom/dateTo: the booking's vessel ETA, or the box's first gate-in when the booking has no vessel call. agentCode = the shipping line; voyageNo matches voyage in or out; bookingBlNo = the carrier's B/L or booking no.");
        reports.MapGet("/container-storage-activity-by-vessel.{format}", StorageActivityByVesselAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("Container Storage Activity by vessel/voyage (Vector ContainerStorageActivityByVslVoy), as PDF or Excel")
            .WithDescription("The IMPORT boxes billed in dateFrom..dateTo (receipt date for cash, invoice date for credit). agentCode = the shipping line; voyageNo; bookingBlNo = the carrier's B/L.");
        reports.MapGet("/export-full-out.{format}", ExportFullOutAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("Export Full Out (CY/CY) (Vector ExportFullOut), as PDF or Excel")
            .WithDescription("Laden lift-on of EXPORT boxes' full gate-outs billed in dateFrom..dateTo (receipt date for cash, invoice date for credit), a row per receipt/invoice. agentCode = the shipping line; voyageNo.");
        reports.MapGet("/monitoring-day.{format}", MonitoringDayAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("Monitoring (Day) (Vector MonitoringDay): monitoring charges for reefer containers, as PDF or Excel")
            .WithDescription("No dates: all of the depot's history, as the RDL. agentCode = the shipping line; bookingType IMPORT/EXPORT/REPO/INTERNAL; vesselCode; voyageNo.");
        reports.MapGet("/lift-off-washing.{format}", LiftOffWashingAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("Lift Off - Washing (Vector TMS.Accounting.LiftOffWashing): each container moved and its lift-off and washing charges, as PDF or Excel")
            .WithDescription("movementCode defaults to MTY_IN (Vector's MTY IN); agentCode = the shipping line; size/type as \"20\"/\"GP\".");

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

    /// <summary>
    /// The User/Liner listings' window: from <paramref name="from"/> to <paramref name="to"/> inclusive, depot time;
    /// a <paramref name="to"/> at midnight means that whole day.
    /// </summary>
    private static async Task<(AccountingReportContext? Context, DateTime To, IResult? Refused)> TimedContextAsync(
        string format, Guid? branchId, DateTime? from, DateTime? to, BranchCalendar calendar, IMasterDataReferences master,
        IUserDirectory users, ITenantContext caller, ICallerPermissions scope, CancellationToken ct)
    {
        if (from is null) return (null, default, RevenueSupport.Invalid("dateFrom", "When the report starts (depot date and time)."));
        if (to is null) return (null, default, RevenueSupport.Invalid("dateTo", "When the report ends (depot date and time)."));
        var end = to.Value.TimeOfDay == TimeSpan.Zero ? to.Value.Date.AddDays(1).AddTicks(-1) : to.Value;
        if (end < from) return (null, default, RevenueSupport.Invalid("dateTo", "The end is before the start."));

        var (context, refused) = await ContextAsync(format, branchId, DateOnly.FromDateTime(from.Value), DateOnly.FromDateTime(end),
            calendar, master, users, caller, scope, ct);
        if (refused is not null) return (null, default, refused);
        var zone = context!.Branch.Zone;
        DateTimeOffset At(DateTime local) => new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local)).ToUniversalTime();
        return (context with { Start = At(from.Value), End = At(end).AddTicks(1) }, end, null);
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

    private static async Task<IResult> ByUserAsync(
        string format, RevenueDbContext db, ITosBookingHeaders tos, BranchCalendar calendar, IMasterDataReferences master,
        IUserDirectory users, ITenantContext caller, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, DateTime? dateFrom = null, DateTime? dateTo = null, string? bookingType = null, Guid? cashierUserId = null)
    {
        var type = string.IsNullOrWhiteSpace(bookingType) ? null : bookingType.Trim().ToUpperInvariant();
        if (type is not null && !BookingTypes.Contains(type))
            return RevenueSupport.Invalid("bookingType", "Use IMPORT, EXPORT, REPO or INTERNAL, or leave it out for all.");
        var (context, to, refused) = await TimedContextAsync(format, branchId, dateFrom, dateTo, calendar, master, users, caller, scope, ct);
        if (refused is not null) return refused;

        return File(await CashReceiptReports.ByUserAsync(db, tos, master, users, context!, dateFrom!.Value, to, type, cashierUserId, ct), format);
    }

    private static async Task<IResult> ByLinerAsync(
        string format, RevenueDbContext db, ITosBookingHeaders tos, BranchCalendar calendar, IMasterDataReferences master,
        IUserDirectory users, ITenantContext caller, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, DateTime? dateFrom = null, DateTime? dateTo = null, string? agentCode = null)
    {
        var (context, to, refused) = await TimedContextAsync(format, branchId, dateFrom, dateTo, calendar, master, users, caller, scope, ct);
        if (refused is not null) return refused;

        var agent = string.IsNullOrWhiteSpace(agentCode) ? null : agentCode.Trim();
        return File(await CashReceiptReports.ByLinerAsync(db, tos, master, context!, dateFrom!.Value, to, agent, ct), format);
    }

    private static async Task<IResult> ByCompanyAsync(
        string format, RevenueDbContext db, ITosBookingHeaders tos, ITosTruckVisits trucks, BranchCalendar calendar,
        IMasterDataReferences master, IUserDirectory users, ITenantContext caller, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, DateOnly? dateFrom = null, DateOnly? dateTo = null)
    {
        var (context, refused) = await ContextAsync(format, branchId, dateFrom, dateTo, calendar, master, users, caller, scope, ct);
        if (refused is not null) return refused;

        return File(await CashReceiptReports.ByCompanyAsync(db, tos, trucks, master, context!, ct), format);
    }

    private static async Task<IResult> CreditInvoiceListingAsync(
        string format, RevenueDbContext db, ITosBookingHeaders tos, BranchCalendar calendar, IMasterDataReferences master,
        IUserDirectory users, ITenantContext caller, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, DateOnly? dateFrom = null, DateOnly? dateTo = null,
        string? agentCode = null, string? customerCode = null, string? bookingType = null)
    {
        var type = string.IsNullOrWhiteSpace(bookingType) ? null : bookingType.Trim().ToUpperInvariant();
        if (type is not null && !BookingTypes.Contains(type))
            return RevenueSupport.Invalid("bookingType", "Use IMPORT, EXPORT, REPO or INTERNAL, or leave it out for all.");
        var (context, refused) = await ContextAsync(format, branchId, dateFrom, dateTo, calendar, master, users, caller, scope, ct);
        if (refused is not null) return refused;

        return File(await AccountingReports.CreditInvoiceListingAsync(db, tos, context!,
            string.IsNullOrWhiteSpace(agentCode) ? null : agentCode.Trim(),
            string.IsNullOrWhiteSpace(customerCode) ? null : customerCode.Trim(), type, ct), format);
    }

    private static async Task<IResult> LiftOffWashingAsync(
        string format, RevenueDbContext db, ITosGateMoves gate, BranchCalendar calendar, IMasterDataReferences master,
        IUserDirectory users, ITenantContext caller, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, DateOnly? dateFrom = null, DateOnly? dateTo = null, string? agentCode = null, string? size = null,
        string? type = null, string? bookingType = null, string? orderType = null, string? movementCode = null)
    {
        var bt = string.IsNullOrWhiteSpace(bookingType) ? null : bookingType.Trim().ToUpperInvariant();
        if (bt is not null && !BookingTypes.Contains(bt))
            return RevenueSupport.Invalid("bookingType", "Use IMPORT, EXPORT, REPO or INTERNAL, or leave it out for all.");
        var (context, refused) = await ContextAsync(format, branchId, dateFrom, dateTo, calendar, master, users, caller, scope, ct);
        if (refused is not null) return refused;

        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().ToUpperInvariant();
        // The desktop never sent a blank movement: blank meant MTY IN. Vector wrote it with a space, Gecko with "_".
        var movement = Clean(movementCode)?.Replace(' ', '_') ?? "MTY_IN";
        var filter = new TosGateMoveFilter(movement, Clean(agentCode), bt, Clean(orderType));
        return File(await OperationChargeReports.LiftOffWashingAsync(db, gate, master, context!, filter, Clean(size), Clean(type), ct), format);
    }

    private static async Task<IResult> StorageActivityAsync(
        string format, RevenueDbContext db, ITosBookedBoxes booked, BranchCalendar calendar, IMasterDataReferences master,
        IUserDirectory users, ITenantContext caller, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, DateOnly? dateFrom = null, DateOnly? dateTo = null, string? agentCode = null, string? vesselCode = null,
        string? voyageNo = null, string? bookingType = null, string? orderType = null, string? bookingBlNo = null)
    {
        var bt = string.IsNullOrWhiteSpace(bookingType) ? null : bookingType.Trim().ToUpperInvariant();
        if (bt is not null && !BookingTypes.Contains(bt))
            return RevenueSupport.Invalid("bookingType", "Use IMPORT, EXPORT, REPO or INTERNAL, or leave it out for all.");
        var (context, refused) = await ContextAsync(format, branchId, dateFrom, dateTo, calendar, master, users, caller, scope, ct);
        if (refused is not null) return refused;

        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().ToUpperInvariant();
        var filter = new TosBookedBoxFilter(Clean(agentCode), Clean(vesselCode), Clean(voyageNo), bt, Clean(orderType), Clean(bookingBlNo));
        return File(await OperationChargeReports.StorageActivityAsync(db, booked, master, context!, filter, ct), format);
    }

    private static async Task<IResult> StorageActivityByVesselAsync(
        string format, RevenueDbContext db, ITosBookingHeaders tos, ITosBookedBoxes booked, BranchCalendar calendar,
        IMasterDataReferences master, IUserDirectory users, ITenantContext caller, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, DateOnly? dateFrom = null, DateOnly? dateTo = null, string? agentCode = null, string? vesselCode = null,
        string? voyageNo = null, string? bookingBlNo = null)
    {
        var (context, refused) = await ContextAsync(format, branchId, dateFrom, dateTo, calendar, master, users, caller, scope, ct);
        if (refused is not null) return refused;

        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        return File(await OperationChargeReports.StorageActivityByVesselAsync(db, tos, booked, master, context!,
            Clean(agentCode), Clean(vesselCode), Clean(voyageNo), Clean(bookingBlNo), ct), format);
    }

    private static async Task<IResult> ExportFullOutAsync(
        string format, RevenueDbContext db, ITosBookedBoxes booked, BranchCalendar calendar,
        IMasterDataReferences master, IUserDirectory users, ITenantContext caller, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, DateOnly? dateFrom = null, DateOnly? dateTo = null, string? agentCode = null, string? vesselCode = null,
        string? voyageNo = null)
    {
        var (context, refused) = await ContextAsync(format, branchId, dateFrom, dateTo, calendar, master, users, caller, scope, ct);
        if (refused is not null) return refused;

        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        return File(await OperationChargeReports.ExportFullOutAsync(db, booked, master, context!,
            Clean(agentCode), Clean(vesselCode), Clean(voyageNo), ct), format);
    }

    private static async Task<IResult> MonitoringDayAsync(
        string format, RevenueDbContext db, ITosBookingHeaders tos, ITosBookedBoxes booked, BranchCalendar calendar,
        IMasterDataReferences master, IUserDirectory users, ITenantContext caller, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, string? agentCode = null, string? bookingType = null, string? vesselCode = null, string? voyageNo = null)
    {
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var type = Clean(bookingType)?.ToUpperInvariant();
        if (type is not null && !BookingTypes.Contains(type))
            return RevenueSupport.Invalid("bookingType", "IMPORT, EXPORT, REPO or INTERNAL.");

        // The RDL has no dates; the shared checks get today's.
        var today = DateOnly.FromDateTime(calendar.Now.UtcDateTime);
        var (context, refused) = await ContextAsync(format, branchId, today, today, calendar, master, users, caller, scope, ct);
        if (refused is not null) return refused;

        return File(await OperationChargeReports.MonitoringDayAsync(db, tos, booked, master, context!,
            Clean(agentCode), type, Clean(vesselCode), Clean(voyageNo), ct), format);
    }
}
