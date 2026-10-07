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
/// The credit invoice (gecko_revenue 27, owner 2026-10-07 — Vector's "send to invoice"):
///   POST /invoices/send        the ticked CREDIT lines of one payer become one tax invoice,
///                              ISSUED at once: numbered (INVOICE series, per branch and month,
///                              gap-free) and final. Lines that are UNBILLED (the box moved),
///                              still QUOTED (billed in advance) or hand-added may go; each once.
///   GET  /invoices             the issued invoices, newest first
///   GET  /invoices/{id}        one invoice with its lines
/// Cash is never invoiced here: the cash window's receipt is the tax invoice. A wrong invoice
/// is corrected by a credit note (a later round), never edited.
/// </summary>
internal static class InvoiceEndpoints
{
    private const string Credit = "CREDIT";

    public static RouteGroupBuilder MapInvoiceEndpoints(this RouteGroupBuilder revenue)
    {
        var invoices = revenue.MapGroup("/invoices").WithTags("Revenue — invoices");
        invoices.MapPost("/send", SendAsync).RequireBranchPermission(RevenuePermissions.InvoiceIssue)
            .WithSummary("Send ticked CREDIT lines of one payer to a new tax invoice, issued at once");
        invoices.MapGet("/", ListAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("Issued invoices, newest first");
        invoices.MapGet("/{invoiceId:guid}", GetAsync).RequireBranchPermission(RevenuePermissions.ChargeView)
            .WithSummary("One invoice with its lines");
        return revenue;
    }

    private static async Task<Results<Ok<SendInvoiceResponse>, NotFound<ProblemDetails>, ValidationProblem, ProblemHttpResult>> SendAsync(
        SendInvoiceRequest request, RevenueDbContext db, IMasterDataReferences master, BranchCalendar calendar,
        ICallerPermissions scope, ITenantContext caller, CancellationToken ct)
    {
        var term = request.PaymentTermCode?.Trim().ToUpperInvariant();
        if (term == "CASH")
            return RevenueSupport.Invalid("paymentTermCode", "Cash is collected at the cash window; its receipt is the tax invoice.");
        if (term != Credit) return RevenueSupport.Invalid("paymentTermCode", "CREDIT.");
        if (!string.IsNullOrWhiteSpace(request.InvoiceNo))
            return RevenueSupport.Conflict($"{request.InvoiceNo.Trim()} is issued and final.",
                "An invoice is final when it is sent. Send these lines to a new invoice (omit invoiceNo); a wrong one is corrected by a credit note.");
        var ids = (request.ChargeIds ?? []).Distinct().ToList();
        if (ids.Count == 0) return RevenueSupport.Invalid("chargeIds", "Which lines? At least one.");
        if (ids.Count > 500) return RevenueSupport.Invalid("chargeIds", "At most 500 lines on one invoice.");
        var remarks = request.Remarks?.Trim();
        if (remarks is { Length: > 500 }) return RevenueSupport.Invalid("remarks", "At most 500 characters.");

        var ticked = await db.Charges.Where(c => ids.Contains(c.ChargeId)).ToListAsync(ct);
        if (ticked.Count != ids.Count || ticked.Any(c => !scope.HasAt(RevenuePermissions.InvoiceIssue, c.BranchId)))
            return TypedResults.NotFound(new ProblemDetails { Title = "Some of these lines are not charges in your branches.", Detail = "Nothing was invoiced." });

        // Only the ticked lines carrying the chosen term are sent (cash and credit never share an invoice).
        var lines = ticked.Where(c => c.PaymentTermCode == term).ToList();
        if (lines.Count == 0) return RevenueSupport.Conflict("None of these lines is on CREDIT: nothing to invoice.");
        if (lines.Where(c => c.Status is not (ChargeStatus.Unbilled or ChargeStatus.Quoted)).ToList() is { Count: > 0 } done)
            return RevenueSupport.Conflict($"{done.Count} of these lines cannot be invoiced: nothing was invoiced.",
                string.Join("; ", done.Select(c => $"{c.ChargeCode} {c.ContainerNo} {c.MovementCode}: {c.Status}")));
        if (lines.Select(c => (c.BranchId, c.BillTo, c.PayerPartyCode, c.CurrencyCode)).Distinct().Count() > 1)
            return RevenueSupport.Conflict("One invoice, one payer: these lines are billed to more than one payer, branch or currency.",
                "Send each payer's lines on their own invoice.");

        var first = lines[0];
        var branch = await calendar.BranchAsync(first.BranchId, ct);
        if (branch is null) return RevenueSupport.Conflict("This branch has no master-data profile.", "Invoices are numbered per branch code.");
        var payerName = first.PayerPartyCode is { } code ? (await master.PartiesAsync([code], ct)).GetValueOrDefault(code)?.Name : null;
        var now = calendar.Now;
        var by = caller.UserId();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var invoice = new Invoice
        {
            InvoiceId = Guid.CreateVersion7(), TenantId = first.TenantId, BranchId = first.BranchId,
            InvoiceNo = await RevenueNumberSeries.NextAsync(db, RevenueNumberSeries.Invoice, branch.BranchId, branch.BranchCode, branch.Local(now), ct),
            InvoiceType = Credit, Status = "ISSUED", PaymentTermCode = term, BillTo = first.BillTo,
            PayerPartyCode = first.PayerPartyCode, PayerName = payerName, CurrencyCode = first.CurrencyCode,
            SubtotalAmount = lines.Sum(c => c.Amount), TaxAmount = lines.Sum(c => c.TaxAmount),
            Remarks = string.IsNullOrEmpty(remarks) ? null : remarks, IssuedAt = now, IssuedBy = by,
            CreatedAt = now, CreatedBy = by, UpdatedAt = now, UpdatedBy = by,
        };
        invoice.TotalAmount = invoice.SubtotalAmount + invoice.TaxAmount;
        db.Invoices.Add(invoice);

        short lineNo = 0;
        foreach (var c in lines.OrderBy(c => c.OrderNo).ThenBy(c => c.ContainerNo).ThenBy(c => c.MovementCode).ThenBy(c => c.ChargeCode))
        {
            var line = new InvoiceLine
            {
                InvoiceLineId = Guid.CreateVersion7(), TenantId = c.TenantId, InvoiceId = invoice.InvoiceId, LineNo = ++lineNo,
                ChargeId = c.ChargeId, BookingId = c.BookingId, OrderNo = c.OrderNo, ContainerNo = c.ContainerNo, MovementCode = c.MovementCode,
                ChargeCode = c.ChargeCode, ChargeName = c.ChargeName, Quantity = c.Quantity, UnitRate = c.UnitRate, Amount = c.Amount,
                TaxCode = c.TaxCode, TaxRate = c.TaxRate, TaxAmount = c.TaxAmount, CreatedAt = now, CreatedBy = by,
            };
            db.InvoiceLines.Add(line);
            c.Status = ChargeStatus.Invoiced;
            c.InvoiceId = invoice.InvoiceId;
            c.InvoiceLineId = line.InvoiceLineId;
            c.UpdatedAt = now;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RevenueSupport.Conflict("A line changed since you loaded it.", "Re-read the statement and send again. Nothing was invoiced.");
        }
        catch (DbUpdateException)
        {
            return RevenueSupport.Conflict("A line was invoiced a moment ago.", "Re-read the statement and send again. Nothing was invoiced.");
        }
        await transaction.CommitAsync(ct);

        return TypedResults.Ok(new SendInvoiceResponse(invoice.InvoiceId, invoice.InvoiceNo, lines.Count,
            invoice.SubtotalAmount, invoice.TaxAmount, invoice.TotalAmount, invoice.CurrencyCode));
    }

    private static async Task<Results<Ok<PagedResult<InvoiceSummaryResponse>>, ProblemHttpResult>> ListAsync(
        [AsParameters] ListQuery query, RevenueDbContext db, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, string? payerCode = null, string? orderNo = null)
    {
        var rows = db.Invoices.AsNoTracking();
        if (branchId is { } asked)
        {
            if (!scope.HasAt(RevenuePermissions.ChargeView, asked))
                return TypedResults.Problem(title: "Outside your branches", statusCode: StatusCodes.Status403Forbidden);
            rows = rows.Where(i => i.BranchId == asked);
        }
        if (scope.BranchesFor(RevenuePermissions.ChargeView) is { } mine)
        {
            var allowed = mine.ToList();
            rows = rows.Where(i => allowed.Contains(i.BranchId));
        }
        if (!string.IsNullOrWhiteSpace(payerCode)) rows = rows.Where(i => i.PayerPartyCode == payerCode.Trim());
        if (!string.IsNullOrWhiteSpace(orderNo))
            rows = rows.Where(i => db.InvoiceLines.Any(l => l.InvoiceId == i.InvoiceId && l.OrderNo == orderNo.Trim()));
        if (!string.IsNullOrWhiteSpace(query.Search))
            rows = rows.Where(i => i.InvoiceNo.Contains(query.Search) || i.PayerPartyCode!.Contains(query.Search) || i.PayerName!.Contains(query.Search));

        var page = await rows.OrderByDescending(i => i.IssuedAt).ThenByDescending(i => i.InvoiceNo)
            .Select(i => new InvoiceSummaryResponse(i.InvoiceId, i.InvoiceNo, i.BranchId, i.InvoiceType, i.Status, i.PaymentTermCode,
                i.BillTo, i.PayerPartyCode, i.PayerName, i.CurrencyCode, i.SubtotalAmount, i.TaxAmount, i.TotalAmount,
                db.InvoiceLines.Count(l => l.InvoiceId == i.InvoiceId), i.IssuedAt, i.Remarks))
            .ToPagedAsync(query.Page, query.PageSize, ct);
        return TypedResults.Ok(page);
    }

    private static async Task<Results<Ok<InvoiceDetailResponse>, NotFound<ProblemDetails>>> GetAsync(
        Guid invoiceId, RevenueDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var i = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.InvoiceId == invoiceId, ct);
        if (i is null || !scope.HasAt(RevenuePermissions.ChargeView, i.BranchId))
            return TypedResults.NotFound(new ProblemDetails { Title = "No such invoice in your branches." });
        var lines = await db.InvoiceLines.AsNoTracking().Where(l => l.InvoiceId == invoiceId).OrderBy(l => l.LineNo)
            .Select(l => new InvoiceLineResponse(l.LineNo, l.ChargeId, l.OrderNo, l.ContainerNo, l.MovementCode, l.ChargeCode, l.ChargeName,
                l.Quantity, l.UnitRate, l.Amount, l.TaxCode, l.TaxRate, l.TaxAmount, l.Amount + l.TaxAmount))
            .ToListAsync(ct);
        return TypedResults.Ok(new InvoiceDetailResponse(
            new InvoiceSummaryResponse(i.InvoiceId, i.InvoiceNo, i.BranchId, i.InvoiceType, i.Status, i.PaymentTermCode, i.BillTo,
                i.PayerPartyCode, i.PayerName, i.CurrencyCode, i.SubtotalAmount, i.TaxAmount, i.TotalAmount, lines.Count, i.IssuedAt, i.Remarks),
            lines));
    }
}

/// <summary><c>InvoiceNo</c>: an issued invoice is final, so only a NEW invoice can be sent to — omit it.</summary>
public sealed record SendInvoiceRequest(IReadOnlyList<Guid>? ChargeIds, string? PaymentTermCode, string? InvoiceNo, string? Remarks);

public sealed record SendInvoiceResponse(Guid InvoiceId, string InvoiceNo, int Lines, decimal Amount, decimal Tax, decimal Total, string CurrencyCode);

public sealed record InvoiceSummaryResponse(
    Guid InvoiceId, string InvoiceNo, Guid BranchId, string InvoiceType, string Status, string PaymentTermCode,
    string BillTo, string? PayerCode, string? PayerName, string CurrencyCode, decimal Amount, decimal Tax, decimal Total,
    int Lines, DateTimeOffset IssuedAt, string? Remarks);

public sealed record InvoiceLineResponse(
    short LineNo, Guid ChargeId, string? OrderNo, string? ContainerNo, string? MovementCode, string ChargeCode, string? ChargeName,
    decimal Quantity, decimal? UnitRate, decimal Amount, string? TaxCode, decimal TaxRate, decimal TaxAmount, decimal Total);

public sealed record InvoiceDetailResponse(InvoiceSummaryResponse Invoice, IReadOnlyList<InvoiceLineResponse> Lines);
