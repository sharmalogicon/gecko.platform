using System.Text.Json;
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

namespace Gecko.Revenue.Endpoints.Window;

/// <summary>
/// The cash window — PLAN_BILLING §4.2, clock 1. KORAKIT's whole billing: 51,434
/// cash receipts a year, ~140 a day, all at the counter.
///
///   GET  /window/bookings?orderNo=      what each box's next movement costs now
///   POST /window/shifts                 open my drawer (with a float)
///   GET  /window/shifts/current         my open drawer and what it should hold
///   POST /window/shifts/{id}/close      count it, record the variance
///   GET  /window/shifts/{id}/receipts   what this drawer has issued (for a reprint)
///   POST /window/receipts               take the money → RCT number → coupon
///   GET  /window/receipts/{id}          one receipt, as printed
///   GET  /window/receipts/{id}/receipt.pdf   the A4 receipt / tax invoice
///   POST /window/receipts/{id}/void     undo a wrong receipt before the box moves (revenue.receipt.void)
///   POST /window/waive                  forgive one line, by name, with a reason
///
/// A receipt is written in ONE transaction: the gap-free number, the PAID charges
/// with their price snapshots, the receipt, its lines and payments, and one
/// GateCouponIssued per box on the outbox. If any of it fails, none of it
/// happened — the number goes back and no box is released.
///
/// Prices are recalculated on every call and the client's total is checked
/// against ours. The window never charges a number the cashier did not see.
/// </summary>
internal static class WindowEndpoints
{
    private static readonly string[] Channels = ["CASH", "TRANSFER", "CHEQUE", "CARD"];

    public static RouteGroupBuilder MapWindowEndpoints(this RouteGroupBuilder revenue)
    {
        var window = revenue.MapGroup("/window").WithTags("Revenue — cash window");

        window.MapGet("/bookings", QuoteAsync).RequireBranchPermission(RevenuePermissions.CashCollect)
            .WithSummary("What a booking's boxes owe for their next movement — cash lines, storage to a date, what is already paid");
        window.MapPost("/shifts", OpenShiftAsync).RequireBranchPermission(RevenuePermissions.CashCollect)
            .WithSummary("Open my cash drawer at a branch");
        window.MapGet("/shifts/current", CurrentShiftAsync).RequireBranchPermission(RevenuePermissions.CashCollect)
            .WithSummary("My open drawer at a branch, and what it should hold by channel");
        window.MapPost("/shifts/{id:guid}/close", CloseShiftAsync).RequireBranchPermission(RevenuePermissions.CashCollect)
            .WithSummary("Count my drawer and close it; the variance is recorded per channel");
        window.MapGet("/shifts/{id:guid}/receipts", ShiftReceiptsAsync).RequireBranchPermission(RevenuePermissions.CashCollect)
            .WithSummary("The receipts a drawer has issued, newest first — the cashier's own drawer only");
        window.MapPost("/receipts", CreateReceiptAsync).RequireBranchPermission(RevenuePermissions.CashCollect)
            .WithSummary("Take payment: receipt / tax invoice, charges PAID, and a gate coupon per box");
        window.MapGet("/receipts/{id:guid}", GetReceiptAsync).RequireBranchPermission(RevenuePermissions.CashCollect)
            .WithSummary("One receipt, as printed");
        window.MapGet("/receipts/{id:guid}/receipt.pdf", ReceiptPdfAsync).RequireBranchPermission(RevenuePermissions.CashCollect)
            .WithSummary("The receipt as an A4 Thai full tax invoice (ใบเสร็จรับเงิน/ใบกำกับภาษี); a voided one prints with VOID");
        window.MapPost("/receipts/{id:guid}/void", VoidReceiptAsync).RequireBranchPermission(RevenuePermissions.ReceiptVoid)
            .WithSummary("Void a wrong receipt before the box has moved: it keeps its number, its charges are cancelled, its coupons are withdrawn");
        window.MapPost("/waive", WaiveAsync).RequireBranchPermission(RevenuePermissions.ChargeWaive)
            .WithSummary("Waive one quoted line, with a reason; if nothing is left to pay, the box is released");

        return revenue;
    }

    // ── the quote ───────────────────────────────────────────────────────────

    private static async Task<Results<Ok<WindowBookingResponse>, NotFound<ProblemDetails>, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> QuoteAsync(
        string? orderNo, DateOnly? paidUntil, RevenueDbContext db, WindowService window, ICallerPermissions permissions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orderNo)) return RevenueSupport.Invalid("orderNo", "Required.");

        var plan = await db.BookingPlans.AsNoTracking().SingleOrDefaultAsync(p => p.OrderNo == orderNo.Trim(), ct);
        if (plan is null) return NotKnown(orderNo);
        if (!permissions.HasAt(RevenuePermissions.CashCollect, plan.BranchId)) return TypedResults.Forbid();

        var context = await window.ContextAsync(plan, ct);
        if (context is null) return NoBranch(plan);
        if (paidUntil is { } until && until < context.Branch.LocalDate(context.Now))
            return RevenueSupport.Invalid("paidUntil", "Storage cannot be paid up to a day that has already gone.");

        var boxes = await window.QuoteBoxesAsync(context, null, paidUntil, ct);
        var settled = await db.Charges.AsNoTracking()
            .Where(c => c.BookingId == plan.BookingId && c.Source == ChargeSource.Window && c.Status != ChargeStatus.Cancelled)
            .ToListAsync(ct);

        var responses = boxes.Select(b => new WindowBoxResponse(
            b.Box.BookingContainerId, b.Box.ContainerNo, b.Box.EquipmentTypeCode,
            b.Step?.MovementCode, b.Rules?.Direction, b.Rules?.IsBillable ?? false,
            b.Quote?.Stay?.InAt, b.Quote?.StayDays,
            (b.Quote?.Lines ?? []).Select(ToResponse).ToList(),
            settled.Where(c => c.BookingContainerId == b.Box.BookingContainerId)
                .Select(c => new SettledChargeResponse(c.ChargeId, c.ChargeCode, c.Status, c.Amount + c.TaxAmount, c.CouponRef, c.ServiceTo, c.WaiveReason))
                .ToList(),
            (b.Quote?.Tried ?? []).Select(t => new TriedVariantResponse(t.ChargeCode, t.BillTo, t.Outcome, t.Amount)).ToList(),
            b.Quote?.Total ?? 0, b.Note)).ToList();

        var lines = boxes.SelectMany(b => b.Quote?.Lines ?? []).ToList();
        var unreplaced = await db.Receipts.AsNoTracking()
            .Where(r => r.BookingId == plan.BookingId && r.Status == "VOIDED" && !db.Receipts.Any(n => n.ReplacesReceiptId == r.ReceiptId))
            .OrderByDescending(r => r.VoidedAt)
            .Select(r => new VoidedReceiptResponse(r.ReceiptId, r.ReceiptNo, r.VoidedAt, r.VoidReason, r.TotalAmount))
            .ToListAsync(ct);
        return TypedResults.Ok(new WindowBookingResponse(
            plan.BookingId, plan.BranchId, plan.OrderNo, plan.Status, plan.OrderTypeCode,
            plan.CustomerPartyCode, plan.AgentPartyCode, plan.LineCode,
            boxes.Select(b => b.Quote?.PaidUntil).FirstOrDefault(d => d is not null),
            context.Branch.LocalDate(context.Now),
            responses, lines.Sum(l => l.Amount), lines.Sum(l => l.TaxAmount), lines.Sum(l => l.Total),
            lines.Select(l => l.CurrencyCode).FirstOrDefault(), unreplaced));
    }

    // ── the drawer ──────────────────────────────────────────────────────────

    private static async Task<Results<Created<ShiftResponse>, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> OpenShiftAsync(
        OpenShiftRequest request, RevenueDbContext db, ITenantContext caller, ICallerPermissions permissions, TimeProvider clock, CancellationToken ct)
    {
        if (request.OpeningFloat < 0) return RevenueSupport.Invalid("openingFloat", "Cannot be negative.");
        if (!permissions.HasAt(RevenuePermissions.CashCollect, request.BranchId)) return TypedResults.Forbid();

        var me = caller.UserId();
        if (await db.Shifts.AnyAsync(s => s.BranchId == request.BranchId && s.CashierUserId == me && s.Status == "OPEN", ct))
            return RevenueSupport.Conflict("Your drawer is already open at this branch.", "Close it before opening another.");

        var shift = new Shift
        {
            ShiftId = Guid.CreateVersion7(), TenantId = caller.TenantId(), BranchId = request.BranchId, CashierUserId = me,
            CurrencyCode = string.IsNullOrWhiteSpace(request.CurrencyCode) ? "THB" : request.CurrencyCode.Trim().ToUpperInvariant(),
            OpenedAt = clock.GetUtcNow(), OpeningFloat = CashQuoter.Money(request.OpeningFloat), Status = "OPEN",
        };
        db.Shifts.Add(shift);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return RevenueSupport.Conflict("Your drawer is already open at this branch.");
        }
        return TypedResults.Created($"/api/revenue/window/shifts/{shift.ShiftId}", await ShiftResponseAsync(db, shift, ct));
    }

    private static async Task<Results<Ok<ShiftResponse>, NotFound<ProblemDetails>, ForbidHttpResult>> CurrentShiftAsync(
        Guid branchId, RevenueDbContext db, ITenantContext caller, ICallerPermissions permissions, CancellationToken ct)
    {
        if (!permissions.HasAt(RevenuePermissions.CashCollect, branchId)) return TypedResults.Forbid();
        var me = caller.UserId();
        var shift = await db.Shifts.AsNoTracking().SingleOrDefaultAsync(s => s.BranchId == branchId && s.CashierUserId == me && s.Status == "OPEN", ct);
        return shift is null
            ? TypedResults.NotFound(new ProblemDetails { Title = "No open drawer.", Detail = "Open one before taking payment." })
            : TypedResults.Ok(await ShiftResponseAsync(db, shift, ct));
    }

    private static async Task<Results<Ok<ShiftResponse>, NotFound<ProblemDetails>, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> CloseShiftAsync(
        Guid id, CloseShiftRequest request, RevenueDbContext db, ITenantContext caller, TimeProvider clock, CancellationToken ct)
    {
        var shift = await db.Shifts.SingleOrDefaultAsync(s => s.ShiftId == id, ct);
        if (shift is null) return TypedResults.NotFound(new ProblemDetails { Title = "No such drawer." });
        if (shift.CashierUserId != caller.UserId()) return TypedResults.Forbid();
        if (shift.Status != "OPEN") return RevenueSupport.Conflict("This drawer is already closed.");

        var expected = await ExpectedAsync(db, shift, ct);
        var counts = (request.Counts ?? []).ToDictionary(c => c.Channel.Trim().ToUpperInvariant(), c => c.CountedAmount);
        var errors = new Dictionary<string, List<string>>();
        foreach (var channel in counts.Keys.Where(c => !Channels.Contains(c))) errors.Add("counts", $"'{channel}' is not a channel ({string.Join(", ", Channels)}).");
        foreach (var e in expected.Where(e => e.Amount > 0 && !counts.ContainsKey(e.Channel))) errors.Add("counts", $"{e.Channel} was taken in this shift and must be counted.");
        if (counts.Values.Any(v => v < 0)) errors.Add("counts", "A count cannot be negative.");
        if (errors.Count > 0) return RevenueSupport.Invalid(errors);

        var now = clock.GetUtcNow();
        foreach (var channel in Channels.Where(c => counts.ContainsKey(c) || expected.Any(e => e.Channel == c && e.Amount > 0)))
            db.ShiftCounts.Add(new ShiftCount
            {
                TenantId = shift.TenantId, ShiftId = shift.ShiftId, Channel = channel,
                ExpectedAmount = expected.SingleOrDefault(e => e.Channel == channel)?.Amount ?? 0,
                CountedAmount = CashQuoter.Money(counts.GetValueOrDefault(channel)),
            });
        shift.Status = "CLOSED";
        shift.ClosedAt = now;
        shift.ClosedBy = caller.UserId();
        shift.CloseNote = request.Note;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await ShiftResponseAsync(db, shift, ct));
    }

    private static async Task<Results<Ok<List<ShiftReceiptResponse>>, NotFound<ProblemDetails>, ForbidHttpResult>> ShiftReceiptsAsync(
        Guid id, RevenueDbContext db, ITenantContext caller, ICallerPermissions permissions, CancellationToken ct)
    {
        var shift = await db.Shifts.AsNoTracking().SingleOrDefaultAsync(s => s.ShiftId == id, ct);
        if (shift is null) return TypedResults.NotFound(new ProblemDetails { Title = "No such drawer." });
        if (!permissions.HasAt(RevenuePermissions.CashCollect, shift.BranchId) || shift.CashierUserId != caller.UserId())
            return TypedResults.Forbid();

        var receipts = await db.Receipts.AsNoTracking()
            .Where(r => r.ShiftId == id)
            .OrderByDescending(r => r.ReceiptAt).ThenByDescending(r => r.ReceiptNo)
            .Select(r => new ShiftReceiptResponse(r.ReceiptId, r.ReceiptNo, r.ReceiptAt, r.OrderNo, r.PayerName,
                r.TotalAmount, r.CurrencyCode, r.Status))
            .ToListAsync(ct);
        return TypedResults.Ok(receipts);
    }

    // ── the receipt ─────────────────────────────────────────────────────────

    private static async Task<Results<Created<ReceiptResponse>, NotFound<ProblemDetails>, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> CreateReceiptAsync(
        CreateReceiptRequest request, RevenueDbContext db, WindowService window, ReceiptDocument document, IMasterDataReferences master,
        ITenantContext caller, ICallerPermissions permissions, CancellationToken ct)
    {
        var errors = new Dictionary<string, List<string>>();
        if (request.BookingContainerIds is not { Count: > 0 }) errors.Add("bookingContainerIds", "Name at least one box.");
        if (request.Payments is not { Count: > 0 }) errors.Add("payments", "How was it paid?");
        foreach (var p in request.Payments ?? [])
        {
            var channel = p.Channel?.Trim().ToUpperInvariant() ?? "";
            if (!Channels.Contains(channel)) errors.Add("payments", $"'{p.Channel}' is not a channel ({string.Join(", ", Channels)}).");
            if (p.Amount <= 0) errors.Add("payments", "Every payment must be more than zero.");
            if (channel == "CASH" && p.TenderedAmount is { } tendered && tendered < p.Amount) errors.Add("payments", "Cash handed over is less than the amount.");
            if (channel != "CASH" && string.IsNullOrWhiteSpace(p.ReferenceNo)) errors.Add("payments", $"A {channel} payment needs its reference (slip, cheque or approval number).");
            if (channel != "CASH" && p.TenderedAmount is not null) errors.Add("payments", "Only cash has an amount handed over.");
        }
        if (errors.Count > 0) return RevenueSupport.Invalid(errors);

        var plan = await db.BookingPlans.AsNoTracking().SingleOrDefaultAsync(p => p.BookingId == request.BookingId, ct);
        if (plan is null) return NotKnown(request.BookingId.ToString());
        if (!permissions.HasAt(RevenuePermissions.CashCollect, plan.BranchId)) return TypedResults.Forbid();
        if (plan.Status != "OPEN") return RevenueSupport.Conflict($"Booking {plan.OrderNo} is {plan.Status}.");

        var me = caller.UserId();
        var shift = await db.Shifts.AsNoTracking().SingleOrDefaultAsync(s => s.BranchId == plan.BranchId && s.CashierUserId == me && s.Status == "OPEN", ct);
        if (shift is null) return RevenueSupport.Conflict("Your drawer is not open at this branch.", "Open it (POST /window/shifts) before taking payment.");

        var context = await window.ContextAsync(plan, ct);
        if (context is null) return NoBranch(plan);
        if (request.PaidUntil is { } until && until < context.Branch.LocalDate(context.Now))
            return RevenueSupport.Invalid("paidUntil", "Storage cannot be paid up to a day that has already gone.");

        var boxes = await window.QuoteBoxesAsync(context, request.BookingContainerIds!.ToHashSet(), request.PaidUntil, ct);
        var missing = request.BookingContainerIds!.Where(id => boxes.All(b => b.Box.BookingContainerId != id)).ToList();
        if (missing.Count > 0) return RevenueSupport.Invalid("bookingContainerIds", $"Not on {plan.OrderNo}: {string.Join(", ", missing)}.");
        var payable = boxes.Where(b => b.Quote is { Lines.Count: > 0 }).ToList();
        var idle = boxes.Except(payable).ToList();
        if (idle.Count > 0)
            return RevenueSupport.Conflict("Nothing to pay for some of these boxes.",
                string.Join(" ", idle.Select(b => $"{b.Box.ContainerNo ?? b.Box.BookingContainerId.ToString()}: {b.Note ?? "nothing due"}.")));

        var total = payable.Sum(b => b.Quote!.Total);
        if (total != request.ExpectedTotal)
            return RevenueSupport.Conflict("The price changed.",
                $"The window shows ฿{request.ExpectedTotal:N2}; it is now ฿{total:N2}. Re-open the booking and confirm the new amount.");
        var paid = request.Payments!.Sum(p => p.Amount);
        if (paid != total)
            return RevenueSupport.Invalid("payments", $"Payments add up to ฿{paid:N2}; the receipt is ฿{total:N2}.");

        if (request.ReplacesReceiptId is { } replacesId)
        {
            var replaced = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.ReceiptId == replacesId, ct);
            if (replaced is null || replaced.BookingId != plan.BookingId)
                return RevenueSupport.Invalid("replacesReceiptId", $"Not a receipt of {plan.OrderNo}.");
            if (replaced.Status != "VOIDED")
                return RevenueSupport.Invalid("replacesReceiptId", $"{replaced.ReceiptNo} is not voided; only a voided receipt is replaced.");
            if (await db.Receipts.AnyAsync(r => r.ReplacesReceiptId == replacesId, ct))
                return RevenueSupport.Conflict($"{replaced.ReceiptNo} has already been replaced.");
        }

        var payerName = request.Payer?.Name?.Trim();
        if (string.IsNullOrEmpty(payerName) && plan.CustomerPartyCode is { } customer)
            payerName = (await master.PartiesAsync([customer], ct)).GetValueOrDefault(customer)?.Name;

        try
        {
            var (receipt, coupons) = await window.IssueReceiptAsync(context, shift, payable, request.Payments!,
                string.IsNullOrEmpty(payerName) ? "Walk-in customer" : payerName, request.Payer, me, request.ReplacesReceiptId, ct);
            return TypedResults.Created($"/api/revenue/window/receipts/{receipt.ReceiptId}",
                (await document.ReadAsync(receipt.ReceiptId, ct,
                    coupons.Select(c => new CouponResponse(c.CouponRef, c.ContainerNo, c.MovementCode, c.ValidUntil)).ToList()))!);
        }
        catch (DbUpdateException)
        {
            // uq_charge__window: the same box and movement were paid a moment ago at another counter.
            return RevenueSupport.Conflict("Already paid.", "Another receipt for these boxes was just issued. Re-open the booking.");
        }
    }

    private static async Task<Results<Ok<ReceiptResponse>, NotFound<ProblemDetails>, ForbidHttpResult>> GetReceiptAsync(
        Guid id, RevenueDbContext db, ReceiptDocument document, ICallerPermissions permissions, CancellationToken ct)
    {
        var branchId = await db.Receipts.AsNoTracking().Where(r => r.ReceiptId == id).Select(r => (Guid?)r.BranchId).SingleOrDefaultAsync(ct);
        if (branchId is null) return TypedResults.NotFound(new ProblemDetails { Title = "No such receipt." });
        if (!permissions.HasAt(RevenuePermissions.CashCollect, branchId.Value)) return TypedResults.Forbid();
        return TypedResults.Ok((await document.ReadAsync(id, ct))!);
    }

    private static async Task<Results<FileContentHttpResult, NotFound<ProblemDetails>, ForbidHttpResult>> ReceiptPdfAsync(
        Guid id, RevenueDbContext db, ReceiptDocument document, ICallerPermissions permissions, CancellationToken ct)
    {
        var branchId = await db.Receipts.AsNoTracking().Where(r => r.ReceiptId == id).Select(r => (Guid?)r.BranchId).SingleOrDefaultAsync(ct);
        if (branchId is null) return TypedResults.NotFound(new ProblemDetails { Title = "No such receipt." });
        if (!permissions.HasAt(RevenuePermissions.CashCollect, branchId.Value)) return TypedResults.Forbid();

        var rendered = await document.RenderAsync(id, ct);
        return rendered is null
            ? TypedResults.NotFound(new ProblemDetails { Title = "No such receipt." })
            : TypedResults.File(rendered.Pdf, "application/pdf", rendered.FileName);
    }

    // ── voiding ─────────────────────────────────────────────────────────────

    /// <summary>
    /// INVOICING_PROPOSAL part A. A receipt keyed wrong (customer, box, amount) is
    /// voided, not edited: it keeps its number and prints VOID, its charges become
    /// CANCELLED (so the window quotes them again), and each coupon it issued is
    /// withdrawn at the barrier. The customer then pays on a new receipt that names
    /// this one. Refused once any of its charges is EARNED — the box went through on
    /// it, the move happened, and the fix is a credit note, not a void.
    ///
    /// Known gap: a gate event reaches Revenue a few seconds after the barrier. A void
    /// in that window goes through here, and TOS logs the coupon as spent-then-revoked.
    /// </summary>
    private static async Task<Results<Ok<ReceiptResponse>, NotFound<ProblemDetails>, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> VoidReceiptAsync(
        Guid id, VoidReceiptRequest request, RevenueDbContext db, ReceiptDocument document, ITenantContext caller,
        ICallerPermissions permissions, TimeProvider clock, CancellationToken ct)
    {
        var receipt = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.ReceiptId == id, ct);
        if (receipt is null) return TypedResults.NotFound(new ProblemDetails { Title = "No such receipt." });
        if (!permissions.HasAt(RevenuePermissions.ReceiptVoid, receipt.BranchId)) return TypedResults.Forbid();

        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length < 5) return RevenueSupport.Invalid("reason", "Say what was wrong — it is kept on the receipt and printed with VOID.");
        if (reason.Length > 300) return RevenueSupport.Invalid("reason", "At most 300 characters.");
        if (receipt.Status == "VOIDED") return RevenueSupport.Conflict($"{receipt.ReceiptNo} is already voided.");

        var charges = await db.Charges.Where(c => c.ReceiptId == id).ToListAsync(ct);
        var moved = charges.Where(c => c.Status == ChargeStatus.Earned).Select(c => c.ContainerNo ?? c.MovementCode).Distinct().ToList();
        if (moved.Count > 0)
            return RevenueSupport.Conflict($"{receipt.ReceiptNo} cannot be voided: the box has already moved on it.",
                $"{string.Join(", ", moved)} went through the gate on this receipt. The fix is a credit note, not a void.");

        var now = clock.GetUtcNow();
        var by = caller.UserId();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Only the void columns may change on a receipt (15_cashier grants), and only once.
        var voided = await db.Receipts.Where(r => r.ReceiptId == id && r.Status == "ISSUED")
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "VOIDED").SetProperty(r => r.VoidedAt, now)
                .SetProperty(r => r.VoidedBy, by).SetProperty(r => r.VoidReason, reason)
                .SetProperty(r => r.UpdatedAt, now).SetProperty(r => r.UpdatedBy, by), ct);
        if (voided == 0) return RevenueSupport.Conflict($"{receipt.ReceiptNo} was voided a moment ago.");

        foreach (var charge in charges.Where(c => c.Status == ChargeStatus.Paid))
        {
            charge.Status = ChargeStatus.Cancelled;
            charge.CancelledAt = now;
            charge.CancelledBy = by;
            charge.CancelReason = $"Receipt {receipt.ReceiptNo} voided: {reason}";
            charge.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);

        foreach (var couponRef in charges.Select(c => c.CouponRef).OfType<string>().Distinct())
            await RevenueOutbox.EnqueueAsync(db, receipt.TenantId, "RECEIPT", receipt.ReceiptId, RevenueOutbox.CouponRevoked,
                new CouponRevokedPayload(null, couponRef, by, $"Receipt {receipt.ReceiptNo} voided: {reason}"), ct);
        await transaction.CommitAsync(ct);

        return TypedResults.Ok((await document.ReadAsync(id, ct))!);
    }

    // ── waiving ─────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<WaiveResponse>, NotFound<ProblemDetails>, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> WaiveAsync(
        WaiveRequest request, RevenueDbContext db, WindowService window, ITenantContext caller, ICallerPermissions permissions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 5)
            return RevenueSupport.Invalid("reason", "Say why — it is kept with the charge.");

        var box = await db.BookingPlanContainers.AsNoTracking().SingleOrDefaultAsync(b => b.BookingContainerId == request.BookingContainerId, ct);
        var plan = box is null ? null : await db.BookingPlans.AsNoTracking().SingleOrDefaultAsync(p => p.BookingId == box.BookingId, ct);
        if (box is null || plan is null) return NotKnown(request.BookingContainerId.ToString());
        if (!permissions.HasAt(RevenuePermissions.ChargeWaive, plan.BranchId)) return TypedResults.Forbid();

        var context = await window.ContextAsync(plan, ct);
        if (context is null) return NoBranch(plan);

        var quoted = (await window.QuoteBoxesAsync(context, new HashSet<Guid> { box.BookingContainerId }, request.PaidUntil, ct)).Single();
        var line = quoted.Quote?.Lines.FirstOrDefault(l =>
            string.Equals(l.ChargeCode, request.ChargeCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(l.BillTo, request.BillTo, StringComparison.OrdinalIgnoreCase));
        if (line is null)
            return RevenueSupport.Conflict($"{request.ChargeCode} is not due on this box.", quoted.Note);

        try
        {
            var (charge, coupon) = await window.WaiveAsync(context, quoted, line, request.Reason.Trim(), caller.UserId(), ct);
            return TypedResults.Ok(new WaiveResponse(charge.ChargeId, charge.ChargeCode, charge.Amount + charge.TaxAmount, coupon));
        }
        catch (DbUpdateException)
        {
            return RevenueSupport.Conflict("Already settled.", "This line was paid or waived a moment ago. Re-open the booking.");
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static QuoteLineResponse ToResponse(QuoteLine l) =>
        new(l.Kind, l.ChargeCode, l.ChargeName, l.BillTo, l.PayerPartyCode, l.Quantity, l.UnitRate, l.Amount,
            l.TaxCode, l.TaxRate, l.TaxAmount, l.Total, l.ServiceFrom, l.ServiceTo, l.Price.ScheduleNo);

    private static NotFound<ProblemDetails> NotKnown(string what) =>
        TypedResults.NotFound(new ProblemDetails
        {
            Title = $"'{what}' is not a booking the cash window knows.",
            Detail = "Revenue learns bookings from TOS (BookingChanged). A booking made a moment ago may still be on its way.",
        });

    private static ProblemHttpResult NoBranch(BookingPlan plan) =>
        RevenueSupport.Conflict("This booking's branch has no master-data profile.",
            $"Branch {plan.BranchId} needs a code and a time zone in MDM before its receipts can be numbered.");

    private static async Task<List<ChannelAmount>> ExpectedAsync(RevenueDbContext db, Shift shift, CancellationToken ct)
    {
        var taken = await (
                from p in db.ReceiptPayments
                join r in db.Receipts on p.ReceiptId equals r.ReceiptId
                where r.ShiftId == shift.ShiftId && r.Status == "ISSUED"
                group p by p.Channel into g
                select new { Channel = g.Key, Amount = g.Sum(x => x.Amount) })
            .ToListAsync(ct);
        return Channels.Select(c => new ChannelAmount(c,
                (c == "CASH" ? shift.OpeningFloat : 0) + (taken.SingleOrDefault(t => t.Channel == c)?.Amount ?? 0)))
            .ToList();
    }

    private static async Task<ShiftResponse> ShiftResponseAsync(RevenueDbContext db, Shift shift, CancellationToken ct)
    {
        var counts = await db.ShiftCounts.AsNoTracking().Where(c => c.ShiftId == shift.ShiftId).ToListAsync(ct);
        var receipts = await db.Receipts.CountAsync(r => r.ShiftId == shift.ShiftId && r.Status == "ISSUED", ct);
        return new ShiftResponse(shift.ShiftId, shift.BranchId, shift.CashierUserId, shift.CurrencyCode, shift.OpenedAt,
            shift.OpeningFloat, shift.Status, shift.ClosedAt, receipts, await ExpectedAsync(db, shift, ct),
            counts.Select(c => new ShiftCountResponse(c.Channel, c.ExpectedAmount, c.CountedAmount, c.CountedAmount - c.ExpectedAmount)).ToList());
    }
}

/// <summary>A booking at the window: its plan, its branch clock and "now", resolved once per request.</summary>
internal sealed record WindowContext(BookingPlan Plan, BranchClockInfo Branch, OrderTypePlanRef? OrderType, DateTimeOffset Now);

/// <summary>One box as the window sees it: its next step and what that step costs.</summary>
internal sealed record QuotedBox(BookingPlanContainer Box, PlanStep? Step, OrderTypeStepRef? Rules, MovementQuote? Quote, string? Note);

/// <summary>The writes behind the window, each in one transaction.</summary>
internal sealed class WindowService(RevenueDbContext db, CashQuoter quoter, BranchCalendar calendar, IMasterDataReferences master)
{
    public async Task<WindowContext?> ContextAsync(BookingPlan plan, CancellationToken ct)
    {
        var branch = await calendar.BranchAsync(plan.BranchId, ct);
        if (branch is null) return null;
        var orderType = (await master.OrderTypePlansAsync([plan.OrderTypeCode], ct)).GetValueOrDefault(plan.OrderTypeCode);
        return new WindowContext(plan, branch, orderType, calendar.Now);
    }

    public async Task<List<QuotedBox>> QuoteBoxesAsync(WindowContext context, IReadOnlySet<Guid>? only, DateOnly? paidUntil, CancellationToken ct)
    {
        var boxes = await db.BookingPlanContainers.AsNoTracking()
            .Where(b => b.BookingId == context.Plan.BookingId && b.IsCurrent)
            .OrderBy(b => b.ContainerNo)
            .ToListAsync(ct);
        if (only is not null) boxes = boxes.Where(b => only.Contains(b.BookingContainerId)).ToList();

        var result = new List<QuotedBox>();
        foreach (var box in boxes)
        {
            if (box.EndReason is not null) { result.Add(new QuotedBox(box, null, null, null, $"Left the booking ({box.EndReason}).")); continue; }
            if (CashQuoter.NextStep(box, context.OrderType) is not ({ } step, var rules)) { result.Add(new QuotedBox(box, null, null, null, "Every step is done.")); continue; }
            if (rules is null) { result.Add(new QuotedBox(box, step, null, null, $"{step.MovementCode} is not a step of {context.Plan.OrderTypeCode} in MDM.")); continue; }
            if (!rules.IsBillable) { result.Add(new QuotedBox(box, step, rules, null, $"{step.MovementCode} is not billable.")); continue; }

            var quote = await quoter.QuoteAsync(context.Plan, box, rules, context.Branch, paidUntil, context.Now, ct);
            result.Add(new QuotedBox(box, step, rules, quote,
                quote.Lines.Count > 0 ? null
                : quote.Tried.Any(t => t.Outcome == "SETTLED") ? $"{step.MovementCode} is already paid."
                : $"Nothing is charged in cash for {step.MovementCode}."));
        }
        return result;
    }

    public async Task<(Receipt Receipt, List<CouponIssuedPayload> Coupons)> IssueReceiptAsync(WindowContext context, Shift shift, List<QuotedBox> payable,
        IReadOnlyList<PaymentRequest> payments, string payerName, PayerRequest? payer, Guid cashier, Guid? replacesReceiptId, CancellationToken ct)
    {
        var plan = context.Plan;
        var now = context.Now;
        var channels = payments.Select(p => p.Channel.Trim().ToUpperInvariant()).Distinct().ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var receiptNo = await RevenueNumberSeries.NextAsync(db, RevenueNumberSeries.Receipt, plan.BranchId, context.Branch.BranchCode,
            context.Branch.Local(now), ct);

        var receipt = new Receipt
        {
            ReceiptId = Guid.CreateVersion7(), TenantId = plan.TenantId, BranchId = plan.BranchId, ReceiptNo = receiptNo,
            ReceiptAt = now, ShiftId = shift.ShiftId, CashierUserId = cashier, BookingId = plan.BookingId, OrderNo = plan.OrderNo,
            PayerPartyCode = plan.CustomerPartyCode, PayerName = payerName, PayerTaxId = payer?.TaxId?.Trim(),
            PayerBranchNo = payer?.BranchNo?.Trim(), PayerAddress = payer?.Address?.Trim(),
            CurrencyCode = payable.Select(b => b.Quote!.CurrencyCode).First() ?? "THB",
            SubtotalAmount = payable.Sum(b => b.Quote!.Subtotal), TaxAmount = payable.Sum(b => b.Quote!.Tax),
            Status = "ISSUED", ReplacesReceiptId = replacesReceiptId,
        };
        receipt.TotalAmount = receipt.SubtotalAmount + receipt.TaxAmount;
        db.Receipts.Add(receipt);

        var coupons = new List<CouponIssuedPayload>();
        short lineNo = 0;
        var boxIndex = 0;
        foreach (var box in payable)
        {
            var quote = box.Quote!;
            var couponRef = $"{receiptNo}-{++boxIndex}";
            foreach (var line in quote.Lines)
            {
                var charge = NewCharge(plan, box, line, ChargeStatus.Paid, now);
                charge.ReceiptId = receipt.ReceiptId;
                charge.CouponRef = couponRef;
                var receiptLine = new ReceiptLine
                {
                    ReceiptLineId = Guid.CreateVersion7(), TenantId = plan.TenantId, ReceiptId = receipt.ReceiptId, LineNo = ++lineNo,
                    ChargeId = charge.ChargeId, ChargeCode = line.ChargeCode, Description = Describe(line), ContainerNo = box.Box.ContainerNo,
                    MovementCode = quote.MovementCode, Quantity = line.Quantity, UnitRate = line.UnitRate, Amount = line.Amount,
                    TaxCode = line.TaxCode, TaxRate = line.TaxRate, TaxAmount = line.TaxAmount,
                };
                charge.ReceiptLineId = receiptLine.ReceiptLineId;
                db.Charges.Add(charge);
                db.ReceiptLines.Add(receiptLine);
            }
            await RecordPricingAsync(plan, box, now, ct);

            coupons.Add(new CouponIssuedPayload(
                CouponId: Guid.CreateVersion7(), BranchId: plan.BranchId, BookingId: plan.BookingId, ContainerNo: box.Box.ContainerNo,
                MovementCode: quote.MovementCode, CouponRef: couponRef,
                Channel: channels.Count == 1 ? channels[0] : "MIXED", Amount: quote.Total, CurrencyCode: receipt.CurrencyCode,
                ValidFrom: now, ValidUntil: ValidUntil(context, quote), IssuedBy: cashier));
        }

        foreach (var p in payments)
            db.ReceiptPayments.Add(new ReceiptPayment
            {
                TenantId = plan.TenantId, ReceiptId = receipt.ReceiptId, Channel = p.Channel.Trim().ToUpperInvariant(),
                Amount = CashQuoter.Money(p.Amount), TenderedAmount = p.TenderedAmount is { } t ? CashQuoter.Money(t) : null,
                ReferenceNo = p.ReferenceNo?.Trim(), BankName = p.BankName?.Trim(),
            });

        await db.SaveChangesAsync(ct);
        foreach (var coupon in coupons)
            await RevenueOutbox.EnqueueAsync(db, plan.TenantId, "RECEIPT", receipt.ReceiptId, RevenueOutbox.CouponIssued, coupon, ct);
        await transaction.CommitAsync(ct);
        return (receipt, coupons);
    }

    public async Task<(Charge Charge, CouponResponse? Coupon)> WaiveAsync(WindowContext context, QuotedBox box, QuoteLine line,
        string reason, Guid by, CancellationToken ct)
    {
        var now = context.Now;
        var charge = NewCharge(context.Plan, box, line, ChargeStatus.Waived, now);
        charge.WaivedAt = now;
        charge.WaivedBy = by;
        charge.WaiveReason = reason;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Charges.Add(charge);
        await db.SaveChangesAsync(ct);

        // Nothing left to pay on this move → the box goes on the waiver, no receipt.
        CouponResponse? coupon = null;
        var remaining = box.Quote!.Lines.Where(l => l != line).ToList();
        if (remaining.Count == 0)
        {
            var payload = new CouponIssuedPayload(
                CouponId: Guid.CreateVersion7(), BranchId: context.Plan.BranchId, BookingId: context.Plan.BookingId,
                ContainerNo: box.Box.ContainerNo, MovementCode: box.Quote.MovementCode,
                CouponRef: $"WAIVE-{context.Plan.OrderNo}-{charge.ChargeId.ToString("N")[..6].ToUpperInvariant()}",
                Channel: "WAIVED", Amount: null, CurrencyCode: null, ValidFrom: now, ValidUntil: ValidUntil(context, box.Quote), IssuedBy: by);
            charge.CouponRef = payload.CouponRef;
            await db.SaveChangesAsync(ct);
            await RevenueOutbox.EnqueueAsync(db, context.Plan.TenantId, "CHARGE", charge.ChargeId, RevenueOutbox.CouponIssued, payload, ct);
            coupon = new CouponResponse(payload.CouponRef, payload.ContainerNo, payload.MovementCode, payload.ValidUntil);
        }
        await transaction.CommitAsync(ct);
        return (charge, coupon);
    }

    /// <summary>
    /// A gate-out that carries storage is paid UP TO a day (Q5): the coupon dies at
    /// the end of it, branch time, and a later truck pays the extra days. Anything
    /// else lives as long as the booking.
    /// </summary>
    private static DateTimeOffset ValidUntil(WindowContext context, MovementQuote quote) =>
        quote.StorageApplies && quote.PaidUntil is { } until
            ? context.Branch.EndOfDay(until)
            : context.Plan.ValidTo is { } to && to > context.Now ? to : context.Now.AddDays(30);

    private static Charge NewCharge(BookingPlan plan, QuotedBox box, QuoteLine line, string status, DateTimeOffset now)
    {
        var p = line.Price;
        return new Charge
        {
            ChargeId = Guid.CreateVersion7(), TenantId = plan.TenantId, BranchId = plan.BranchId, Source = ChargeSource.Window,
            BookingId = plan.BookingId, OrderNo = plan.OrderNo, BookingContainerId = box.Box.BookingContainerId,
            ContainerNo = box.Box.ContainerNo, MovementCode = box.Quote!.MovementCode,
            ContainerStayId = line.Kind is QuoteLine.Storage or QuoteLine.Reefer ? box.Quote.Stay?.ContainerStayId : null,
            ServiceFrom = line.ServiceFrom, ServiceTo = line.ServiceTo,
            ChargeCodeId = line.ChargeCodeId, ChargeCode = line.ChargeCode, ChargeName = line.ChargeName,
            BillTo = line.BillTo, PaymentTermCode = line.PaymentTermCode, PayerPartyCode = line.PayerPartyCode,
            Quantity = line.Quantity, UnitRate = line.UnitRate, Amount = line.Amount, CurrencyCode = line.CurrencyCode,
            TaxCode = line.TaxCode, TaxRate = line.TaxRate, TaxAmount = line.TaxAmount,
            PricedForDate = p.PricedForDate, ScheduleId = p.ScheduleId, ScheduleNo = p.ScheduleNo, ScheduleVersionNo = p.VersionNo,
            ScheduleType = p.ScheduleType, ScopeRank = p.ScopeRank, TosRateId = p.TosRateId, RateRowVersion = p.RateRowVersion,
            Specificity = p.Specificity, PricingMethod = p.PricingMethod, BillingUnitCode = p.BillingUnitCode,
            PricesIncludeTax = p.PricesIncludeTax, BaseRate = p.BaseRate, FreeUnits = p.FreeUnits,
            ChargeableQuantity = p.ChargeableQuantity, ResolvedAt = p.ResolvedAt, PriceSnapshotJson = JsonSerializer.Serialize(p),
            Status = status, CreatedAt = now, UpdatedAt = now,
        };
    }

    /// <summary>The movement's pricing record (§4.1): what was tried, kept once per box × movement for the cash clock.</summary>
    private async Task RecordPricingAsync(BookingPlan plan, QuotedBox box, DateTimeOffset now, CancellationToken ct)
    {
        var quote = box.Quote!;
        var record = await db.MovementPricings.SingleOrDefaultAsync(m =>
            m.BookingContainerId == box.Box.BookingContainerId && m.MovementCode == quote.MovementCode && m.Clock == "CASH", ct);
        if (record is null)
        {
            record = new MovementPricing { TenantId = plan.TenantId, BranchId = plan.BranchId, Clock = "CASH", MovementCode = quote.MovementCode, CreatedAt = now };
            db.MovementPricings.Add(record);
        }
        record.BookingId = plan.BookingId;
        record.BookingContainerId = box.Box.BookingContainerId;
        record.ContainerNo = box.Box.ContainerNo;
        record.OrderTypeCode = plan.OrderTypeCode;
        record.VariantsTried = quote.Tried.Count;
        record.VariantsPriced = quote.Lines.Count;
        record.TotalAmount = quote.Subtotal;
        record.CurrencyCode = quote.CurrencyCode;
        record.TrailJson = JsonSerializer.Serialize(quote.Tried);
        record.PricedAt = now;
        record.UpdatedAt = now;
    }

    private static string Describe(QuoteLine line) =>
        line.Kind == QuoteLine.Storage && line.ServiceFrom is { } from && line.ServiceTo is { } to
            ? $"{line.ChargeName} {from:dd/MM} - {to:dd/MM} ({line.Quantity:0} chargeable days)"
            : line.Kind == QuoteLine.Reefer
                ? $"{line.ChargeName} ({line.Quantity:0} h plugged in)"
                : line.ChargeName;
}
