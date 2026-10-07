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
/// A charge added by hand on the booking statement (Vector's "add charge" / "apply to all
/// containers", owner 2026-10-07): billing.charge source MANUAL, status QUOTED, at Vector's
/// price (original rate + discount), with the reason it was added. It is collected like any
/// line of its move: CASH at the cash window with the box's move (CashQuoter), CREDIT billed
/// at the gate when the box moves. A booking-level line (no box) goes on a credit invoice.
///   POST /charges/manual   one line per box asked for (or one for the booking); a box that
///                          already carries the charge on that move is skipped
///   POST /charges/bulk     the same to every box asked for (every current box when none):
///                          ADD adds where missing, UPDATE reprices the QUOTED ones present
/// </summary>
internal static class ManualChargeEndpoints
{
    public static RouteGroupBuilder MapManualChargeEndpoints(this RouteGroupBuilder revenue)
    {
        var charges = revenue.MapGroup("/charges").WithTags("Revenue — charges");
        charges.MapPost("/manual", AddAsync).RequireBranchPermission(RevenuePermissions.ChargeOverride)
            .WithSummary("Add a charge by hand: one QUOTED line per box (or one for the booking), at original rate + discount");
        charges.MapPost("/bulk", BulkAsync).RequireBranchPermission(RevenuePermissions.ChargeOverride)
            .WithSummary("Add (or reprice) a charge on every box of a booking: ADD skips boxes that carry it, UPDATE reprices them");
        return revenue;
    }

    private static async Task<IResult> AddAsync(
        ManualChargeRequest request, RevenueDbContext db, IMasterDataReferences master, ICallerPermissions scope,
        ITenantContext caller, TimeProvider clock, CancellationToken ct)
    {
        var (prepared, refused) = await PrepareAsync(request, everyBoxWhenNone: false, db, master, scope, ct);
        if (refused is not null) return refused;
        var p = prepared!;
        var created = new List<Charge>();
        foreach (var box in p.Boxes.DefaultIfEmpty())
        {
            if (Existing(p, box) is not null) continue;
            created.Add(New(p, box, caller.UserId(), clock.GetUtcNow()));
        }
        db.Charges.AddRange(created);
        await db.SaveChangesAsync(ct);
        var list = new List<ChargeResponse>();
        foreach (var c in created) list.Add(await ChargeEndpoints.OneAsync(db, c, master, ct));
        return TypedResults.Ok(list);
    }

    private static async Task<IResult> BulkAsync(
        ManualChargeRequest request, RevenueDbContext db, IMasterDataReferences master, ICallerPermissions scope,
        ITenantContext caller, TimeProvider clock, CancellationToken ct)
    {
        var mode = request.Mode?.Trim().ToUpperInvariant();
        if (mode is not ("ADD" or "UPDATE")) return RevenueSupport.Invalid("mode", "ADD or UPDATE.");
        var (prepared, refused) = await PrepareAsync(request, everyBoxWhenNone: true, db, master, scope, ct);
        if (refused is not null) return refused;
        var p = prepared!;
        var by = caller.UserId();
        var now = clock.GetUtcNow();
        int added = 0, updated = 0, skipped = 0;
        foreach (var box in p.Boxes)
        {
            var present = Existing(p, box);
            if (mode == "ADD")
            {
                if (present is not null) { skipped++; continue; }
                db.Charges.Add(New(p, box, by, now));
                added++;
            }
            else if (present is { Status: ChargeStatus.Quoted })
            {
                Price(present, p, by, now);
                updated++;
            }
            else skipped++;
        }
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new BulkChargeResponse(added, updated, skipped));
    }

    private sealed record Prepared(BookingPlan Plan, List<BookingPlanContainer> Boxes, ChargeVariantRef Variant, string? MovementCode,
        decimal Quantity, decimal Original, string DiscountType, decimal? Discount, decimal Selling, string Reason, List<Charge> Lines);

    private static async Task<(Prepared?, IResult?)> PrepareAsync(ManualChargeRequest r, bool everyBoxWhenNone, RevenueDbContext db,
        IMasterDataReferences master, ICallerPermissions scope, CancellationToken ct)
    {
        var errors = new Dictionary<string, List<string>>();
        var order = r.OrderNo?.Trim();
        var code = r.ChargeCode?.Trim().ToUpperInvariant();
        var billTo = (r.BillTo ?? "CUSTOMER").Trim().ToUpperInvariant();
        var term = (r.PaymentTermCode ?? "").Trim().ToUpperInvariant();
        var movement = string.IsNullOrWhiteSpace(r.MovementCode) ? null : r.MovementCode.Trim().ToUpperInvariant();
        var reason = r.Remarks?.Trim() ?? "";
        var type = (r.DiscountType ?? "NONE").Trim().ToUpperInvariant();
        var discount = r.DiscountRate ?? 0m;
        if (string.IsNullOrEmpty(order)) errors.Add("orderNo", "Which booking? Its order number.");
        if (string.IsNullOrEmpty(code)) errors.Add("chargeCode", "Which charge?");
        if (term is not ("CASH" or "CREDIT")) errors.Add("paymentTermCode", "CASH or CREDIT.");
        if ((r.Quantity ?? 1) <= 0) errors.Add("quantity", "More than 0.");
        if (r.OriginalRate is not { } original || original < 0) errors.Add("originalRate", "A rate of 0 or more.");
        if (!ChargeEndpoints.DiscountTypes.Contains(type)) errors.Add("discountType", "NONE, AMT or PCT.");
        else if (discount < 0 || (type == "PCT" && discount > 100) || (type == "NONE" && discount != 0))
            errors.Add("discountRate", "0 or more; a PCT at most 100; none without a discount type.");
        if (reason.Length == 0) errors.Add("remarks", "Say why the charge is added — a line without a reason is indistinguishable from a mistake.");
        else if (reason.Length > 500) errors.Add("remarks", "At most 500 characters.");
        if (errors.Count > 0) return (null, RevenueSupport.Invalid(errors));

        var plan = await db.BookingPlans.AsNoTracking().SingleOrDefaultAsync(b => b.OrderNo == order, ct);
        if (plan is null || !scope.HasAt(RevenuePermissions.ChargeOverride, plan.BranchId))
            return (null, TypedResults.NotFound(new ProblemDetails { Title = $"'{order}' is not a booking in your branches." }));
        if (plan.Status != "OPEN") return (null, RevenueSupport.Conflict($"{plan.OrderNo} is {plan.Status}: no charge is added to a closed booking."));

        var variant = (await master.ChargeVariantsAsync([code!], ct))
            .FirstOrDefault(v => v.BillTo == billTo && v.PaymentTermCode == term);
        if (variant is null) return (null, RevenueSupport.Invalid("chargeCode", $"{code} has no {billTo}/{term} variant in master data."));

        var asked = (r.BookingContainerIds ?? []).Distinct().ToList();
        var current = await db.BookingPlanContainers.AsNoTracking()
            .Where(b => b.BookingId == plan.BookingId && (asked.Count > 0 ? asked.Contains(b.BookingContainerId) : b.IsCurrent && b.EndReason == null))
            .ToListAsync(ct);
        if (asked.Count > 0 && current.Count != asked.Count)
            return (null, RevenueSupport.Invalid("bookingContainerIds", "A box that is not on this booking."));
        var boxes = asked.Count > 0 || everyBoxWhenNone ? current : [];
        if (boxes.Count > 0 && movement is null) return (null, RevenueSupport.Invalid("movementCode", "Which move of the box carries it?"));
        if (boxes.Count == 0 && term == "CASH")
            return (null, RevenueSupport.Invalid("paymentTermCode", "A booking-level line is billed on an invoice, which is CREDIT only. Add a cash line to a box."));

        var lines = await db.Charges.Where(c => c.BookingId == plan.BookingId && c.ChargeCode == code && c.BillTo == billTo
                                                && c.PaymentTermCode == term && c.Status != ChargeStatus.Cancelled).ToListAsync(ct);
        var selling = ChargeEndpoints.SellingRate(r.OriginalRate!.Value, type, discount);
        return (new Prepared(plan, boxes, variant, movement, r.Quantity ?? 1, r.OriginalRate.Value, type, type == "NONE" ? null : discount,
            selling, reason, lines), null);
    }

    private static Charge? Existing(Prepared p, BookingPlanContainer? box) =>
        p.Lines.FirstOrDefault(c => c.BookingContainerId == box?.BookingContainerId && c.MovementCode == p.MovementCode);

    private static Charge New(Prepared p, BookingPlanContainer? box, Guid by, DateTimeOffset now)
    {
        var c = new Charge
        {
            ChargeId = Guid.CreateVersion7(), TenantId = p.Plan.TenantId, BranchId = p.Plan.BranchId, Source = ChargeSource.Manual,
            BookingId = p.Plan.BookingId, OrderNo = p.Plan.OrderNo, BookingContainerId = box?.BookingContainerId, ContainerNo = box?.ContainerNo,
            MovementCode = p.MovementCode, ChargeCodeId = p.Variant.ChargeCodeId, ChargeCode = p.Variant.ChargeCode, ChargeName = p.Variant.DescriptionEn,
            BillTo = p.Variant.BillTo, PaymentTermCode = p.Variant.PaymentTermCode, PayerPartyCode = CashQuoter.PayerFor(p.Plan, p.Variant.BillTo),
            Quantity = p.Quantity, CurrencyCode = "THB", TaxCode = p.Variant.TaxCode, TaxRate = p.Variant.TaxRatePct,
            BillingUnitCode = p.Variant.BillingUnitCode, Status = ChargeStatus.Quoted, CreatedAt = now, CreatedBy = by,
        };
        Price(c, p, by, now);
        return c;
    }

    /// <summary>Vector's price on the line, with the reason as its audit trail.</summary>
    private static void Price(Charge c, Prepared p, Guid by, DateTimeOffset now)
    {
        c.UnitRateOriginal = p.Original;
        c.DiscountType = p.DiscountType;
        c.DiscountRate = p.Discount;
        c.UnitRate = p.Selling;
        c.Amount = CashQuoter.Money(c.Quantity * p.Selling);
        c.TaxAmount = CashQuoter.Money(c.Amount * c.TaxRate / 100m);
        c.IsRateOverridden = true;
        c.OverrideReason = p.Reason;
        c.OverriddenBy = by;
        c.OverriddenAt = now;
        c.UpdatedAt = now;
    }
}

/// <summary><c>BookingContainerIds</c> empty: the booking itself (manual) / every current box (bulk). <c>Mode</c>: bulk only.</summary>
public sealed record ManualChargeRequest(
    string? OrderNo, IReadOnlyList<Guid>? BookingContainerIds, string? ChargeCode, string? MovementCode, string? BillTo,
    string? PaymentTermCode, decimal? Quantity, decimal? OriginalRate, string? DiscountType, decimal? DiscountRate, string? Remarks,
    string? Mode = null);

public sealed record BulkChargeResponse(int Added, int Updated, int Skipped);
