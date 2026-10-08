using Gecko.Data;
using Gecko.Identity.Contracts;
using Gecko.Revenue.Application;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Endpoints.Reports;

/// <summary>
/// Cash receipts over a range of depot days, for /reports/accounts.
///
///   GET /reports/receipts        totals: by day, shift, cashier, paying customer and
///                                payment channel; issued and voided apart.
///   GET /reports/receipts/list   the receipts themselves, paged.
///
/// A receipt is a tax invoice (15_cashier.sql): ISSUED counts, VOIDED is shown
/// beside it and never added in. Days are the depot's. Cashiers are named through
/// Identity (IUserDirectory). Read by revenue.charge.view at the depot — the
/// owner, ops manager and accounts; a cashier sees only their own drawer at the window.
/// </summary>
internal static class ReceiptReportEndpoints
{
    public const int MaxDays = 366;
    private const string Issued = "ISSUED";
    private const string Voided = "VOIDED";

    public static RouteGroupBuilder MapReceiptReportEndpoints(this RouteGroupBuilder revenue)
    {
        var reports = revenue.MapGroup("/reports").WithTags("Revenue — reports");

        reports.MapGet("/receipts", SummaryAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("Cash receipts at a depot over a range of depot days: by day, shift, cashier, customer and channel");
        reports.MapGet("/receipts/list", ListAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("The receipts behind the cash-receipts report, paged");

        return revenue;
    }

    private sealed record Range(BranchClockInfo Branch, DateOnly From, DateOnly To, DateTimeOffset Start, DateTimeOffset End);

    /// <summary>The shared parameter checks; a 400 names the field, a depot outside the caller's is 403.</summary>
    private static async Task<(Range? Range, IResult? Refused)> RangeAsync(
        Guid? branchId, DateOnly? from, DateOnly? to, BranchCalendar calendar, ICallerPermissions scope, CancellationToken ct)
    {
        if (branchId is null) return (null, RevenueSupport.Invalid("branchId", "Which depot? Days are the depot's own."));
        if (from is null) return (null, RevenueSupport.Invalid("from", "The first day of the report."));
        if (to is null) return (null, RevenueSupport.Invalid("to", "The last day of the report."));
        if (to < from) return (null, RevenueSupport.Invalid("to", "The last day is before the first."));
        if (to.Value.DayNumber - from.Value.DayNumber + 1 > MaxDays)
            return (null, RevenueSupport.Invalid("to", $"At most {MaxDays} days in one report."));

        var branch = await calendar.BranchAsync(branchId.Value, ct);
        if (branch is null) return (null, TypedResults.NotFound());
        if (!scope.HasAt(RevenuePermissions.ChargeView, branch.BranchId))
            return (null, TypedResults.Problem(title: "Outside your branches", detail: "That depot is not one you cover.",
                statusCode: StatusCodes.Status403Forbidden));

        return (new Range(branch, from.Value, to.Value, StartOf(branch, from.Value), StartOf(branch, to.Value.AddDays(1))), null);
    }

    private static DateTimeOffset StartOf(BranchClockInfo branch, DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, branch.Zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static async Task<IResult> SummaryAsync(
        Guid? branchId, DateOnly? from, DateOnly? to, RevenueDbContext db, BranchCalendar calendar, IUserDirectory users,
        ICallerPermissions scope, CancellationToken ct)
    {
        var (range, refused) = await RangeAsync(branchId, from, to, calendar, scope, ct);
        if (refused is not null) return refused;
        var r = range!;

        var receipts = await db.Receipts.AsNoTracking()
            .Where(x => x.BranchId == r.Branch.BranchId && x.ReceiptAt >= r.Start && x.ReceiptAt < r.End)
            .Select(x => new
            {
                x.ReceiptId, x.ReceiptAt, x.ShiftId, x.CashierUserId, x.PayerPartyCode, x.PayerName, x.CurrencyCode,
                x.SubtotalAmount, x.TaxAmount, x.TotalAmount, x.Status,
            }).ToListAsync(ct);

        var issued = receipts.Where(x => x.Status == Issued).ToList();
        var issuedIds = issued.Select(x => x.ReceiptId).ToList();
        var payments = issuedIds.Count == 0 ? []
            : await db.ReceiptPayments.AsNoTracking().Where(p => issuedIds.Contains(p.ReceiptId))
                .GroupBy(p => p.Channel)
                .Select(g => new ReceiptChannelResponse(g.Key, g.Count(), g.Sum(p => p.Amount)))
                .ToListAsync(ct);

        var shiftIds = receipts.Select(x => x.ShiftId).Distinct().ToList();
        var shifts = await db.Shifts.AsNoTracking().Where(s => shiftIds.Contains(s.ShiftId)).ToDictionaryAsync(s => s.ShiftId, ct);
        var names = await users.DisplayNamesAsync(receipts.Select(x => x.CashierUserId), ct);

        static ReceiptTally Tally(IEnumerable<ReceiptLite> set)
        {
            var list = set.ToList();
            return new ReceiptTally(list.Count, list.Sum(x => x.Subtotal), list.Sum(x => x.Tax), list.Sum(x => x.Total));
        }

        var lite = issued.Select(x => new ReceiptLite(x.ReceiptAt, x.ShiftId, x.CashierUserId, x.PayerPartyCode, x.PayerName, x.SubtotalAmount, x.TaxAmount, x.TotalAmount)).ToList();
        var voided = receipts.Where(x => x.Status == Voided)
            .Select(x => new ReceiptLite(x.ReceiptAt, x.ShiftId, x.CashierUserId, x.PayerPartyCode, x.PayerName, x.SubtotalAmount, x.TaxAmount, x.TotalAmount)).ToList();

        var days = Enumerable.Range(0, r.To.DayNumber - r.From.DayNumber + 1)
            .Select(i => r.From.AddDays(i))
            .Select(d => new ReceiptDayResponse(d, Tally(lite.Where(x => r.Branch.LocalDate(x.At) == d))))
            .ToList();

        var byShift = lite.GroupBy(x => x.ShiftId)
            .Select(g =>
            {
                var s = shifts.GetValueOrDefault(g.Key);
                return new ReceiptShiftResponse(g.Key, s?.CashierUserId, s is null ? null : names.GetValueOrDefault(s.CashierUserId),
                    s?.OpenedAt, s?.ClosedAt, s?.Status, Tally(g));
            })
            .OrderBy(s => s.OpenedAt).ToList();

        var byCashier = lite.GroupBy(x => x.CashierUserId)
            .Select(g => new ReceiptCashierResponse(g.Key, names.GetValueOrDefault(g.Key), Tally(g)))
            .OrderByDescending(c => c.Tally.Total).ToList();

        // A walk-in customer has no party code; the name on the receipt is who paid.
        var byCustomer = lite.GroupBy(x => x.PayerCode ?? $"\u0000{x.PayerName}")
            .Select(g => new ReceiptCustomerResponse(g.First().PayerCode, g.First().PayerName, Tally(g)))
            .OrderByDescending(c => c.Tally.Total).ThenBy(c => c.PayerName, StringComparer.Ordinal).ToList();

        return TypedResults.Ok(new ReceiptsReportResponse(
            r.Branch.BranchId, r.Branch.BranchCode, r.From, r.To,
            receipts.Select(x => x.CurrencyCode).Distinct().ToList(),
            Tally(lite), Tally(voided), days, byShift, byCashier, byCustomer,
            payments.OrderByDescending(p => p.Amount).ToList()));
    }

    private static async Task<IResult> ListAsync(
        Guid? branchId, DateOnly? from, DateOnly? to, [AsParameters] ListQuery query, RevenueDbContext db, BranchCalendar calendar,
        IUserDirectory users, ICallerPermissions scope, CancellationToken ct, string? status = null, Guid? shiftId = null,
        string? issuedFrom = null)
    {
        var (range, refused) = await RangeAsync(branchId, from, to, calendar, scope, ct);
        if (refused is not null) return refused;
        var r = range!;

        var wanted = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        if (wanted is not null and not (Issued or Voided)) return RevenueSupport.Invalid("status", "Use ISSUED or VOIDED.");

        var rows = db.Receipts.AsNoTracking().Where(x => x.BranchId == r.Branch.BranchId && x.ReceiptAt >= r.Start && x.ReceiptAt < r.End);
        if (wanted is not null) rows = rows.Where(x => x.Status == wanted);
        if (shiftId is not null) rows = rows.Where(x => x.ShiftId == shiftId);
        if (!string.IsNullOrWhiteSpace(issuedFrom))
        {
            var source = issuedFrom.Trim().ToUpperInvariant();
            if (source is not ("GATE" or "WINDOW" or "CASH_BILL")) return RevenueSupport.Invalid("issuedFrom", "Use GATE, WINDOW or CASH_BILL.");
            rows = rows.Where(x => x.IssuedFrom == source);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var q = query.Search.Trim();
            rows = rows.Where(x => x.ReceiptNo.Contains(q) || x.OrderNo!.Contains(q) || x.PayerName.Contains(q)
                                   || x.PayerPartyCode!.Contains(q) || x.PayerTaxId!.Contains(q));
        }

        var page = await rows.OrderBy(x => x.ReceiptAt).ThenBy(x => x.ReceiptNo).ToPagedAsync(query.Page, query.PageSize, ct);
        var ids = page.Items.Select(x => x.ReceiptId).ToList();
        var channels = (await db.ReceiptPayments.AsNoTracking().Where(p => ids.Contains(p.ReceiptId))
                .Select(p => new { p.ReceiptId, p.Channel }).ToListAsync(ct))
            .ToLookup(p => p.ReceiptId, p => p.Channel);
        var names = await users.DisplayNamesAsync(page.Items.Select(x => x.CashierUserId), ct);
        // The split trail, both ways, for the page's rows.
        var fromIds = page.Items.Select(x => x.SplitFromReceiptId).OfType<Guid>().Distinct().ToList();
        var splitFrom = fromIds.Count == 0 ? []
            : await db.Receipts.AsNoTracking().Where(x => fromIds.Contains(x.ReceiptId)).ToDictionaryAsync(x => x.ReceiptId, x => x.ReceiptNo, ct);
        var splitInto = (await db.Receipts.AsNoTracking().Where(x => x.SplitFromReceiptId != null && ids.Contains(x.SplitFromReceiptId.Value))
                .Select(x => new { From = x.SplitFromReceiptId!.Value, x.ReceiptNo }).ToListAsync(ct))
            .ToLookup(x => x.From, x => x.ReceiptNo);

        return TypedResults.Ok(new PagedResult<ReceiptRowResponse>(
            page.Items.Select(x => new ReceiptRowResponse(
                x.ReceiptId, x.ReceiptNo, x.ReceiptAt, x.ShiftId, x.CashierUserId, names.GetValueOrDefault(x.CashierUserId),
                x.OrderNo, x.PayerPartyCode, x.PayerName, x.PayerTaxId, x.PayerBranchNo, x.CurrencyCode,
                x.SubtotalAmount, x.TaxAmount, x.TotalAmount, x.Status, x.VoidedAt, x.VoidReason,
                channels[x.ReceiptId].Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList(),
                x.IssuedFrom, x.SplitFromReceiptId is { } fromId ? splitFrom.GetValueOrDefault(fromId) : null,
                splitInto[x.ReceiptId].Any() ? splitInto[x.ReceiptId].OrderBy(n => n, StringComparer.Ordinal).ToList() : null)).ToList(),
            page.Page, page.PageSize, page.TotalCount));
    }

    private sealed record ReceiptLite(DateTimeOffset At, Guid ShiftId, Guid CashierUserId, string? PayerCode, string PayerName,
        decimal Subtotal, decimal Tax, decimal Total);
}

/// <summary>Receipts counted, and their money before VAT, VAT and total.</summary>
public sealed record ReceiptTally(int Receipts, decimal Subtotal, decimal Tax, decimal Total);

public sealed record ReceiptDayResponse(DateOnly Day, ReceiptTally Tally);

public sealed record ReceiptShiftResponse(
    Guid ShiftId, Guid? CashierUserId, string? CashierName, DateTimeOffset? OpenedAt, DateTimeOffset? ClosedAt, string? Status, ReceiptTally Tally);

public sealed record ReceiptCashierResponse(Guid CashierUserId, string? CashierName, ReceiptTally Tally);

/// <summary><c>PayerCode</c> null = a walk-in customer, known by the name on the receipt.</summary>
public sealed record ReceiptCustomerResponse(string? PayerCode, string PayerName, ReceiptTally Tally);

public sealed record ReceiptChannelResponse(string Channel, int Payments, decimal Amount);

/// <summary>
/// <c>Total</c> is the ISSUED receipts; <c>Voided</c> the voided ones, never added in.
/// <c>Currencies</c> lists what the receipts are in — one, in practice.
/// </summary>
public sealed record ReceiptsReportResponse(
    Guid BranchId, string BranchCode, DateOnly From, DateOnly To, IReadOnlyList<string> Currencies,
    ReceiptTally Total, ReceiptTally Voided,
    IReadOnlyList<ReceiptDayResponse> Days, IReadOnlyList<ReceiptShiftResponse> Shifts,
    IReadOnlyList<ReceiptCashierResponse> Cashiers, IReadOnlyList<ReceiptCustomerResponse> Customers,
    IReadOnlyList<ReceiptChannelResponse> Channels);

public sealed record ReceiptRowResponse(
    Guid ReceiptId, string ReceiptNo, DateTimeOffset ReceiptAt, Guid ShiftId, Guid CashierUserId, string? CashierName,
    string? OrderNo, string? PayerCode, string PayerName, string? PayerTaxId, string? PayerBranchNo, string CurrencyCode,
    decimal Subtotal, decimal Tax, decimal Total, string Status, DateTimeOffset? VoidedAt, string? VoidReason,
    IReadOnlyList<string> Channels,
    // gecko_revenue 29: GATE | WINDOW | CASH_BILL (null before 29), and the split trail both ways.
    string? IssuedFrom = null, string? SplitFromReceiptNo = null, IReadOnlyList<string>? SplitIntoReceiptNos = null);
