using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Application;
using Gecko.Revenue.Endpoints.Window;
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
/// Customer Cash Bill (owner 2026-10-08; desktop TMS\Billing\CustomerCashBill.cs fed by the Cost Sheet's
/// "Send To → Cash Invoice"): the CASH lines a booking statement still owes — not collected at the gate,
/// e.g. storage after the move, a hand-added line — billed on a cash receipt for one customer, across
/// any of their bookings. Same receipt, number series (CA + branch + YYMM + 5) and PDF as the window.
///   GET  /cash-bills/lines                   the open CASH lines (QUOTED, priced &gt; 0) to choose from
///   POST /cash-bills                         bill the chosen lines; the receipt is final (void + re-issue to change it)
///   GET  /cash-bills/receipts/by-no/{no}     a receipt by its number (search / reprint)
/// A billed expected (QUOTE) line is closed and paid as a WINDOW line (the statement's quote can never be
/// PAID itself); a MANUAL line is paid in place. Voiding the receipt puts both back (WindowEndpoints void).
/// </summary>
internal static class CashBillEndpoints
{
    private static readonly string[] Channels = ["CASH", "TRANSFER", "CHEQUE", "CARD"];
    private static readonly decimal[] WithholdingRates = [0m, 1m, 3m];

    public static RouteGroupBuilder MapCashBillEndpoints(this RouteGroupBuilder revenue)
    {
        var bills = revenue.MapGroup("/cash-bills").WithTags("Revenue — cash bill");
        bills.MapGet("/lines", LinesAsync).RequireBranchPermission(RevenuePermissions.CashCollect)
            .WithSummary("Open CASH lines on booking statements (QUOTED, priced) for a customer and/or booking — what a cash bill can take");
        bills.MapPost("/", CreateAsync).RequireBranchPermission(RevenuePermissions.CashCollect)
            .WithSummary("Bill chosen statement lines on one cash receipt (Customer Cash Bill): payer, remarks, W/H tax 0/1/3%, payments");
        bills.MapGet("/receipts/by-no/{receiptNo}", ByNumberAsync).RequireBranchPermission(RevenuePermissions.CashCollect)
            .WithSummary("A receipt by its number");
        return revenue;
    }

    // ── the lines to choose from ───────────────────────────────────────────────

    private static async Task<Results<Ok<IReadOnlyList<CashBillLineResponse>>, ValidationProblem, ForbidHttpResult>> LinesAsync(
        Guid? branchId, string? customerCode, string? orderNo, RevenueDbContext db, IMasterDataReferences master,
        ICallerPermissions scope, CancellationToken ct)
    {
        if (branchId is not { } branch) return RevenueSupport.Invalid("branchId", "Which depot?");
        if (!scope.HasAt(RevenuePermissions.CashCollect, branch)) return TypedResults.Forbid();
        var customer = customerCode?.Trim().ToUpperInvariant();
        var order = orderNo?.Trim();
        if (string.IsNullOrEmpty(customer) && string.IsNullOrEmpty(order))
            return RevenueSupport.Invalid("customerCode", "Name the customer or a booking.");

        var plans = db.BookingPlans.AsNoTracking().Where(p => p.BranchId == branch);
        if (!string.IsNullOrEmpty(customer)) plans = plans.Where(p => p.CustomerPartyCode == customer);
        if (!string.IsNullOrEmpty(order)) plans = plans.Where(p => p.OrderNo == order);
        var lines = await (
            from c in Open(db).Where(c => c.BranchId == branch)
            join p in plans on c.BookingId equals p.BookingId
            orderby p.OrderNo, c.ContainerNo, c.MovementCode, c.ChargeCode
            select new { c, p.CustomerPartyCode }).Take(2000).ToListAsync(ct);

        var boxIds = lines.Select(l => l.c.BookingContainerId).OfType<Guid>().Distinct().ToList();
        var types = await db.BookingPlanContainers.AsNoTracking().Where(b => boxIds.Contains(b.BookingContainerId))
            .ToDictionaryAsync(b => b.BookingContainerId, b => b.EquipmentTypeCode, ct);
        return TypedResults.Ok<IReadOnlyList<CashBillLineResponse>>(lines.Select(l => new CashBillLineResponse(
            l.c.ChargeId, l.c.BookingId!.Value, l.c.OrderNo ?? "", l.CustomerPartyCode, l.c.BookingContainerId, l.c.ContainerNo,
            l.c.BookingContainerId is { } b ? types.GetValueOrDefault(b) : null, l.c.MovementCode, l.c.ChargeCode, l.c.ChargeName,
            l.c.PaymentTermCode, l.c.BillTo, l.c.Quantity, l.c.UnitRate ?? 0m, l.c.Amount, l.c.TaxRate, l.c.TaxAmount,
            l.c.Amount + l.c.TaxAmount, l.c.CurrencyCode, l.c.Source)).ToList());
    }

    /// <summary>A statement's open CASH line: QUOTED, priced above 0, on a booking.</summary>
    private static IQueryable<Charge> Open(RevenueDbContext db) =>
        db.Charges.Where(c => c.Status == ChargeStatus.Quoted && c.PaymentTermCode == "CASH" && c.BookingId != null
                              && c.UnitRate != null && c.Amount > 0);

    // ── bill them ─────────────────────────────────────────────────────────────

    private static async Task<Results<Created<ReceiptResponse>, ValidationProblem, ForbidHttpResult, ProblemHttpResult>> CreateAsync(
        CreateCashBillRequest request, RevenueDbContext db, BranchCalendar calendar, IMasterDataReferences master, ReceiptDocument document,
        WindowService window,
        ITenantContext caller, ICallerPermissions scope, HttpContext http, CancellationToken ct)
    {
        var (key, badKey) = Idempotency.KeyOf(http.Request);
        if (badKey is not null) return RevenueSupport.Invalid(Idempotency.Header, badKey);
        var hash = key is null ? null : Idempotency.HashOf(request);
        if (key is not null)
        {
            var issued = await db.Receipts.AsNoTracking().Where(r => r.IdempotencyKey == key)
                .Select(r => new { r.ReceiptId, r.IdempotencyHash }).SingleOrDefaultAsync(ct);
            if (issued is not null)
            {
                if (!Idempotency.SameRequest(issued.IdempotencyHash, hash!)) return Idempotency.DifferentRequest(key);
                return TypedResults.Created($"/api/revenue/window/receipts/{issued.ReceiptId}", (await document.ReadAsync(issued.ReceiptId, ct))!);
            }
        }

        var errors = new Dictionary<string, List<string>>();
        if (request.BranchId is not { } branchId) return RevenueSupport.Invalid("branchId", "Which depot?");
        if (!scope.HasAt(RevenuePermissions.CashCollect, branchId)) return TypedResults.Forbid();
        var customer = request.CustomerCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(customer)) errors.Add("customerCode", "Which customer is the bill for?");
        var ids = (request.ChargeIds ?? []).Distinct().ToList();
        if (ids.Count == 0) errors.Add("chargeIds", "Choose at least one line.");
        var rate = request.WithholdingTaxRate ?? 0m;
        if (!WithholdingRates.Contains(rate)) errors.Add("withholdingTaxRate", "NONE (0), 1 or 3.");
        var remarks = request.Remarks?.Trim();
        if (remarks?.Length > 500) errors.Add("remarks", "At most 500 characters.");
        if (request.Payments is not { Count: > 0 }) errors.Add("payments", "How was it paid?");
        foreach (var p in request.Payments ?? [])
        {
            var channel = p.Channel?.Trim().ToUpperInvariant() ?? "";
            if (!Channels.Contains(channel)) errors.Add("payments", $"'{p.Channel}' is not a channel ({string.Join(", ", Channels)}).");
            if (p.Amount <= 0) errors.Add("payments", "Every payment must be more than zero.");
            if (channel == "CASH" && p.TenderedAmount is { } tendered && tendered < p.Amount) errors.Add("payments", "Cash handed over is less than the amount.");
            if (channel != "CASH" && string.IsNullOrWhiteSpace(p.ReferenceNo)) errors.Add("payments", $"A {channel} payment needs its reference (slip, cheque or approval number).");
        }
        if (errors.Count > 0) return RevenueSupport.Invalid(errors);

        var party = (await master.PartiesAsync([customer!], ct)).GetValueOrDefault(customer!);
        if (party is null) return RevenueSupport.Invalid("customerCode", $"'{customer}' is not a party of this tenant.");
        var branch = await calendar.BranchAsync(branchId, ct);
        if (branch is null) return RevenueSupport.Conflict("This depot has no master-data profile.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var charges = await Open(db).Where(c => ids.Contains(c.ChargeId)).ToListAsync(ct);
        if (charges.Count != ids.Count)
            return RevenueSupport.Conflict("Some of these lines are no longer open (paid, waived, re-priced or cancelled a moment ago).",
                "Refresh the lines and choose again.");
        if (charges.Any(c => c.BranchId != branchId)) return RevenueSupport.Invalid("chargeIds", "A line of another depot.");

        var subtotal = charges.Sum(c => c.Amount);
        var tax = charges.Sum(c => c.TaxAmount);
        var total = subtotal + tax;
        if (request.ExpectedTotal is { } expected && expected != total)
            return RevenueSupport.Conflict("The amount changed.", $"The screen shows ฿{expected:N2}; the lines now come to ฿{total:N2}. Refresh and confirm.");
        var withheld = rate == 0m ? 0m : CashQuoter.Money(subtotal * rate / 100m);
        var paid = request.Payments!.Sum(p => p.Amount);
        if (paid != total - withheld)
            return RevenueSupport.Invalid("payments", withheld > 0
                ? $"Payments add up to ฿{paid:N2}; the bill is ฿{total:N2} less ฿{withheld:N2} withholding tax = ฿{total - withheld:N2}."
                : $"Payments add up to ฿{paid:N2}; the bill is ฿{total:N2}.");

        var now = calendar.Now;
        var me = caller.UserId();
        var shift = await OpenShiftAsync(db, branchId, me, caller.TenantId(), now, ct);
        var receiptNo = await RevenueNumberSeries.NextAsync(db, RevenueNumberSeries.Receipt, branchId, branch.BranchCode, branch.Local(now), ct);
        var bookings = charges.Select(c => (c.BookingId, c.OrderNo)).Distinct().ToList();
        var paidLines = new List<Charge>();
        var payer = request.Payer;
        var receipt = new Receipt
        {
            ReceiptId = Guid.CreateVersion7(), TenantId = caller.TenantId(), BranchId = branchId, ReceiptNo = receiptNo,
            ReceiptAt = now, ShiftId = shift.ShiftId, CashierUserId = me,
            BookingId = bookings.Count == 1 ? bookings[0].BookingId : null, OrderNo = bookings.Count == 1 ? bookings[0].OrderNo : null,
            PayerPartyCode = customer, PayerName = string.IsNullOrWhiteSpace(payer?.Name) ? party.Name : payer.Name.Trim(),
            PayerTaxId = payer?.TaxId?.Trim(), PayerBranchNo = payer?.BranchNo?.Trim(), PayerAddress = payer?.Address?.Trim(),
            Remarks = string.IsNullOrEmpty(remarks) ? null : remarks,
            CurrencyCode = charges.Select(c => c.CurrencyCode).First(), SubtotalAmount = subtotal, TaxAmount = tax, TotalAmount = total,
            Status = "ISSUED", WithholdingTaxRate = withheld > 0 ? rate : null, WithholdingTaxAmount = withheld,
            IdempotencyKey = key, IdempotencyHash = hash, IssuedFrom = "CASH_BILL",
        };
        db.Receipts.Add(receipt);

        short lineNo = 0;
        foreach (var source in charges.OrderBy(c => c.OrderNo).ThenBy(c => c.ContainerNo).ThenBy(c => c.MovementCode).ThenBy(c => c.ChargeCode))
        {
            var paidLine = source;
            if (source.Source == ChargeSource.Quote)
            {
                // The statement's expected line can never be PAID itself (ck_charge__quote_key): it is closed,
                // and the money is a WINDOW line with the same price.
                paidLine = new Charge();
                db.Entry(paidLine).CurrentValues.SetValues(db.Entry(source).CurrentValues);
                paidLine.ChargeId = Guid.CreateVersion7();
                paidLine.Source = ChargeSource.Window;
                paidLine.CreatedAt = now;
                paidLine.CreatedBy = me;
                db.Charges.Add(paidLine);
                source.Status = ChargeStatus.Cancelled;
                source.CancelledAt = now;
                source.CancelledBy = me;
                source.CancelReason = $"Paid on cash bill {receiptNo}";
                source.UpdatedAt = now;
            }
            var line = new ReceiptLine
            {
                ReceiptLineId = Guid.CreateVersion7(), TenantId = receipt.TenantId, ReceiptId = receipt.ReceiptId, LineNo = ++lineNo,
                ChargeId = paidLine.ChargeId, ChargeCode = paidLine.ChargeCode,
                Description = string.IsNullOrWhiteSpace(paidLine.ChargeName) ? paidLine.ChargeCode : paidLine.ChargeName,
                ContainerNo = paidLine.ContainerNo, MovementCode = paidLine.MovementCode, Quantity = paidLine.Quantity,
                UnitRate = paidLine.UnitRate ?? 0m, Amount = paidLine.Amount, TaxCode = paidLine.TaxCode, TaxRate = paidLine.TaxRate,
                TaxAmount = paidLine.TaxAmount,
            };
            db.ReceiptLines.Add(line);
            paidLine.Status = ChargeStatus.Paid;
            paidLine.ReceiptId = receipt.ReceiptId;
            paidLine.ReceiptLineId = line.ReceiptLineId;
            paidLine.UpdatedAt = now;
            paidLines.Add(paidLine);
        }

        foreach (var p in request.Payments!)
            db.ReceiptPayments.Add(new ReceiptPayment
            {
                TenantId = receipt.TenantId, ReceiptId = receipt.ReceiptId, Channel = p.Channel.Trim().ToUpperInvariant(),
                Amount = CashQuoter.Money(p.Amount), TenderedAmount = p.TenderedAmount is { } t ? CashQuoter.Money(t) : null,
                ReferenceNo = p.ReferenceNo?.Trim(), BankName = p.BankName?.Trim(),
            });

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            return RevenueSupport.Conflict("A line changed while billing it.", "Refresh the lines and choose again.");
        }

        // A box whose next move is now fully paid gets its gate coupon (owner 2026-10-08).
        var channels = request.Payments!.Select(p => p.Channel.Trim().ToUpperInvariant()).Distinct().ToList();
        foreach (var bookingId in paidLines.Where(c => c.BookingContainerId is not null).Select(c => c.BookingId).OfType<Guid>().Distinct())
            if (await db.BookingPlans.AsNoTracking().SingleOrDefaultAsync(p => p.BookingId == bookingId && p.Status == "OPEN", ct) is { } plan
                && await window.ContextAsync(plan, ct) is { } context)
                await window.CouponsForPrepaidAsync(context, paidLines, receipt, channels.Count == 1 ? channels[0] : "MIXED", ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Created($"/api/revenue/window/receipts/{receipt.ReceiptId}", (await document.ReadAsync(receipt.ReceiptId, ct))!);
    }

    /// <summary>The clerk's open shift at the branch, opened (float 0) when there is none — no drawer step to bill.</summary>
    private static async Task<Shift> OpenShiftAsync(RevenueDbContext db, Guid branchId, Guid cashier, Guid tenantId, DateTimeOffset now, CancellationToken ct)
    {
        Task<Shift?> Open() => db.Shifts.AsNoTracking()
            .SingleOrDefaultAsync(s => s.BranchId == branchId && s.CashierUserId == cashier && s.Status == "OPEN", ct);
        if (await Open() is { } open) return open;
        var shift = new Shift
        {
            ShiftId = Guid.CreateVersion7(), TenantId = tenantId, BranchId = branchId, CashierUserId = cashier,
            CurrencyCode = "THB", OpenedAt = now, OpeningFloat = 0m, Status = "OPEN",
        };
        db.Shifts.Add(shift);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(shift).State = EntityState.Detached;
            if (await Open() is { } other) return other;
            throw;
        }
        db.Entry(shift).State = EntityState.Detached;
        return shift;
    }

    // ── by number ─────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<ReceiptResponse>, NotFound<ProblemDetails>>> ByNumberAsync(
        string receiptNo, RevenueDbContext db, ReceiptDocument document, ICallerPermissions scope, CancellationToken ct)
    {
        var no = receiptNo.Trim().ToUpperInvariant();
        var found = await db.Receipts.AsNoTracking().Where(r => r.ReceiptNo == no).Select(r => new { r.ReceiptId, r.BranchId }).SingleOrDefaultAsync(ct);
        if (found is null || !scope.HasAt(RevenuePermissions.CashCollect, found.BranchId))
            return TypedResults.NotFound(new ProblemDetails { Title = $"No receipt {no} in your branches." });
        return TypedResults.Ok((await document.ReadAsync(found.ReceiptId, ct))!);
    }
}

/// <param name="Payer">The tax-invoice details as chosen (customer's name, tax id, branch, the chosen address);
/// stored on the receipt as given. Name null = the customer's name.</param>
/// <param name="WithholdingTaxRate">0 (none), 1 or 3 — % of the amount before VAT, kept back by the payer.</param>
/// <param name="ExpectedTotal">The total the screen showed (VAT included); a different total is refused.</param>
public sealed record CreateCashBillRequest(
    Guid? BranchId, string? CustomerCode, IReadOnlyList<Guid>? ChargeIds, PayerRequest? Payer, string? Remarks,
    decimal? WithholdingTaxRate, IReadOnlyList<PaymentRequest>? Payments, decimal? ExpectedTotal);

public sealed record CashBillLineResponse(
    Guid ChargeId, Guid BookingId, string OrderNo, string? CustomerCode, Guid? BookingContainerId, string? ContainerNo,
    string? EquipmentTypeCode, string? MovementCode, string ChargeCode, string? ChargeName, string PaymentTermCode, string BillTo,
    decimal Quantity, decimal UnitRate, decimal Amount, decimal TaxRate, decimal TaxAmount, decimal Total, string CurrencyCode, string Source);
