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

        // gecko_revenue 25: a supervisor corrects ONE quoted line, by id, with a reason.
        charges.MapPost("/{chargeId:guid}/price", PriceAsync).RequireBranchPermission(RevenuePermissions.ChargeOverride)
            .WithSummary("Price one QUOTED line Vector's way: original rate + discount (NONE/AMT/PCT) -> selling rate; amount = quantity x rate, VAT from its tax code");
        charges.MapDelete("/{chargeId:guid}/price", UnpriceAsync).RequireBranchPermission(RevenuePermissions.ChargeOverride)
            .WithSummary("Put one corrected QUOTED line back on its tariff rate (the correction stays in the history)");
        charges.MapPost("/{chargeId:guid}/lock", LockAsync).RequireBranchPermission(RevenuePermissions.ChargeOverride)
            .WithSummary("Lock (or unlock) one QUOTED line: a locked line survives Regenerate");
        charges.MapPost("/regenerate", RegenerateAsync).RequireBranchPermission(RevenuePermissions.ChargeOverride)
            .WithSummary("Re-price a booking's QUOTED lines from the tariff in force now; locked and hand-added lines are kept");
        charges.MapPost("/waive", WaiveManyAsync).RequireBranchPermission(RevenuePermissions.ChargeWaive)
            .WithSummary("Waive several QUOTED lines by id, all or nothing, with a reason code");
        charges.MapPost("/{chargeId:guid}/waive", WaiveAsync).RequireBranchPermission(RevenuePermissions.ChargeWaive)
            .WithSummary("Waive one QUOTED line by id, with a reason; if nothing is left to pay on the box's next move, it is released");
        charges.MapDelete("/{chargeId:guid}/waive", UnwaiveAsync).RequireBranchPermission(RevenuePermissions.ChargeWaive)
            .WithSummary("Undo a waive made by id: the line is QUOTED again and a waiver coupon is withdrawn — only before the box moves");

        return revenue;
    }

    // ── editing one quoted line (gecko_revenue 25, owner 2026-10-06) ───────────
    //
    // QUOTED lines only: paid is corrected by voiding the receipt, invoiced by a credit
    // note — money taken and the charge behind it must never disagree. The rate is
    // edited, never the amount, so the line always multiplies out. An override sticks:
    // CashQuoter keeps it whenever the box is priced again (window, gate, statement).

    public static readonly string[] DiscountTypes = ["NONE", "AMT", "PCT"];
    public static readonly string[] WaiveReasonCodes = ["GOODWILL", "RATE_ERROR", "DISPUTE_RESOLUTION", "MANAGEMENT_APPROVAL", "SYSTEM_ERROR"];

    /// <summary>Vector's selling rate: AMT max(0, original - d); PCT max(0, original x (1 - d/100)); NONE the original — 2 places.</summary>
    public static decimal SellingRate(decimal original, string discountType, decimal discount) => decimal.Round(discountType switch
    {
        "AMT" => Math.Max(0m, original - discount),
        "PCT" => Math.Max(0m, original * (1m - discount / 100m)),
        _ => original,
    }, 2, MidpointRounding.AwayFromZero);

    private static async Task<Results<Ok<ChargeResponse>, NotFound<ProblemDetails>, ValidationProblem, ProblemHttpResult>> PriceAsync(
        Guid chargeId, PriceChargeRequest request, RevenueDbContext db, IMasterDataReferences master, ICallerPermissions scope,
        ITenantContext caller, TimeProvider clock, CancellationToken ct)
    {
        var charge = await db.Charges.SingleOrDefaultAsync(c => c.ChargeId == chargeId, ct);
        if (charge is null || !scope.HasAt(RevenuePermissions.ChargeOverride, charge.BranchId)) return NoSuchCharge();

        var errors = new Dictionary<string, List<string>>();
        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length == 0) errors.Add("reason", "Say why — a price changed without a reason is indistinguishable from a mistake.");
        else if (reason.Length > 500) errors.Add("reason", "At most 500 characters.");
        if (request.OriginalRate is not { } original || original < 0 || original > 99_999_999m)
            errors.Add("originalRate", "A rate of 0 or more. Zero means it costs nothing; to forgive a line, waive it.");
        var type = (request.DiscountType ?? "NONE").Trim().ToUpperInvariant();
        if (!DiscountTypes.Contains(type)) errors.Add("discountType", "NONE, AMT or PCT.");
        var discount = request.DiscountRate ?? 0m;
        if (discount < 0) errors.Add("discountRate", "0 or more.");
        else if (type == "PCT" && discount > 100) errors.Add("discountRate", "A percentage is at most 100.");
        else if (type == "NONE" && discount != 0) errors.Add("discountRate", "No discount type, no discount.");
        if (errors.Count > 0) return RevenueSupport.Invalid(errors);
        if (charge.Status != ChargeStatus.Quoted) return NotQuoted(charge, "repriced");
        if (!db.TrySetExpectedVersion(charge, request.RowVersion))
            return RevenueSupport.Invalid("rowVersion", "Send the rowVersion you received with the line.");

        charge.UnitRateOriginal = request.OriginalRate;
        charge.DiscountType = type;
        charge.DiscountRate = type == "NONE" ? null : discount;
        charge.UnitRate = SellingRate(request.OriginalRate!.Value, type, discount);
        Reprice(charge);
        charge.IsRateOverridden = true;
        charge.OverrideReason = reason;
        charge.OverriddenBy = caller.UserId();
        charge.OverriddenAt = clock.GetUtcNow();
        charge.UpdatedAt = charge.OverriddenAt.Value;

        if (await SaveOrStaleAsync(db, ct) is { } stale) return stale;
        return TypedResults.Ok(await OneAsync(db, charge, master, ct));
    }

    private static async Task<Results<Ok<ChargeResponse>, NotFound<ProblemDetails>, ValidationProblem, ProblemHttpResult>> UnpriceAsync(
        Guid chargeId, string? rowVersion, RevenueDbContext db, IMasterDataReferences master, ICallerPermissions scope,
        TimeProvider clock, CancellationToken ct)
    {
        var charge = await db.Charges.SingleOrDefaultAsync(c => c.ChargeId == chargeId, ct);
        if (charge is null || !scope.HasAt(RevenuePermissions.ChargeOverride, charge.BranchId)) return NoSuchCharge();
        if (charge.Status != ChargeStatus.Quoted) return NotQuoted(charge, "repriced");
        if (!charge.IsRateOverridden) return RevenueSupport.Conflict("This line is on its tariff rate; there is no corrected price to undo.");
        if (!db.TrySetExpectedVersion(charge, rowVersion))
            return RevenueSupport.Invalid("rowVersion", "Send the rowVersion you received with the line.");

        // Back on the tariff's rate (the price snapshot the quote was made from). The correction
        // itself is kept in the row's system-versioned history.
        charge.UnitRate = TariffRate(charge);
        Reprice(charge);
        charge.IsRateOverridden = false;
        charge.UnitRateOriginal = null;
        charge.DiscountType = null;
        charge.DiscountRate = null;
        charge.OverrideReason = null;
        charge.OverriddenBy = null;
        charge.OverriddenAt = null;
        charge.UpdatedAt = clock.GetUtcNow();

        if (await SaveOrStaleAsync(db, ct) is { } stale) return stale;
        return TypedResults.Ok(await OneAsync(db, charge, master, ct));
    }

    private static async Task<Results<Ok<ChargeResponse>, NotFound<ProblemDetails>, ValidationProblem, ProblemHttpResult>> LockAsync(
        Guid chargeId, LockChargeRequest request, RevenueDbContext db, IMasterDataReferences master, ICallerPermissions scope,
        TimeProvider clock, CancellationToken ct)
    {
        var charge = await db.Charges.SingleOrDefaultAsync(c => c.ChargeId == chargeId, ct);
        if (charge is null || !scope.HasAt(RevenuePermissions.ChargeOverride, charge.BranchId)) return NoSuchCharge();
        if (request.IsLocked is not { } locked) return RevenueSupport.Invalid("isLocked", "true to keep this line through Regenerate, false to let it be re-priced.");
        if (charge.Status != ChargeStatus.Quoted) return NotQuoted(charge, locked ? "locked" : "unlocked");
        if (!db.TrySetExpectedVersion(charge, request.RowVersion))
            return RevenueSupport.Invalid("rowVersion", "Send the rowVersion you received with the line.");
        charge.IsLocked = locked;
        charge.UpdatedAt = clock.GetUtcNow();
        if (await SaveOrStaleAsync(db, ct) is { } stale) return stale;
        return TypedResults.Ok(await OneAsync(db, charge, master, ct));
    }

    private static async Task<Results<Ok<ChargeResponse>, NotFound<ProblemDetails>, ValidationProblem, ProblemHttpResult>> WaiveAsync(
        Guid chargeId, WaiveChargeRequest request, RevenueDbContext db, IMasterDataReferences master, ICallerPermissions scope,
        Window.WindowService window, ITenantContext caller, CancellationToken ct)
    {
        var charge = await db.Charges.SingleOrDefaultAsync(c => c.ChargeId == chargeId, ct);
        if (charge is null || !scope.HasAt(RevenuePermissions.ChargeWaive, charge.BranchId)) return NoSuchCharge();
        if (WaiveReasonProblem(request.ReasonCode, request.Reason, out var code, out var reason) is { } bad) return bad;
        if (charge.Status != ChargeStatus.Quoted) return NotQuoted(charge, "waived");
        if (!db.TrySetExpectedVersion(charge, request.RowVersion))
            return RevenueSupport.Invalid("rowVersion", "Send the rowVersion you received with the line.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await WaiveOneAsync(db, window, charge, code, reason, caller.UserId(), ct) is { } stale) return stale;
        await transaction.CommitAsync(ct);
        return TypedResults.Ok(await OneAsync(db, charge, master, ct));
    }

    /// <summary>All or nothing: every line must be QUOTED and in the caller's branches, or nothing is waived.</summary>
    private static async Task<Results<Ok<BulkWaiveResponse>, NotFound<ProblemDetails>, ValidationProblem, ProblemHttpResult>> WaiveManyAsync(
        BulkWaiveRequest request, RevenueDbContext db, ICallerPermissions scope, Window.WindowService window, ITenantContext caller, CancellationToken ct)
    {
        var ids = (request.ChargeIds ?? []).Distinct().ToList();
        if (ids.Count == 0) return RevenueSupport.Invalid("chargeIds", "Which lines? At least one.");
        if (ids.Count > 500) return RevenueSupport.Invalid("chargeIds", "At most 500 lines at once.");
        if (WaiveReasonProblem(request.ReasonCode, request.Reason, out var code, out var reason) is { } bad) return bad;

        var charges = await db.Charges.Where(c => ids.Contains(c.ChargeId)).ToListAsync(ct);
        if (charges.Count != ids.Count || charges.Any(c => !scope.HasAt(RevenuePermissions.ChargeWaive, c.BranchId)))
            return TypedResults.NotFound(new ProblemDetails { Title = "Some of these lines are not charges in your branches.", Detail = "Nothing was waived." });
        if (charges.Where(c => c.Status != ChargeStatus.Quoted).ToList() is { Count: > 0 } settled)
            return RevenueSupport.Conflict($"{settled.Count} of these lines are not QUOTED: nothing was waived.",
                string.Join("; ", settled.Select(c => $"{c.ChargeCode} {c.ContainerNo} {c.MovementCode}: {c.Status}")));

        var by = caller.UserId();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var charge in charges)
            if (await WaiveOneAsync(db, window, charge, code, reason, by, ct) is { } stale) return stale;   // rolled back with the transaction
        await transaction.CommitAsync(ct);
        return TypedResults.Ok(new BulkWaiveResponse(charges.Count));
    }

    private static async Task<ProblemHttpResult?> WaiveOneAsync(RevenueDbContext db, Window.WindowService window, Charge charge,
        string code, string reason, Guid by, CancellationToken ct)
    {
        var plan = await db.BookingPlans.AsNoTracking().SingleOrDefaultAsync(p => p.BookingId == charge.BookingId, ct);
        var context = plan is null ? null : await window.ContextAsync(plan, ct);
        charge.Status = ChargeStatus.Waived;
        charge.WaivedAt = context?.Now ?? DateTimeOffset.UtcNow;
        charge.WaivedBy = by;
        charge.WaiveReasonCode = code;
        charge.WaiveReason = reason;
        charge.UpdatedAt = charge.WaivedAt.Value;
        if (await SaveOrStaleAsync(db, ct) is { } stale) return stale;
        // Nothing left to pay on the box's next move → it goes on the waiver, as at the window.
        if (context is not null) await window.ReleaseIfNothingDueAsync(context, charge, by, ct);
        return null;
    }

    private static ValidationProblem? WaiveReasonProblem(string? reasonCode, string? note, out string code, out string reason)
    {
        code = reasonCode?.Trim().ToUpperInvariant() ?? "";
        reason = note?.Trim() ?? "";
        var errors = new Dictionary<string, List<string>>();
        if (!WaiveReasonCodes.Contains(code)) errors.Add("reasonCode", $"One of {string.Join(", ", WaiveReasonCodes)}.");
        if (reason.Length == 0) reason = code;   // the code is the reason; the note is optional detail
        if (reason.Length > 300) errors.Add("reason", "At most 300 characters.");
        return errors.Count > 0 ? RevenueSupport.Invalid(errors) : null;
    }

    private static async Task<Results<Ok<ChargeResponse>, NotFound<ProblemDetails>, ValidationProblem, ProblemHttpResult>> UnwaiveAsync(
        Guid chargeId, string? rowVersion, RevenueDbContext db, IMasterDataReferences master, ICallerPermissions scope,
        ITenantContext caller, TimeProvider clock, CancellationToken ct)
    {
        var charge = await db.Charges.SingleOrDefaultAsync(c => c.ChargeId == chargeId, ct);
        if (charge is null || !scope.HasAt(RevenuePermissions.ChargeWaive, charge.BranchId)) return NoSuchCharge();
        if (charge.Status != ChargeStatus.Waived)
            return RevenueSupport.Conflict($"This line is {charge.Status}, not WAIVED: there is no waive to undo.");
        if (charge.Source == ChargeSource.Window)
            return RevenueSupport.Conflict("This line was waived at the cash window, not on the statement; that waive has no undo.",
                "Only a waive made on the booking statement (by line) can be undone here.");
        if (charge.BookingContainerId is { } boxId
            && await db.BookingPlanContainers.AsNoTracking().SingleOrDefaultAsync(b => b.BookingContainerId == boxId, ct) is { } box
            && !CashQuoter.Steps(box).Any(s => s.MovementCode == charge.MovementCode && s.Status == "PENDING"))
            return RevenueSupport.Conflict($"The box has already made its {charge.MovementCode} move: the waive stands.",
                "A move that happened on a waiver is corrected by charging it separately, not by undoing the waive.");
        if (!db.TrySetExpectedVersion(charge, rowVersion))
            return RevenueSupport.Invalid("rowVersion", "Send the rowVersion you received with the line.");

        var now = clock.GetUtcNow();
        var by = caller.UserId();
        var coupon = charge.CouponRef;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        charge.Status = ChargeStatus.Quoted;
        charge.WaivedAt = null;
        charge.WaivedBy = null;
        charge.WaiveReason = null;
        charge.WaiveReasonCode = null;
        charge.CouponRef = null;
        charge.UpdatedAt = now;
        if (await SaveOrStaleAsync(db, ct) is { } stale) return stale;
        // The waiver coupon it released is taken back at the barrier: the line is due again.
        if (coupon is not null)
            await RevenueOutbox.EnqueueAsync(db, charge.TenantId, "CHARGE", charge.ChargeId, RevenueOutbox.CouponRevoked,
                new CouponRevokedPayload(null, coupon, by, $"Waive of {charge.ChargeCode} on {charge.ContainerNo} undone"), ct);
        await transaction.CommitAsync(ct);

        return TypedResults.Ok(await OneAsync(db, charge, master, ct));
    }

    /// <summary>
    /// Vector's Regenerate (usp_RegenerateCostSheet): every QUOTED line of the booking is priced again
    /// from the tariff in force now. A correction that is not locked goes back to the tariff (the only
    /// thing that undoes one — owner 2026-10-07); a locked line keeps its rate; a hand-added line is kept
    /// unless keepManual is false. Paid, earned, unbilled, invoiced, waived and cancelled lines are never touched.
    /// </summary>
    private static async Task<Results<Ok<RegenerateResponse>, NotFound<ProblemDetails>, ValidationProblem, ProblemHttpResult>> RegenerateAsync(
        RegenerateRequest request, RevenueDbContext db, AutomaticCoupons coupons, BranchCalendar calendar, IMasterDataReferences master,
        ICallerPermissions scope, CancellationToken ct)
    {
        var order = request.OrderNo?.Trim();
        var reason = request.Reason?.Trim() ?? "";
        if (string.IsNullOrEmpty(order)) return RevenueSupport.Invalid("orderNo", "Which booking? Its order number.");
        if (reason.Length == 0) return RevenueSupport.Invalid("reason", "Say why the booking is re-priced.");
        if (reason.Length > 500) return RevenueSupport.Invalid("reason", "At most 500 characters.");

        var plan = await db.BookingPlans.AsNoTracking().SingleOrDefaultAsync(p => p.OrderNo == order, ct);
        if (plan is null || !scope.HasAt(RevenuePermissions.ChargeOverride, plan.BranchId))
            return TypedResults.NotFound(new ProblemDetails { Title = $"'{order}' is not a booking in your branches." });
        if (plan.Status != "OPEN") return RevenueSupport.Conflict($"{plan.OrderNo} is {plan.Status}: a closed booking is not re-priced.");
        var branch = await calendar.BranchAsync(plan.BranchId, ct);
        if (branch is null) return RevenueSupport.Conflict("This booking's branch has no master-data profile.");
        var orderType = (await master.OrderTypePlansAsync([plan.OrderTypeCode], ct)).GetValueOrDefault(plan.OrderTypeCode);

        var keepLocked = request.KeepLocked ?? true;
        var keepManual = request.KeepManual ?? true;
        var now = calendar.Now;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var quoted = await db.Charges.Where(c => c.BookingId == plan.BookingId && c.Status == ChargeStatus.Quoted).ToListAsync(ct);
        var removedManual = 0;
        foreach (var c in quoted)
        {
            if (!keepLocked) c.IsLocked = false;
            if (c.Source == ChargeSource.Manual)
            {
                if (keepManual) continue;
                c.Status = ChargeStatus.Cancelled; c.CancelledAt = now; c.CancelReason = $"Regenerated: {reason}"; c.UpdatedAt = now;
                removedManual++;
                continue;
            }
            if (c.IsLocked || !c.IsRateOverridden) continue;
            // an unlocked correction goes back to the tariff: the refresh below prices it afresh
            c.IsRateOverridden = false; c.UnitRateOriginal = null; c.DiscountType = null; c.DiscountRate = null;
            c.OverrideReason = null; c.OverriddenBy = null; c.OverriddenAt = null; c.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        var refreshed = await coupons.RefreshQuotesAsync(plan, branch, orderType, now, ct, $"Regenerated: {reason}");
        await transaction.CommitAsync(ct);

        return TypedResults.Ok(new RegenerateResponse(refreshed.Repriced, refreshed.Added, refreshed.Removed + removedManual,
            quoted.Count(c => c.IsLocked && c.Status == ChargeStatus.Quoted), refreshed.NoRate));
    }

    /// <summary>The tariff's rate the quote was made from (its price snapshot); null when no tariff priced it.</summary>
    private static decimal? TariffRate(Charge charge)
    {
        if (string.IsNullOrEmpty(charge.PriceSnapshotJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(charge.PriceSnapshotJson);
            return doc.RootElement.TryGetProperty("UnitRate", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Number ? r.GetDecimal() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>amount = quantity x unitRate, rounded to satang once; VAT on top at the line's own rate.</summary>
    private static void Reprice(Charge charge)
    {
        charge.Amount = charge.UnitRate is { } rate ? CashQuoter.Money(charge.Quantity * rate) : 0m;
        charge.TaxAmount = CashQuoter.Money(charge.Amount * charge.TaxRate / 100m);
    }

    private static NotFound<ProblemDetails> NoSuchCharge() =>
        TypedResults.NotFound(new ProblemDetails { Title = "No such charge in your branches." });

    /// <summary>The 409 that names the status and says what corrects such a line instead.</summary>
    private static ProblemHttpResult NotQuoted(Charge charge, string verb) => RevenueSupport.Conflict(charge.Status switch
    {
        ChargeStatus.Paid or ChargeStatus.Earned => $"{charge.Status} charges cannot be {verb}. Void the receipt first.",
        ChargeStatus.Invoiced => $"INVOICED charges cannot be {verb}. Issue a credit note.",
        ChargeStatus.Unbilled => $"UNBILLED charges cannot be {verb}. Correct it on the invoice that bills it.",
        ChargeStatus.Waived => $"WAIVED charges cannot be {verb}. Undo the waive first.",
        _ => $"{charge.Status} charges cannot be {verb}. The line is no longer due.",
    });

    private static async Task<ProblemHttpResult?> SaveOrStaleAsync(RevenueDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return RevenueSupport.Conflict("The charge changed since you loaded it.",
                "Re-read the statement and re-apply your change. The rowVersion you sent is no longer current.");
        }
    }

    internal static async Task<ChargeResponse> OneAsync(RevenueDbContext db, Charge charge, IMasterDataReferences master, CancellationToken ct)
    {
        var names = await PayerNamesAsync(master, [charge.PayerPartyCode], ct);
        var types = await ChargeTypesAsync(master, [charge.ChargeCode], ct);
        return ToResponse(charge, names, types);
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
        var types = await ChargeTypesAsync(master, page.Items.Select(c => c.ChargeCode), ct);
        return TypedResults.Ok(new PagedResult<ChargeResponse>(
            page.Items.Select(c => ToResponse(c, names, types)).ToList(), page.Page, page.PageSize, page.TotalCount));
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
        var settledKeys = lines.Where(c => c.Source is not (ChargeSource.Quote or ChargeSource.Manual) && c.Status != ChargeStatus.Cancelled)
            .Select(c => (c.BookingContainerId, c.MovementCode, c.ChargeCode, c.BillTo)).ToHashSet();
        // ... and a hand-added line once its move has been paid at the window or billed at the gate.
        lines = lines.Where(c => !(c.Source is ChargeSource.Quote or ChargeSource.Manual && c.Status == ChargeStatus.Quoted)
                                 || !settledKeys.Contains((c.BookingContainerId, c.MovementCode, c.ChargeCode, c.BillTo))).ToList();
        var receipts = await db.Receipts.AsNoTracking().Where(r => r.BookingId == plan.BookingId)
            .OrderBy(r => r.ReceiptAt).ToListAsync(ct);
        var receiptNo = receipts.ToDictionary(r => r.ReceiptId, r => r.ReceiptNo);

        var names = await PayerNamesAsync(master, lines.Select(c => c.PayerPartyCode).Append(plan.CustomerPartyCode), ct);
        var types = await ChargeTypesAsync(master, lines.Select(c => c.ChargeCode), ct);

        StatementTotals Totals(IEnumerable<Charge> set)
        {
            var list = set.ToList();
            decimal Sum(params string[] statuses) => list.Where(c => statuses.Contains(c.Status)).Sum(c => c.Amount + c.TaxAmount);
            var quoted = list.Where(c => c.Status == ChargeStatus.Quoted).ToList();
            return new StatementTotals(Sum("PAID", "EARNED"), Sum("WAIVED"), Sum("UNBILLED"), Sum("INVOICED"), Sum("CANCELLED"),
                quoted.Where(c => c.PaymentTermCode == "CASH").Sum(c => c.Amount + c.TaxAmount),
                quoted.Where(c => c.PaymentTermCode != "CASH").Sum(c => c.Amount + c.TaxAmount),
                quoted.Count(c => c.ScheduleId is null && !c.IsRateOverridden));   // a supervisor's rate is a price
        }

        // Vector's SZ / TYPE column: the box's equipment type and its size from MDM.
        var boxType = boxes.ToDictionary(b => b.BookingContainerId, b => b.EquipmentTypeCode);
        var sizes = await master.EquipmentTypesAsync(boxes.Select(b => b.EquipmentTypeCode).OfType<string>().Distinct().ToList(), ct);
        StatementLineResponse Line(Charge c)
        {
            var type = c.BookingContainerId is { } id ? boxType.GetValueOrDefault(id) : null;
            return new(ToResponse(c, names, types) with
                { EquipmentTypeCode = type, EquipmentSize = type is null ? null : sizes.GetValueOrDefault(type)?.SizeCode },
            c.ReceiptId is { } rid ? receiptNo.GetValueOrDefault(rid) : null);
        }

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

    /// <summary>Charge type and category per code, from MDM (the statement filters on them).</summary>
    private static async Task<IReadOnlyDictionary<string, (string Type, string Category)>> ChargeTypesAsync(
        IMasterDataReferences master, IEnumerable<string> codes, CancellationToken ct)
    {
        var distinct = codes.Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
        if (distinct.Count == 0) return new Dictionary<string, (string, string)>();
        return (await master.ChargeVariantsAsync(distinct, ct))
            .GroupBy(v => v.ChargeCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (g.First().ChargeType, g.First().ChargeCategory), StringComparer.OrdinalIgnoreCase);
    }

    private static ChargeResponse ToResponse(Charge c, IReadOnlyDictionary<string, string> names,
        IReadOnlyDictionary<string, (string Type, string Category)>? types = null) => new(
        c.ChargeId, c.BranchId, c.Source, c.Status,
        c.BookingId, c.OrderNo, c.ContainerNo, c.MovementCode, c.GateTransactionId, c.EirNo,
        c.BillingPeriod, c.ServiceFrom, c.ServiceTo,
        c.ChargeCode, c.ChargeName, c.BillTo, c.PaymentTermCode,
        c.PayerPartyCode, c.PayerPartyCode is { } code ? names.GetValueOrDefault(code) : null,
        c.Quantity, c.UnitRate, c.Amount, c.TaxAmount, c.Amount + c.TaxAmount, c.CurrencyCode,
        c.PricedForDate, c.ScheduleNo, c.ScheduleVersionNo,
        c.ReceiptId, c.CouponRef, c.InvoiceId, c.EarnedAt, c.WaivedAt, c.WaiveReason, c.CancelledAt, c.CancelReason,
        c.CreditNoteRequired, c.CreatedAt,
        Convert.ToBase64String(c.RowVersion), c.IsRateOverridden, c.UnitRateOriginal, c.OverrideReason, c.OverriddenBy, c.OverriddenAt,
        types?.GetValueOrDefault(c.ChargeCode).Type, types?.GetValueOrDefault(c.ChargeCode).Category,
        c.DiscountType, c.DiscountRate, c.IsLocked, c.WaivedBy, c.WaiveReasonCode, c.BillingUnitCode);
}

/// <summary>Vector's price for one QUOTED line (gecko_revenue 25/26): original rate + discount. <c>Reason</c> is the audit trail.</summary>
public sealed record PriceChargeRequest(string? RowVersion, decimal? OriginalRate, string? DiscountType, decimal? DiscountRate, string? Reason);

/// <summary>Waive one QUOTED line by id: a reason code from the closed list, and an optional note.</summary>
public sealed record WaiveChargeRequest(string? RowVersion, string? ReasonCode, string? Reason);

public sealed record BulkWaiveRequest(IReadOnlyList<Guid>? ChargeIds, string? ReasonCode, string? Reason);

public sealed record BulkWaiveResponse(int Waived);

public sealed record LockChargeRequest(bool? IsLocked, string? RowVersion);

public sealed record RegenerateRequest(string? OrderNo, bool? KeepLocked, bool? KeepManual, string? Reason);

/// <summary><c>NoRate</c>: QUOTED lines the tariff still cannot price — those boxes will be held at the gate.</summary>
public sealed record RegenerateResponse(int Repriced, int Added, int Removed, int KeptLocked, int NoRate);

/// <summary>One priced line. <c>PayerName</c> is MDM's; null for a walk-in cash customer or a code MDM no longer knows.</summary>
/// <param name="RowVersion">Send it back with an override or a waive: a stale one is a 409.</param>
/// <param name="IsRateOverridden">The unit rate is a supervisor's (gecko_revenue 25), not the tariff's; re-pricing keeps it.</param>
/// <param name="UnitRateOriginal">With an override: the tariff's rate it replaced — what the line WOULD have cost.</param>
/// <param name="ChargeType">From the charge-code master (GATE, LIFT, STORAGE, ...); null when MDM no longer knows the code.</param>
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
    bool CreditNoteRequired, DateTimeOffset CreatedAt,
    string? RowVersion = null, bool IsRateOverridden = false, decimal? UnitRateOriginal = null, string? OverrideReason = null,
    Guid? OverriddenBy = null, DateTimeOffset? OverriddenAt = null, string? ChargeType = null, string? ChargeCategory = null,
    string? DiscountType = null, decimal? DiscountRate = null, bool IsLocked = false, Guid? WaivedBy = null, string? WaiveReasonCode = null,
    string? BillingUnitCode = null, string? EquipmentTypeCode = null, string? EquipmentSize = null)
{
    /// <summary>Vector's name for the rate a correction started from (gecko_revenue 25 <c>unit_rate_original</c>).</summary>
    public decimal? OriginalRate => UnitRateOriginal;
}

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
