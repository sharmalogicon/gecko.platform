using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Application;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Endpoints.Charges;

/// <summary>
/// Reading billing.charge — every priced line, and where it is in its life
/// (14_billing.sql). Two reads, both read-only and additive:
///
///   GET /charges            the register (billing/service-orders): filter by
///                           status, source, payer, charge code, box, order, date.
///   GET /charges/unbilled   the credit lines not yet invoiced (status UNBILLED),
///                           totalled per payer (billing/unbilled).
///   GET /charges/statement  one booking's statement (billing/statement): every
///                           box with its charge lines, and the receipts that paid them.
///
/// A cash depot's lines are PAID / EARNED / WAIVED; UNBILLED comes from credit
/// accrual at the gate (PLAN_BILLING 6.3), so the unbilled read is empty until
/// that runs — it says nothing it cannot back. revenue.charge.view per branch.
/// </summary>
internal static class ChargeEndpoints
{
    public static readonly string[] Statuses = ["QUOTED", "PAID", "EARNED", "UNBILLED", "INVOICED", "WAIVED", "CANCELLED"];
    public static readonly string[] Sources = ["WINDOW", "GATE", "STORAGE", "MANUAL"];

    public static RouteGroupBuilder MapChargeEndpoints(this RouteGroupBuilder revenue)
    {
        var charges = revenue.MapGroup("/charges").WithTags("Revenue — charges");

        charges.MapGet("/", ListAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("The charge register: priced lines with their status, payer, box and price source");
        charges.MapGet("/unbilled", UnbilledAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("Credit lines not yet invoiced (UNBILLED), totalled per payer");
        charges.MapGet("/statement", StatementAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("One booking's statement: each box with its charge lines, and the receipts that paid them");

        return revenue;
    }

    private static async Task<Results<Ok<PagedResult<ChargeResponse>>, ValidationProblem, ProblemHttpResult>> ListAsync(
        [AsParameters] ListQuery query, RevenueDbContext db, IMasterDataReferences master, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, string? status = null, string? source = null, string? payerCode = null, string? chargeCode = null,
        string? containerNo = null, string? orderNo = null, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var rows = Scoped(db, scope, branchId, out var refused);
        if (refused is not null) return refused;

        if (Codes(status, Statuses, "status", out var statuses) is { } badStatus) return badStatus;
        if (Codes(source, Sources, "source", out var sources) is { } badSource) return badSource;
        if (from is not null && to is not null && to < from) return RevenueSupport.Invalid("to", "The end is before the start.");

        if (statuses.Count > 0) rows = rows.Where(c => statuses.Contains(c.Status));
        if (sources.Count > 0) rows = rows.Where(c => sources.Contains(c.Source));
        if (Clean(payerCode) is { } payer) rows = rows.Where(c => c.PayerPartyCode == payer);
        if (Clean(chargeCode) is { } code) rows = rows.Where(c => c.ChargeCode == code);
        if (Clean(containerNo) is { } box) rows = rows.Where(c => c.ContainerNo == box.Replace(" ", "").Replace("-", ""));
        if (Clean(orderNo) is { } order) rows = rows.Where(c => c.OrderNo == order);
        if (from is not null) rows = rows.Where(c => c.CreatedAt >= from);
        if (to is not null) rows = rows.Where(c => c.CreatedAt <= to);
        if (Clean(query.Search) is { } q)
            rows = rows.Where(c => c.ContainerNo!.Contains(q) || c.OrderNo!.Contains(q) || c.EirNo!.Contains(q)
                                   || c.ChargeCode.Contains(q) || c.PayerPartyCode!.Contains(q));

        var page = await rows.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.ChargeId)
            .ToPagedAsync(query.Page, query.PageSize, ct);

        var names = await PayerNamesAsync(master, page.Items.Select(c => c.PayerPartyCode), ct);
        return TypedResults.Ok(new PagedResult<ChargeResponse>(
            page.Items.Select(c => ToResponse(c, names)).ToList(), page.Page, page.PageSize, page.TotalCount));
    }

    private static async Task<Results<Ok<UnbilledResponse>, ValidationProblem, ProblemHttpResult>> UnbilledAsync(
        RevenueDbContext db, IMasterDataReferences master, ICallerPermissions scope, TimeProvider clock, CancellationToken ct,
        Guid? branchId = null)
    {
        var rows = Scoped(db, scope, branchId, out var refused);
        if (refused is not null) return refused;

        var payers = await rows.Where(c => c.Status == ChargeStatus.Unbilled)
            .GroupBy(c => new { c.PayerPartyCode, c.BillTo, c.CurrencyCode })
            .Select(g => new
            {
                g.Key.PayerPartyCode, g.Key.BillTo, g.Key.CurrencyCode,
                Lines = g.Count(),
                Boxes = g.Where(c => c.ContainerNo != null).Select(c => c.ContainerNo).Distinct().Count(),
                Amount = g.Sum(c => c.Amount),
                Tax = g.Sum(c => c.TaxAmount),
                Oldest = g.Min(c => c.CreatedAt),
                Newest = g.Max(c => c.CreatedAt),
            }).ToListAsync(ct);

        var names = await PayerNamesAsync(master, payers.Select(p => p.PayerPartyCode), ct);
        var list = payers
            .Select(p => new UnbilledPayerResponse(
                p.PayerPartyCode, p.PayerPartyCode is { } code ? names.GetValueOrDefault(code) : null, p.BillTo, p.CurrencyCode,
                p.Lines, p.Boxes, p.Amount, p.Tax, p.Amount + p.Tax, p.Oldest, p.Newest))
            .OrderByDescending(p => p.Total).ThenBy(p => p.PayerCode, StringComparer.Ordinal)
            .ToList();

        return TypedResults.Ok(new UnbilledResponse(
            clock.GetUtcNow(), branchId, list.Sum(p => p.Lines), list.Sum(p => p.Amount), list.Sum(p => p.Tax), list.Sum(p => p.Total),
            list));
    }

    /// <summary>
    /// INVOICING_PROPOSAL part D, replacing Vector's BookingStatement: read-only. A
    /// discount is a tariff or a waiver, never an edit to a line, so nothing here
    /// changes a price. Boxes come from Revenue's copy of the booking, including boxes
    /// that have left it; a line whose box is unknown is listed under no box.
    /// </summary>
    private static async Task<Results<Ok<BookingStatementResponse>, NotFound<ProblemDetails>, ValidationProblem, ProblemHttpResult>> StatementAsync(
        string? orderNo, RevenueDbContext db, IMasterDataReferences master, ICallerPermissions scope, CancellationToken ct)
    {
        var order = orderNo?.Trim();
        if (string.IsNullOrEmpty(order)) return RevenueSupport.Invalid("orderNo", "Which booking? Its order number.");

        var plan = await db.BookingPlans.AsNoTracking().SingleOrDefaultAsync(p => p.OrderNo == order, ct);
        if (plan is null)
            return TypedResults.NotFound(new ProblemDetails
            {
                Title = $"'{order}' is not a booking Revenue knows.",
                Detail = "Revenue learns bookings from TOS. A booking made a moment ago may still be on its way.",
            });
        if (!scope.HasAt(RevenuePermissions.ChargeView, plan.BranchId))
            return TypedResults.Problem(title: "Outside your branches", detail: "That booking is at a depot you do not cover.",
                statusCode: StatusCodes.Status403Forbidden);

        var boxes = await db.BookingPlanContainers.AsNoTracking().Where(b => b.BookingId == plan.BookingId)
            .OrderBy(b => b.ContainerNo).ToListAsync(ct);
        // gecko_revenue 23: the expected (QUOTED) lines of an open booking, next to what was paid / billed.
        // A quote that no longer applies (CANCELLED) is history, not a line.
        var open = plan.Status == "OPEN";
        var lines = await db.Charges.AsNoTracking()
            .Where(c => c.BookingId == plan.BookingId
                        && !(c.Source == ChargeSource.Quote && (c.Status == ChargeStatus.Cancelled || !open)))
            .OrderBy(c => c.MovementCode).ThenBy(c => c.CreatedAt).ThenBy(c => c.ChargeCode).ToListAsync(ct);
        // Paid or billed before the quote was refreshed: the real line stands, the quote steps aside.
        var settledKeys = lines.Where(c => c.Source != ChargeSource.Quote && c.Status != ChargeStatus.Cancelled)
            .Select(c => (c.BookingContainerId, c.MovementCode, c.ChargeCode, c.BillTo)).ToHashSet();
        lines = lines.Where(c => c.Source != ChargeSource.Quote
                                 || !settledKeys.Contains((c.BookingContainerId, c.MovementCode, c.ChargeCode, c.BillTo))).ToList();
        var receipts = await db.Receipts.AsNoTracking().Where(r => r.BookingId == plan.BookingId)
            .OrderBy(r => r.ReceiptAt).ToListAsync(ct);
        var receiptNo = receipts.ToDictionary(r => r.ReceiptId, r => r.ReceiptNo);

        var names = await PayerNamesAsync(master, lines.Select(c => c.PayerPartyCode).Append(plan.CustomerPartyCode), ct);

        StatementTotals Totals(IEnumerable<Charge> set)
        {
            var list = set.ToList();
            decimal Sum(params string[] statuses) => list.Where(c => statuses.Contains(c.Status)).Sum(c => c.Amount + c.TaxAmount);
            var quoted = list.Where(c => c.Status == ChargeStatus.Quoted).ToList();
            return new StatementTotals(Sum("PAID", "EARNED"), Sum("WAIVED"), Sum("UNBILLED"), Sum("INVOICED"), Sum("CANCELLED"),
                quoted.Where(c => c.PaymentTermCode == "CASH").Sum(c => c.Amount + c.TaxAmount),
                quoted.Where(c => c.PaymentTermCode != "CASH").Sum(c => c.Amount + c.TaxAmount),
                quoted.Count(c => c.ScheduleId is null));
        }

        StatementLineResponse Line(Charge c) => new(ToResponse(c, names),
            c.ReceiptId is { } rid ? receiptNo.GetValueOrDefault(rid) : null);

        var known = boxes.Select(b => b.BookingContainerId).ToHashSet();
        var boxRows = boxes.Select(b =>
        {
            var mine = lines.Where(c => c.BookingContainerId == b.BookingContainerId).ToList();
            return new StatementBoxResponse(b.BookingContainerId, b.ContainerNo, b.EquipmentTypeCode, b.IsCurrent, b.EndReason,
                mine.Select(Line).ToList(), Totals(mine));
        }).ToList();
        var loose = lines.Where(c => c.BookingContainerId is not { } id || !known.Contains(id)).ToList();
        if (loose.Count > 0)
            boxRows.Add(new StatementBoxResponse(null, null, null, false, null, loose.Select(Line).ToList(), Totals(loose)));

        var replacedBy = receipts.Where(r => r.ReplacesReceiptId is not null)
            .ToDictionary(r => r.ReplacesReceiptId!.Value, r => r.ReceiptNo);

        return TypedResults.Ok(new BookingStatementResponse(
            plan.BookingId, plan.OrderNo, plan.BranchId, plan.Status, plan.OrderTypeCode,
            plan.CustomerPartyCode, plan.CustomerPartyCode is { } cust ? names.GetValueOrDefault(cust) : null,
            boxRows,
            receipts.Select(r => new StatementReceiptResponse(
                r.ReceiptId, r.ReceiptNo, r.ReceiptAt, r.Status, r.PayerName, r.CurrencyCode, r.SubtotalAmount, r.TaxAmount, r.TotalAmount,
                r.VoidedAt, r.VoidReason,
                r.ReplacesReceiptId is { } rep ? receiptNo.GetValueOrDefault(rep) : null,
                replacedBy.GetValueOrDefault(r.ReceiptId))).ToList(),
            Totals(lines)));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>The caller's branches only; a branch asked for outside them is a 403, not an empty page.</summary>
    private static IQueryable<Charge> Scoped(RevenueDbContext db, ICallerPermissions scope, Guid? branchId, out ProblemHttpResult? refused)
    {
        refused = null;
        var rows = db.Charges.AsNoTracking();
        if (branchId is { } asked)
        {
            if (!scope.HasAt(RevenuePermissions.ChargeView, asked))
            {
                refused = TypedResults.Problem(title: "Outside your branches", detail: "That depot is not one you cover.",
                    statusCode: StatusCodes.Status403Forbidden);
                return rows;
            }
            rows = rows.Where(c => c.BranchId == asked);
        }
        if (scope.BranchesFor(RevenuePermissions.ChargeView) is { } mine)
        {
            var allowed = mine.ToList();
            rows = rows.Where(c => allowed.Contains(c.BranchId));
        }
        return rows;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    /// <summary>A comma-separated list of known codes; anything else is a 400 on the field.</summary>
    private static ValidationProblem? Codes(string? raw, string[] known, string field, out List<string> codes)
    {
        codes = (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToUpperInvariant()).Distinct().ToList();
        var unknown = codes.Where(c => !known.Contains(c)).ToList();
        return unknown.Count == 0 ? null
            : RevenueSupport.Invalid(field, $"Unknown {field} '{string.Join(", ", unknown)}'. Use {string.Join(", ", known)}.");
    }

    private static async Task<IReadOnlyDictionary<string, string>> PayerNamesAsync(
        IMasterDataReferences master, IEnumerable<string?> codes, CancellationToken ct)
    {
        var distinct = codes.OfType<string>().Distinct().ToList();
        if (distinct.Count == 0) return new Dictionary<string, string>();
        var parties = await master.PartiesAsync(distinct, ct);
        return parties.ToDictionary(p => p.Key, p => p.Value.Name);
    }

    private static ChargeResponse ToResponse(Charge c, IReadOnlyDictionary<string, string> names) => new(
        c.ChargeId, c.BranchId, c.Source, c.Status,
        c.BookingId, c.OrderNo, c.ContainerNo, c.MovementCode, c.GateTransactionId, c.EirNo,
        c.BillingPeriod, c.ServiceFrom, c.ServiceTo,
        c.ChargeCode, c.ChargeName, c.BillTo, c.PaymentTermCode,
        c.PayerPartyCode, c.PayerPartyCode is { } code ? names.GetValueOrDefault(code) : null,
        c.Quantity, c.UnitRate, c.Amount, c.TaxAmount, c.Amount + c.TaxAmount, c.CurrencyCode,
        c.PricedForDate, c.ScheduleNo, c.ScheduleVersionNo,
        c.ReceiptId, c.CouponRef, c.InvoiceId, c.EarnedAt, c.WaivedAt, c.WaiveReason, c.CancelledAt, c.CancelReason,
        c.CreditNoteRequired, c.CreatedAt);
}

/// <summary>One priced line. <c>PayerName</c> is MDM's; null for a walk-in cash customer or a code MDM no longer knows.</summary>
public sealed record ChargeResponse(
    Guid ChargeId, Guid BranchId, string Source, string Status,
    Guid? BookingId, string? OrderNo, string? ContainerNo, string? MovementCode, Guid? GateTransactionId, string? EirNo,
    string? BillingPeriod, DateOnly? ServiceFrom, DateOnly? ServiceTo,
    string ChargeCode, string? ChargeName, string BillTo, string PaymentTermCode,
    string? PayerCode, string? PayerName,
    decimal Quantity, decimal? UnitRate, decimal Amount, decimal TaxAmount, decimal Total, string CurrencyCode,
    DateOnly? PricedForDate, string? ScheduleNo, short? ScheduleVersionNo,
    Guid? ReceiptId, string? CouponRef, Guid? InvoiceId, DateTimeOffset? EarnedAt,
    DateTimeOffset? WaivedAt, string? WaiveReason, DateTimeOffset? CancelledAt, string? CancelReason,
    bool CreditNoteRequired, DateTimeOffset CreatedAt);

public sealed record UnbilledPayerResponse(
    string? PayerCode, string? PayerName, string BillTo, string CurrencyCode,
    int Lines, int Boxes, decimal Amount, decimal Tax, decimal Total, DateTimeOffset Oldest, DateTimeOffset Newest);

/// <summary>Money on a booking by where it is: paid (incl. earned), waived, unbilled, invoiced, cancelled — each with VAT.</summary>
/// <param name="ExpectedCash">QUOTED cash still to be paid at the window (gecko_revenue 23).</param>
/// <param name="ExpectedCredit">QUOTED credit still to be billed.</param>
/// <param name="NoPrice">QUOTED lines no tariff prices (amount 0, no schedule): a rate is missing.</param>
public sealed record StatementTotals(decimal Paid, decimal Waived, decimal Unbilled, decimal Invoiced, decimal Cancelled,
    decimal ExpectedCash = 0, decimal ExpectedCredit = 0, int NoPrice = 0);

/// <summary>A charge line, and the number of the receipt that paid it (a voided one included).</summary>
public sealed record StatementLineResponse(ChargeResponse Charge, string? ReceiptNo);

/// <summary><c>BookingContainerId</c> null = lines whose box Revenue does not know on this booking.</summary>
public sealed record StatementBoxResponse(
    Guid? BookingContainerId, string? ContainerNo, string? EquipmentTypeCode, bool IsCurrent, string? EndReason,
    IReadOnlyList<StatementLineResponse> Lines, StatementTotals Totals);

public sealed record StatementReceiptResponse(
    Guid ReceiptId, string ReceiptNo, DateTimeOffset ReceiptAt, string Status, string PayerName, string CurrencyCode,
    decimal Subtotal, decimal Tax, decimal Total, DateTimeOffset? VoidedAt, string? VoidReason,
    string? ReplacesReceiptNo, string? ReplacedByReceiptNo);

public sealed record BookingStatementResponse(
    Guid BookingId, string OrderNo, Guid BranchId, string BookingStatus, string OrderTypeCode,
    string? CustomerCode, string? CustomerName,
    IReadOnlyList<StatementBoxResponse> Boxes, IReadOnlyList<StatementReceiptResponse> Receipts, StatementTotals Totals);

/// <summary>Totals add across currencies only when there is one; the page shows them per payer.</summary>
public sealed record UnbilledResponse(
    DateTimeOffset AsAt, Guid? BranchId, int Lines, decimal Amount, decimal Tax, decimal Total,
    IReadOnlyList<UnbilledPayerResponse> Payers);
