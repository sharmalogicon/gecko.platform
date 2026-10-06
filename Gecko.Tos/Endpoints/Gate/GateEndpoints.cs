using System.Security.Cryptography;
using System.Text;
using Gecko.Data;
using Gecko.Identity.Contracts;
using Gecko.MasterData.Contracts;
using Gecko.SharedKernel;
using Gecko.Tos.Application;
using Gecko.Tos.Domain;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Infrastructure.Persistence;
using Gecko.Tos.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Endpoints.Gate;

/// <summary>
/// The barrier (PLAN §5.2–§5.5, Phase 5). Two endpoints carry the whole depot:
/// one that says whether a box may move, and one that records it moving.
///
/// THE RULE THAT SHAPES THIS FILE (§5.5): a gate event is ONE SQL TRANSACTION —
/// truck visit, EIR, seals, the step going DONE, the yard row opening or
/// closing, the visit journal, the assignment ending, the coupon being spent and
/// the outbox message. Vector wrote those from five places in application code,
/// which is why its four answers to "what is in the yard" disagree.
/// </summary>
internal static class GateEndpoints
{
    /// <summary>The MDM code list a truck's category is checked against (gecko_master 15).</summary>
    private const string TruckCategoryList = "TRUCK_CATEGORY";

    public static RouteGroupBuilder MapGateEndpoints(this RouteGroupBuilder tos)
    {
        var gate = tos.MapGroup("/gate").WithTags("TOS — gate");

        gate.MapGet("/preflight", PreflightAsync)
            .RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("Ask the barrier about a box before the boom lifts")
            .WithDescription("Five seeks, all local (ADR-007): the assignment, its next step, the holds, the yard and the cut-off. Returns ALLOWED, NEEDS_OVERRIDE or BLOCKED with a reason for each finding.");

        gate.MapPost("/transactions", RecordAsync)
            .RequireBranchPermission(TosPermissions.GateCreate)
            .Validate<GateTransactionRequest>()
            .WithSummary("Record a box crossing the barrier — the EIR")
            .WithDescription("One transaction: truck visit, EIR, seals, step DONE, yard row, visit event, assignment close, coupon, outbox.");

        gate.MapGet("/transactions", ListAsync).RequireBranchPermission(TosPermissions.GateView).WithSummary("The gate day");
        gate.MapGet("/transactions/{id:guid}/eir.pdf", EirPdfAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("The printed EIR (A4 PDF) — voided EIRs print too, marked VOID");
        gate.MapGet("/transactions/{id:guid}", GetAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithName("GetGateTransaction").WithSummary("One EIR with its seals");

        gate.MapPost("/transactions/{id:guid}/void", VoidAsync)
            .RequireBranchPermission(TosPermissions.GateOverride)
            .Validate<VoidGateTransactionRequest>()
            .WithSummary("Void an EIR and re-open the step it completed")
            .WithDescription("The number is kept and never reused (Q4). Reissue by recording the move again; the new EIR points back at this one.");

        // Owner 2026-10-04 (GATE_IN_VECTOR_PARITY_FOR_API §1–§2): raise, pay, then gate.
        gate.MapPost("/blind-orders", BookingEndpoints.RaiseBlindOrderAsync)
            .RequireBranchPermission(TosPermissions.GateCreate)
            .Validate<BlindOrderRequest>()
            .WithSummary("Raise a BLIND GATE IN order for a box that came with no paperwork")
            .WithDescription("One transaction: the booking, its line and the box. A retry for the same box answers 200 with the same order. Then price and pay at the window, then POST the gate move.");
        gate.MapGet("/bookable-boxes", BookableBoxesAsync)
            .RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("The boxes a gate clerk can pick, each with its next step")
            .WithDescription("Open bookings at the depot; filter by the next step's direction (IN/OUT) and full/empty, leave out the boxes already on this truck. Search matches the order no, B/L, customer ref or container no.");

        gate.MapGet("/vas", VasAsync)
            .RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("The gate VAS an order type offers (the clerk's VAS panel)")
            .WithDescription("Offered on an EMPTY drop-off or a pick-up. Tick them into the Save's vas; the window prices them.");

        gate.MapGet("/damage-codes", DamageCodesAsync)
            .RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("The CEDEX damage codes, locations and components (the clerk's damage panel)");

        gate.MapGet("/visits", ListVisitsAsync).RequireBranchPermission(TosPermissions.GateView).WithSummary("Trucks at the depot");
        gate.MapGet("/visits/{id:guid}", GetVisitAsync).RequireBranchPermission(TosPermissions.GateView).WithSummary("One truck visit and its boxes");
        gate.MapGet("/visits/{id:guid}/truck-in.pdf", TruckInPdfAsync).RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("The truck-in form: the truck, driver, haulier and every box with movement, seals and weights (Vector TMS_TruckInForm)");
        gate.MapPost("/visits/{id:guid}/depart", DepartAsync).RequireBranchPermission(TosPermissions.GateCreate)
            .WithSummary("The truck leaves — closes the visit and stops the dwell clock");

        // The stock list, from yard.vw_container_in_yard: gate-in time read from the
        // EIR, the hold flag from the hold rows, dwell computed. Nothing stored twice
        // (D-5) — Vector's four sources disagreed by three boxes.
        tos.MapGet("/yard/containers", InYardAsync)
            .RequireBranchPermission(TosPermissions.GateView)
            .WithSummary("What is in the yard right now");

        return tos;
    }

    // ── the box picker ──────────────────────────────────────────────────────

    /// <summary>
    /// Vector's gate booking search (GateIn.cs:555) as a grid of boxes: open bookings at this depot,
    /// each active box with its next step — the lowest PENDING step, the one the gate would complete.
    /// The direction / full-empty filters read that step's MDM rules; <paramref name="excludeBookingContainerIds"/>
    /// leaves out the boxes already on this truck.
    /// </summary>
    /// <summary>The damage panel's lists, for a clerk who cannot read MDM's equipment screens (A4).</summary>
    private static async Task<Ok<GateDamageCodesResponse>> DamageCodesAsync(IMasterDataReferences master, CancellationToken ct)
    {
        var codes = await master.SurveyCodesAsync(ct);
        return TypedResults.Ok(new GateDamageCodesResponse(
            codes.DamageCodes.Values.OrderBy(d => d.DamageCode)
                .Select(d => new GateDamageCodeResponse(d.DamageCode, d.DescriptionEn, d.Severity, d.MakesUnserviceable)).ToList(),
            codes.Locations.Order().ToList(),
            codes.Components.Order().ToList()));
    }

    /// <summary>
    /// GATE_IN_COMPLETION_PLAN A3: an order type's gate VAS (value-added, raised at the gate), for a clerk who has no
    /// commercial master-data permission. The same rule the window prices by (CashQuoter.OffersVas).
    /// </summary>
    private static async Task<Results<Ok<List<GateVasResponse>>, ValidationProblem>> VasAsync(
        IMasterDataReferences master, CancellationToken ct, string? orderTypeCode = null)
    {
        var code = orderTypeCode.Clean();
        if (code is null) return TosSupport.Invalid("orderTypeCode", "Which order type? The VAS come from the box's order.");
        var plan = (await master.OrderTypePlansAsync([code], ct)).GetValueOrDefault(code);
        if (plan is not { IsActive: true }) return TosSupport.Invalid("orderTypeCode", $"'{code}' is not an active order type.");

        var offered = plan.Steps.Where(s => s.Direction == GateRules.Out || (s.Direction == GateRules.In && s.FullEmpty == GateRules.Empty))
            .OrderBy(s => s.SequenceNo).Select(s => s.MovementCode).ToList();
        var vas = (await master.OrderTypeChargesAsync(code, ct)).Where(c => c.IsValueAddedService && c.RaiseAtGateIn).ToList();
        var names = (await master.ChargeVariantsAsync(vas.Select(v => v.ChargeCode).Distinct(), ct))
            .GroupBy(v => v.ChargeCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return TypedResults.Ok(vas
            .DistinctBy(v => (v.ChargeCode, v.BillTo, v.PaymentTermCode))
            .OrderBy(v => v.ChargeCode).ThenBy(v => v.BillTo).ThenBy(v => v.PaymentTermCode)
            .Select(v => new GateVasResponse(v.ChargeCode, names.GetValueOrDefault(v.ChargeCode)?.DescriptionEn ?? v.ChargeCode,
                names.GetValueOrDefault(v.ChargeCode)?.DescriptionLocal, v.BillTo, v.PaymentTermCode,
                "An EMPTY drop-off or a pick-up",
                v.MovementCode is null ? offered : offered.Where(m => string.Equals(m, v.MovementCode, StringComparison.OrdinalIgnoreCase)).ToList()))
            .ToList());
    }

    private static async Task<Results<Ok<PagedResult<BookableBoxResponse>>, ValidationProblem, ProblemHttpResult>> BookableBoxesAsync(
        [AsParameters] ListQuery query, TosDbContext db, IMasterDataReferences master, ICallerPermissions scope, TimeProvider time, CancellationToken ct,
        Guid? branchId = null, string? direction = null, string? fullEmpty = null, Guid[]? excludeBookingContainerIds = null, Guid? draftId = null)
    {
        if (branchId is null) return TosSupport.Invalid("branchId", "Which gate? A barrier belongs to a depot.");
        if (!scope.HasAt(TosPermissions.GateView, branchId.Value)) return TosScope.OutsideYourBranches("You cannot read that depot's gate.");
        var way = direction.Clean();
        if (way is not null && way is not (GateRules.In or GateRules.Out)) return TosSupport.Invalid("direction", "Use IN (a drop-off) or OUT (a pick-up).");
        var load = fullEmpty.Clean();
        if (load is not null && load is not (GateRules.Full or GateRules.Empty)) return TosSupport.Invalid("fullEmpty", "Use FULL or EMPTY.");
        var excluded = excludeBookingContainerIds ?? [];

        var rows =
            from x in db.BookingContainers.AsNoTracking()
            join b in db.Bookings on x.BookingId equals b.BookingId
            join r in db.EquipmentRequirements on x.EquipmentRequirementId equals r.EquipmentRequirementId
            let next = db.MovementPlans.Where(p => p.BookingContainerId == x.BookingContainerId && p.Status == "PENDING")
                .OrderBy(p => p.SequenceNo).FirstOrDefault()
            where b.BranchId == branchId && b.Status == BookingRules.Open && x.EndedAt == null && next != null
                  && !excluded.Contains(x.BookingContainerId)
            select new { x, b, r.EquipmentTypeCode, next };

        if (query.Search.Clean() is { } q)
        {
            var box = ContainerNumber.Normalise(q);
            rows = rows.Where(r => r.b.OrderNo.Contains(q) || r.b.CarrierRef!.Contains(q) || r.b.CustomerRef!.Contains(q) || r.x.ContainerNo == box);
        }

        // The step's direction and full/empty are MDM rules: read them per order type, then filter.
        var candidates = await rows.OrderByDescending(r => r.b.CreatedAt).ThenBy(r => r.x.ContainerNo).Take(MaxBookableBoxes).ToListAsync(ct);
        // A box another truck's clerk holds (Record, GATE_IN_BIG_SAVE §1) is not offered.
        var now = time.GetUtcNow();
        var ids = candidates.Select(r => r.x.BookingContainerId).ToList();
        var numbers = candidates.Select(r => r.x.ContainerNo).OfType<string>().ToList();
        var held = await db.BoxReservations.AsNoTracking()
            .Where(h => h.ReleasedAt == null && h.ExpiresAt > now && h.DraftId != draftId
                        && ((h.BookingContainerId != null && ids.Contains(h.BookingContainerId.Value)) || (h.ContainerNo != null && numbers.Contains(h.ContainerNo))))
            .Select(h => new { h.BookingContainerId, h.ContainerNo }).ToListAsync(ct);
        candidates = candidates.Where(r => !held.Any(h => h.BookingContainerId == r.x.BookingContainerId || (h.ContainerNo != null && h.ContainerNo == r.x.ContainerNo))).ToList();
        var plans = await master.OrderTypePlansAsync(candidates.Select(r => r.b.OrderTypeCode).Distinct(), ct);
        var picked = candidates
            .Select(r => (Row: r, Rules: plans.GetValueOrDefault(r.b.OrderTypeCode)?.Steps.FirstOrDefault(s => s.OrderTypeMovementId == r.next!.OrderTypeMovementId)))
            .Where(r => r.Rules is not null && (way is null || r.Rules.Direction == way) && (load is null || r.Rules.FullEmpty == load))
            .ToList();

        var page = Math.Max(query.Page ?? 1, 1);
        var size = Math.Clamp(query.PageSize ?? 50, 1, 200);
        var items = picked.Skip((page - 1) * size).Take(size).Select(p => new BookableBoxResponse(
            p.Row.x.BookingContainerId, p.Row.b.BookingId, p.Row.b.OrderNo, p.Row.b.CarrierRef,
            p.Row.b.BookingTypeCode, p.Row.b.OrderTypeCode, p.Row.b.LinePartyCode, p.Row.b.AgentPartyCode, p.Row.b.CustomerPartyCode,
            p.Row.x.ContainerNo, p.Row.EquipmentTypeCode,
            new BookableStepResponse(p.Row.next!.MovementPlanId, p.Row.next.SequenceNo, p.Row.next.MovementCode, p.Rules!.Direction, p.Rules.FullEmpty)))
            .ToList();
        return TypedResults.Ok(new PagedResult<BookableBoxResponse>(items, page, size, picked.Count));
    }

    /// <summary>The picker searches at most this many boxes, newest bookings first: the clerk types a B/L to narrow it.</summary>
    private const int MaxBookableBoxes = 500;

    // ── preflight ───────────────────────────────────────────────────────────

    private static async Task<Results<Ok<GatePreflightResponse>, ValidationProblem, ProblemHttpResult>> PreflightAsync(
        BarrierReader barrier, TosDbContext db, IMasterDataReferences master, ICallerPermissions scope, TimeProvider time, CancellationToken ct,
        Guid? branchId = null, string? containerNo = null, string? direction = null, DateTimeOffset? at = null,
        Guid? truckVisitId = null, string? truckCategoryCode = null, string? haulierCode = null, Guid? draftId = null,
        IUserDirectory? users = null)
    {
        if (branchId is null) return TosSupport.Invalid("branchId", "Which gate? A barrier belongs to a depot.");
        if (string.IsNullOrWhiteSpace(containerNo)) return TosSupport.Invalid("containerNo", "The number the camera or the clerk read.");

        var way = direction.Clean() ?? GateRules.In;
        if (!GateRules.Directions.Contains(way)) return TosSupport.Invalid("direction", "Use IN or OUT.");
        if (!scope.HasAt(TosPermissions.GateView, branchId.Value))
            return TosScope.OutsideYourBranches("That gate is at a depot you do not cover.");

        var view = await barrier.ReadAsync(branchId.Value, containerNo, way, at ?? time.GetUtcNow(), ct);
        if (await HeldFindingAsync(db, users, view, draftId, time.GetUtcNow(), ct) is { } held) view.Findings.Add(held);

        // The truck, when the clerk has named it: is it the truck that was paid for (§7.4, §7.5)?
        var (category, haulier) = (truckCategoryCode.Clean(), haulierCode.Clean());
        if (truckVisitId is { } visitId)
        {
            var visit = await db.TruckVisits.AsNoTracking().SingleOrDefaultAsync(v => v.TruckVisitId == visitId, ct);
            if (visit is null) return TosSupport.Invalid("truckVisitId", "Unknown truck visit.");
            (category, haulier) = (visit.TruckCategoryCode, visit.HaulierPartyCode);
        }
        if (truckVisitId is not null || category is not null || haulier is not null)
            view.Findings.AddRange(await NotAsPaidAsync(master, view, category, haulier, ct));

        return TypedResults.Ok(await ProjectAsync(db, view, ct));
    }

    /// <summary>
    /// The truck at the gate against what its coupon was priced with (gecko_tos 16),
    /// read locally (ADR-007). The truck's side is resolved as Revenue resolves it
    /// when it prices the gate event (CashQuoter.ResolveAsync): no category → the
    /// tenant default, no haulier → the booking's.
    /// </summary>
    private static async Task<List<GateFinding>> NotAsPaidAsync(IMasterDataReferences master, BarrierView view,
        string? truckCategory, string? haulierCode, CancellationToken ct)
    {
        if (view.Coupon is not { } coupon || view.Booking is null) return [];

        truckCategory ??= (await master.GetStringSettingAsync(RevenueSettingKeys.DefaultTruckCategory, view.Booking.BranchId, ct)).Clean();
        return GateRules.NotAsPaid(coupon.CouponRef, coupon.Amount is not null, coupon.TruckCategoryCode, truckCategory,
            coupon.HaulierPartyCode, haulierCode ?? view.Booking.HaulierPartyCode).ToList();
    }

    // ── the gate event ──────────────────────────────────────────────────────

    private static async Task<Results<Created<GateTransactionResponse>, ValidationProblem, ProblemHttpResult>> RecordAsync(
        GateTransactionRequest request, TosDbContext db, BarrierReader barrier, IMasterDataReferences master,
        BranchClock clock, ITenantContext caller, ICallerPermissions scope, TimeProvider time, IUserDirectory users,
        HttpContext http, CancellationToken ct)
    {
        // A repeated request with the same Idempotency-Key (a retry after a lost answer) gets the EIR
        // the first one recorded — not "nothing pending", and never a second move (gecko_tos 24).
        var (key, badKey) = Idempotency.KeyOf(http.Request);
        if (badKey is not null) return TosSupport.Invalid(Idempotency.Header, badKey);
        return await RecordCoreAsync(request, key, db, barrier, master, clock, caller, scope, time, users, ct);
    }

    /// <summary>
    /// The gate move itself — the endpoint above, and each row of the big Save (TripEndpoints), which gives
    /// every row its own key so a resumed Save answers with the EIRs already recorded.
    /// </summary>
    internal static async Task<Results<Created<GateTransactionResponse>, ValidationProblem, ProblemHttpResult>> RecordCoreAsync(
        GateTransactionRequest request, string? key, TosDbContext db, BarrierReader barrier, IMasterDataReferences master,
        BranchClock clock, ITenantContext caller, ICallerPermissions scope, TimeProvider time, IUserDirectory users, CancellationToken ct)
    {
        var branchId = request.BranchId!.Value;
        var way = request.Direction.Clean()!;
        if (!scope.HasAt(TosPermissions.GateCreate, branchId))
            return TosScope.OutsideYourBranches("That gate is at a depot you do not cover.");

        var hash = key is null ? null : Idempotency.HashOf(request);
        if (key is not null && await ReplayAsync(db, key, hash!, ct) is { } replay) return replay;

        var branch = (await clock.BranchesAsync([branchId], ct)).GetValueOrDefault(branchId);
        if (branch is null) return TosSupport.Invalid("branchId", "Unknown branch.");

        var now = time.GetUtcNow();
        var at = request.TransactionAt ?? now;
        if (at > now.AddMinutes(5))
            return TosSupport.Invalid("transactionAt", "A box cannot cross the barrier in the future.");

        var seals = request.Seals ?? [];

        // Vector parity (gate-in-vector-parity.md §2): a trip type must agree with the direction.
        var tripType = request.TripType.Clean()!;   // [Required] on the request
        if (GateRules.TripTypeContradiction(tripType, way) is { } contradiction)
            return TosSupport.Invalid("tripType", contradiction);

        // §5.5 — everything below commits together or not at all.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // The barrier read is done INSIDE the transaction: a hold applied two
        // seconds ago must still stop this box.
        var view = await barrier.ReadAsync(branchId, request.ContainerNo, way, at, ct);
        if (view.Booking is null || view.Step is null || view.StepRules is null || view.Assignment is null)
            return Refused(view);

        // Vector's mandatory-field matrix (§2.1, GateRules.MissingForTrip), on every
        // transaction — judged on the step's load state and the booking's direction,
        // not on what the clerk picked. A missing input is a 400 before any refusal.
        var missing = GateRules.MissingForTrip(tripType, view.StepRules.FullEmpty,
            view.Booking.DirectionCode.Contains("EXPORT", StringComparison.OrdinalIgnoreCase),
            request.TareWeightKg, request.MaxGrossWeightKg, request.CargoWeightKg, request.CustomsPermitNo, seals.Count);
        if (missing.Count > 0) return TypedResults.ValidationProblem(missing);

        var findings = view.Findings.ToList();
        findings.AddRange(GateRules.Observations(view.StepRules, request.GrossWeightKg, seals.Count));
        // Another truck's clerk holds this box (Record, GATE_IN_BIG_SAVE §1).
        if (await HeldFindingAsync(db, users, view, request.DraftId, now, ct) is { } held) findings.Add(held);

        if (findings.Any(f => f.Severity == GateSeverity.Block)) return Refused(view with { Findings = findings });

        // An override is a permission AND a typed reason. Either alone is a shrug.
        if (view.NeedsLateOverride)
        {
            if (!scope.HasAt(TosPermissions.CutoffOverride, branchId))
                return Forbidden($"This box is late. Only someone holding {TosPermissions.CutoffOverride} can let it in.", findings);
            if (string.IsNullOrWhiteSpace(request.LateOverrideReason))
                return TosSupport.Invalid("lateOverrideReason", "Say why the late gate is allowed. Vector let 686 boxes in during 2025 with nothing written here.");
        }
        if (view.NeedsCheckDigitOverride)
        {
            if (!scope.HasAt(TosPermissions.GateOverride, branchId))
                return Forbidden($"{view.ContainerNo} fails its check digit. Only someone holding {TosPermissions.GateOverride} can accept it.", findings);
            if (string.IsNullOrWhiteSpace(request.CheckDigitOverrideReason))
                return TosSupport.Invalid("checkDigitOverrideReason", "Say why a number that fails ISO 6346 is being accepted.");
        }

        // ── the truck ───────────────────────────────────────────────────────
        TruckVisit visit;
        if (request.TruckVisitId is { } visitId)
        {
            var existing = await db.TruckVisits.SingleOrDefaultAsync(v => v.TruckVisitId == visitId, ct);
            if (existing is null) return TosSupport.Invalid("truckVisitId", "Unknown truck visit.");
            if (existing.GateOutAt is not null) return TosSupport.Conflict($"Visit {existing.VisitNo} has already left.");
            if (existing.BranchId != branchId) return TosSupport.Invalid("truckVisitId", "That visit belongs to another depot.");
            visit = existing;
        }
        else
        {
            if (request.Truck is null)
                return TosSupport.Invalid("truck", "Name the truck, or the open visit it is already on.");

            var haulier = request.Truck.HaulierCode.Clean() is { } code
                ? (await master.PartiesAsync([code], ct)).GetValueOrDefault(code)
                : null;
            if (request.Truck.HaulierCode.Clean() is not null && haulier is null)
                return TosSupport.Invalid("truck.haulierCode", "Unknown haulier.");

            // A tariff axis (§3.2): only a value of the tenant's TRUCK_CATEGORY code list prices.
            var truckCategory = request.Truck.TruckCategoryCode.Clean();
            if (truckCategory is not null
                && !(await master.CodeListValuesAsync(TruckCategoryList, [truckCategory], ct)).Contains(truckCategory))
                return TosSupport.Invalid("truck.truckCategoryCode", $"'{truckCategory}' is not a truck category of this tenant.");

            visit = new TruckVisit
            {
                TenantId = caller.TenantId(),
                BranchId = branchId,
                VisitNo = await TosNumberSeries.NextAsync(db, TosNumberSeries.TruckVisit, branchId, branch.BranchCode, clock.LocalNow(branch), ct),
                TruckPlate = request.Truck.Plate.Trim(),
                TrailerPlate = request.Truck.TrailerPlate?.Trim(),
                HaulierPartyId = haulier?.PartyId,
                HaulierPartyCode = haulier?.PartyCode,
                DriverName = request.Truck.DriverName?.Trim(),
                DriverLicenceHash = Hash(request.Truck.DriverLicence),
                LaneCode = request.Truck.LaneCode.Clean(),
                TruckCategoryCode = truckCategory,
                ArrivedAt = request.Truck.ArrivedAt ?? at,
                Source = "GATE",
            };
            db.TruckVisits.Add(visit);
        }
        visit.GateInAt ??= at;
        if (visit.ArrivedAt > at) visit.ArrivedAt = at;   // the CHECK: in never precedes arrival
        await db.SaveChangesAsync(ct);

        // Not the truck that was paid for (§7.4, §7.5): said on the EIR's response, never a refusal.
        findings.AddRange(await NotAsPaidAsync(master, view, visit.TruckCategoryCode, visit.HaulierPartyCode, ct));

        // ≤ 2 boxes each way (drop-one-take-one, twin 20s); the index is the backstop.
        var taken = await db.GateTransactions
            .CountAsync(g => g.TruckVisitId == visit.TruckVisitId && g.Direction == way && g.Status == "COMPLETED", ct);
        if (taken >= 2)
            return TosSupport.Conflict($"Visit {visit.VisitNo} already has {taken} box(es) going {way}.", "A truck carries two.");

        // ── the EIR ─────────────────────────────────────────────────────────
        var declaredSeal = view.Assignment.DeclaredSealNo.Clean();
        var sealMismatch = declaredSeal is not null && seals.Count > 0
                           && !seals.Any(s => string.Equals(s.SealNo.Trim(), declaredSeal, StringComparison.OrdinalIgnoreCase));

        var transaction = new GateTransaction
        {
            TenantId = caller.TenantId(),
            BranchId = branchId,
            EirNo = await TosNumberSeries.NextAsync(db, TosNumberSeries.Eir, branchId, branch.BranchCode, clock.LocalNow(branch), ct),
            TruckVisitId = visit.TruckVisitId,
            Direction = way,
            PositionNo = (byte)(taken + 1),
            MovementId = view.Step.MovementId,
            MovementCode = view.Step.MovementCode,
            FullEmpty = view.StepRules.FullEmpty,
            ContainerNo = view.ContainerNo,
            ContainerId = view.Registry?.ContainerId,
            IsCheckDigitValid = view.IsCheckDigitValid,
            CheckDigitOverrideBy = view.IsCheckDigitValid ? null : caller.UserId(),
            CheckDigitOverrideReason = view.IsCheckDigitValid ? null : (request.CheckDigitOverrideReason?.Trim() ?? "Recorded: the depot does not enforce the check digit."),
            EquipmentTypeId = view.Requirement?.EquipmentTypeId,
            EquipmentTypeCode = view.Requirement?.EquipmentTypeCode,
            IsoCode = request.IsoCode.Clean(),
            BookingId = view.Booking.BookingId,
            BookingContainerId = view.Assignment.BookingContainerId,
            MovementPlanId = view.Step.MovementPlanId,
            LinePartyId = view.Booking.LinePartyId,
            LinePartyCode = view.Booking.LinePartyCode,
            VesselCallId = view.Booking.VesselCallId,
            GrossWeightKg = request.GrossWeightKg,
            TareWeightKg = request.TareWeightKg,
            VgmKg = request.VgmKg,
            VgmMethod = request.VgmMethod,
            WeightSource = request.WeightSource,
            ConditionCode = request.ConditionCode.Clean(),
            GradeCode = request.GradeCode.Clean(),
            SealMismatch = sealMismatch,
            TempObservedC = request.TemperatureC,
            YardId = request.YardId,
            YardSlotId = request.YardSlotId,
            PositionText = request.PositionText.Clean(),
            CutoffKindApplied = view.CutoffKind,
            CutoffAtApplied = view.CutoffAt,
            IsLate = view.IsLate,
            CutoffExceptionId = view.CoveringException?.CutoffExceptionId,
            LateOverrideBy = view.NeedsLateOverride ? caller.UserId() : null,
            LateOverrideReason = view.NeedsLateOverride ? request.LateOverrideReason!.Trim() : null,
            GateAuthorizationId = view.Coupon?.GateAuthorizationId,
            TransactionAt = at,
            RecordedAt = now,
            Status = "COMPLETED",
            Remarks = request.Remarks?.Trim(),
            TripTypeCode = tripType,
            MaterialCode = request.MaterialCode.Clean(),
            MaxGrossWeightKg = request.MaxGrossWeightKg,
            CargoWeightKg = request.CargoWeightKg,
            VentSetting = request.VentSetting.Clean(),
            HumidityPct = request.HumidityPct,
            GensetNo = request.GensetNo.Clean(),
            ClipOnNo = request.ClipOnNo.Clean(),
            CustomsPermitNo = request.CustomsPermitNo.Clean(),
            PaperlessCode = request.PaperlessCode.Clean(),
            NextLocationCode = request.NextLocationCode.Clean(),
            HeightCode = request.HeightCode.Clean() ?? (view.Requirement?.EquipmentTypeCode is { } typeCode
                ? (await master.EquipmentTypesAsync([typeCode], ct)).GetValueOrDefault(typeCode)?.HeightClass
                : null),
            IdempotencyKey = key,
            IdempotencyHash = hash,
        };
        db.GateTransactions.Add(transaction);
        try { await db.SaveChangesAsync(ct); }   // the id comes from NEWSEQUENTIALID()
        catch (DbUpdateException e) when (key is not null && e.InnerException?.Message.Contains("uq_gate_transaction__idempotency_key") == true)
        {
            // The same key arrived twice at once and the other request won: answer with its EIR.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return await ReplayAsync(db, key, hash!, ct)
                   ?? TosSupport.Conflict($"A request with {Idempotency.Header} {key} is still being processed.", "Repeat it in a moment.");
        }

        foreach (var seal in seals)
            db.GateTransactionSeals.Add(new GateTransactionSeal
            {
                TenantId = transaction.TenantId,
                GateTransactionId = transaction.GateTransactionId,
                SealNo = seal.SealNo.Trim(),
                SealType = seal.SealType,
                IsIntact = seal.IsIntact,
                MatchesDeclared = declaredSeal is null ? null : string.Equals(seal.SealNo.Trim(), declaredSeal, StringComparison.OrdinalIgnoreCase),
            });

        // ── the step, and any optional one it overtook (§5.3) ───────────────
        foreach (var skipped in view.StepsToSkip)
        {
            var row = await db.MovementPlans.SingleAsync(p => p.MovementPlanId == skipped.MovementPlanId, ct);
            row.Status = "SKIPPED";
            row.SkippedAt = at;
            row.SkippedBy = caller.UserId();
            row.SkipReason = GateRules.SkipReason;
        }

        var stepRow = await db.MovementPlans.SingleAsync(p => p.MovementPlanId == view.Step.MovementPlanId, ct);
        if (stepRow.Status != "PENDING") return TosSupport.Conflict($"{stepRow.MovementCode} is already {stepRow.Status}.");
        stepRow.Status = "DONE";
        stepRow.GateTransactionId = transaction.GateTransactionId;

        // ── the yard (D-5): one row, opened or closed, never both ───────────
        Guid? containerVisitId = null;
        if (way == GateRules.In)
        {
            var yardRow = new ContainerVisit
            {
                TenantId = transaction.TenantId,
                BranchId = branchId,
                ContainerNo = view.ContainerNo,
                ContainerId = view.Registry?.ContainerId,
                EquipmentTypeId = view.Requirement?.EquipmentTypeId,
                EquipmentTypeCode = view.Requirement?.EquipmentTypeCode,
                LinePartyId = view.Booking.LinePartyId,
                LinePartyCode = view.Booking.LinePartyCode,
                GateInTransactionId = transaction.GateTransactionId,
                FullEmpty = view.StepRules.FullEmpty,
                ConditionCode = request.ConditionCode.Clean(),
                GradeCode = request.GradeCode.Clean(),
                YardId = request.YardId,
                YardSlotId = request.YardSlotId,
                PositionText = request.PositionText.Clean(),
                CurrentBookingContainerId = view.Assignment.BookingContainerId,
                LastEventAt = at,
            };
            db.ContainerVisits.Add(yardRow);
            await db.SaveChangesAsync(ct);
            containerVisitId = yardRow.ContainerVisitId;
            AddEvent(db, yardRow, "GATE_IN", null, transaction.EirNo, at, caller.UserId(), transaction.GateTransactionId);
        }
        else if (view.OpenVisit is { } open)
        {
            var yardRow = await db.ContainerVisits.SingleAsync(v => v.ContainerVisitId == open.ContainerVisitId, ct);
            yardRow.GateOutTransactionId = transaction.GateTransactionId;
            yardRow.LastEventAt = at;
            containerVisitId = yardRow.ContainerVisitId;
            AddEvent(db, yardRow, "GATE_OUT", yardRow.FullEmpty, transaction.EirNo, at, caller.UserId(), transaction.GateTransactionId);

            // A reefer still plugged in is unplugged by leaving (TIER3 §6): same
            // transaction, and queued before ContainerGatedOut so Revenue sees it first.
            await ReeferLog.CloseOnGateOutAsync(db, yardRow, transaction.GateTransactionId, at, caller.UserId(), now, ct);
        }

        // ── the assignment: the last step ends it (D-3) ─────────────────────
        var stillPending = await db.MovementPlans.CountAsync(p =>
            p.BookingContainerId == view.Assignment.BookingContainerId
            && p.Status == "PENDING" && p.MovementPlanId != stepRow.MovementPlanId, ct);
        var completed = stillPending == 0;
        if (completed)
        {
            var assignment = await db.BookingContainers.SingleAsync(x => x.BookingContainerId == view.Assignment.BookingContainerId, ct);
            assignment.EndedAt = at;
            assignment.EndedBy = caller.UserId();
            assignment.EndReason = "COMPLETED";
        }

        // ── the coupon (ADR-007): spent here, once ──────────────────────────
        if (view.Coupon is { } coupon)
        {
            var couponRow = await db.GateAuthorizations.SingleAsync(a => a.GateAuthorizationId == coupon.GateAuthorizationId, ct);
            couponRow.ConsumedByGateTransactionId = transaction.GateTransactionId;
            couponRow.ConsumedAt = at;
        }

        // ── the holds on the box are done: it went through ─────────────────
        await BoxReservations.ConsumeAsync(db, view.Assignment.BookingContainerId, view.ContainerNo, transaction.GateTransactionId, caller.UserId(), now, ct);

        // ── the outbox: the LINE message leaves from here, not from the barrier ─
        await QueueAsync(db, transaction, view, visit, taken, completed, ct);

        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await tx.CommitAsync(ct);

        var response = await ProjectAsync(db, transaction.GateTransactionId, containerVisitId, completed, ct, findings);
        return TypedResults.Created($"/api/tos/gate/transactions/{transaction.GateTransactionId}", response);
    }

    /// <summary>The EIR an earlier request with this key recorded, answered as that request was (201); a 422 for another body; null when the key is new.</summary>
    private static async Task<Results<Created<GateTransactionResponse>, ValidationProblem, ProblemHttpResult>?> ReplayAsync(
        TosDbContext db, string key, byte[] hash, CancellationToken ct)
    {
        var made = await db.GateTransactions.AsNoTracking().Where(g => g.IdempotencyKey == key)
            .Select(g => new { g.GateTransactionId, g.IdempotencyHash, g.BookingContainerId }).SingleOrDefaultAsync(ct);
        if (made is null) return null;
        if (!Idempotency.SameRequest(made.IdempotencyHash, hash)) return Idempotency.DifferentRequest(key);
        var visitId = await db.ContainerVisits.AsNoTracking()
            .Where(v => v.GateInTransactionId == made.GateTransactionId || v.GateOutTransactionId == made.GateTransactionId)
            .Select(v => (Guid?)v.ContainerVisitId).FirstOrDefaultAsync(ct);
        var completed = await db.BookingContainers.AsNoTracking()
            .AnyAsync(x => x.BookingContainerId == made.BookingContainerId && x.EndReason == "COMPLETED", ct);
        return TypedResults.Created($"/api/tos/gate/transactions/{made.GateTransactionId}",
            await ProjectAsync(db, made.GateTransactionId, visitId, completed, ct));
    }

    /// <summary>
    /// What would refuse this move, read before any money is taken (GATE_IN_COMPLETION_PLAN A1): the barrier's
    /// BLOCK findings except "not paid yet" (NO_COUPON), and the trip's mandatory fields. A blind row has no
    /// order yet, so NO_ASSIGNMENT is expected and its step is BLIND GATE IN's first.
    /// </summary>
    internal static async Task<List<GateFinding>> PrecheckAsync(GateTransactionRequest move, bool blind, BarrierReader barrier,
        IMasterDataReferences master, TimeProvider time, CancellationToken ct)
    {
        var way = move.Direction.Clean()!;
        var view = await barrier.ReadAsync(move.BranchId!.Value, move.ContainerNo, way, move.TransactionAt ?? time.GetUtcNow(), ct);
        var blocks = view.Findings
            .Where(f => f.Severity == GateSeverity.Block && f.Code != "NO_COUPON" && !(blind && f.Code == "NO_ASSIGNMENT"))
            .ToList();
        if (GateRules.TripTypeContradiction(move.TripType.Clean()!, way) is { } contradiction)
            blocks.Add(new GateFinding("TRIP_TYPE", contradiction, GateSeverity.Block));

        string? fullEmpty = view.StepRules?.FullEmpty;
        var export = view.Booking?.DirectionCode.Contains("EXPORT", StringComparison.OrdinalIgnoreCase) ?? false;
        if (fullEmpty is null && blind)
            fullEmpty = (await master.OrderTypePlansAsync(["BLIND GATE IN"], ct)).GetValueOrDefault("BLIND GATE IN")?
                .Steps.OrderBy(s => s.SequenceNo).FirstOrDefault()?.FullEmpty;
        // Vector GateIn.cs:1191: a box with no paperwork comes in EMPTY; a laden one needs a real order.
        if (blind && (fullEmpty == GateRules.Full || move.CargoWeightKg > 0))
            blocks.Add(new GateFinding("BLIND_FULL", "A box on no order (BLIND GATE IN) can only come in EMPTY: a FULL box needs its booking.", GateSeverity.Block));
        if (fullEmpty is not null)
            foreach (var (field, messages) in GateRules.MissingForTrip(move.TripType.Clean()!, fullEmpty, export,
                         move.TareWeightKg, move.MaxGrossWeightKg, move.CargoWeightKg, move.CustomsPermitNo, (move.Seals ?? []).Count))
                blocks.Add(new GateFinding("MISSING_FIELD", $"{field}: {string.Join(" ", messages)}", GateSeverity.Block));
        return blocks;
    }

    /// <summary>BOX_RESERVED when another draft holds the box the barrier is reading (GATE_IN_BIG_SAVE §1).</summary>
    private static async Task<GateFinding?> HeldFindingAsync(TosDbContext db, IUserDirectory? users, BarrierView view, Guid? draftId,
        DateTimeOffset now, CancellationToken ct)
    {
        var hold = await BoxReservations.HeldByOtherAsync(db, view.Assignment?.BookingContainerId, view.ContainerNo, draftId, now, ct);
        if (hold is null) return null;
        var name = users is null ? null : (await users.DisplayNamesAsync([hold.ReservedBy], ct)).GetValueOrDefault(hold.ReservedBy);
        return BoxReservations.Finding(hold, name);
    }

    // ── void ────────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<GateTransactionResponse>, NotFound, ValidationProblem, ProblemHttpResult>> VoidAsync(
        Guid id, VoidGateTransactionRequest request, TosDbContext db, ITenantContext caller,
        ICallerPermissions scope, TimeProvider time, CancellationToken ct)
    {
        var transaction = await db.GateTransactions.SingleOrDefaultAsync(g => g.GateTransactionId == id, ct);
        if (transaction is null || !scope.HasAt(TosPermissions.GateOverride, transaction.BranchId)) return TypedResults.NotFound();
        if (transaction.Status == "VOIDED")
            return TosSupport.Conflict($"{transaction.EirNo} was already voided.", transaction.VoidReason);
        // The rowVersion is optional (older callers send none); when sent it is
        // applied, so a void of an EIR someone else changed meanwhile is a 409.
        if (request.RowVersion is not null && !db.TrySetExpectedVersion(transaction, request.RowVersion))
            return TosSupport.Invalid("rowVersion", "Send the rowVersion you received when reading the EIR.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // §10.9: the void and the re-opened step are one transaction, or a box
        // ends up with a done step and no EIR behind it.
        transaction.Status = "VOIDED";
        transaction.VoidedAt = time.GetUtcNow();
        transaction.VoidedBy = caller.UserId();
        transaction.VoidReason = request.Reason.Trim();

        var step = await db.MovementPlans.SingleOrDefaultAsync(p => p.GateTransactionId == id, ct);
        if (step is not null)
        {
            step.Status = "PENDING";
            step.GateTransactionId = null;
        }

        // The yard row goes back to what it was: a voided gate-in never happened,
        // and a voided gate-out puts the box back in the yard.
        var opened = await db.ContainerVisits.SingleOrDefaultAsync(v => v.GateInTransactionId == id, ct);
        if (opened is not null)
        {
            opened.DeletedAt = transaction.VoidedAt;
            opened.DeletedBy = caller.UserId();
        }
        var closed = await db.ContainerVisits.SingleOrDefaultAsync(v => v.GateOutTransactionId == id, ct);
        if (closed is not null)
        {
            closed.GateOutTransactionId = null;
            closed.LastEventAt = transaction.VoidedAt!.Value;
            AddEvent(db, closed, "CORRECTION", transaction.EirNo, null, transaction.VoidedAt!.Value, caller.UserId(), id);
        }

        // A reefer that gate-out unplugged is plugged in again: the box never left.
        var replugged = closed is null ? [] : await ReeferLog.ReopenOnVoidAsync(db, id, ct);
        foreach (var session in replugged)
            AddEvent(db, closed!, "CORRECTION", "PLUG_OUT", null, transaction.VoidedAt!.Value, caller.UserId(), session.ReeferPowerSessionId);

        // An assignment closed by that step re-opens with it.
        var assignment = await db.BookingContainers.SingleOrDefaultAsync(x => x.BookingContainerId == transaction.BookingContainerId, ct);
        if (assignment is { EndReason: "COMPLETED" })
        {
            assignment.EndedAt = null;
            assignment.EndedBy = null;
            assignment.EndReason = null;
        }

        var coupon = await db.GateAuthorizations.SingleOrDefaultAsync(a => a.ConsumedByGateTransactionId == id, ct);
        if (coupon is not null)
        {
            coupon.ConsumedByGateTransactionId = null;
            coupon.ConsumedAt = null;
        }

        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await QueueVoidAsync(db, transaction,
            await db.TruckVisits.AsNoTracking().SingleOrDefaultAsync(v => v.TruckVisitId == transaction.TruckVisitId, ct),
            // The visit's other boxes still standing: Revenue re-raises the visit's
            // once-per-truck gate charge on one of them (GATE_CHARGING_DESIGN §3 d).
            await db.GateTransactions.AsNoTracking()
                .Where(g => g.TruckVisitId == transaction.TruckVisitId && g.GateTransactionId != transaction.GateTransactionId && g.Status == "COMPLETED")
                .OrderBy(g => g.TransactionAt).ThenBy(g => g.PositionNo)
                .Select(g => new VisitSurvivor(g.GateTransactionId, g.EirNo, g.ContainerNo, g.Direction, g.MovementCode, g.BookingId,
                    g.BookingContainerId, g.TransactionAt))
                .ToListAsync(ct),
            ct);
        foreach (var session in replugged)
            await ReeferLog.EnqueueAsync(db, session, closed!, ReeferLog.Corrected, transaction.VoidedAt!.Value, ct);
        await tx.CommitAsync(ct);

        return TypedResults.Ok(await ProjectAsync(db, id, null, false, ct));
    }

    // ── reads ───────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<PagedResult<GateTransactionSummaryResponse>>, ValidationProblem>> ListAsync(
        [AsParameters] ListQuery query, TosDbContext db, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, string? direction = null, string? status = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, bool? lateOnly = null,
        string? containerNo = null, string? truck = null, Guid? bookingId = null)
    {
        var rows =
            from t in db.GateTransactions.AsNoTracking()
            join b in db.Bookings on t.BookingId equals b.BookingId
            join v in db.TruckVisits on t.TruckVisitId equals v.TruckVisitId
            select new { g = t, b.OrderNo, v.TruckPlate };

        if (branchId is not null) rows = rows.Where(r => r.g.BranchId == branchId);
        if (direction.Clean() is { } way)
        {
            if (!GateRules.Directions.Contains(way)) return TosSupport.Invalid("direction", "Use IN or OUT.");
            rows = rows.Where(r => r.g.Direction == way);
        }
        if (status.Clean() is { } s) rows = rows.Where(r => r.g.Status == s);
        if (from is not null) rows = rows.Where(r => r.g.TransactionAt >= from);
        if (to is not null) rows = rows.Where(r => r.g.TransactionAt < to);
        if (lateOnly == true) rows = rows.Where(r => r.g.IsLate);
        // The register's own filters, each narrowing on top of the free search.
        if (ContainerNumber.Normalise(containerNo ?? "") is { Length: > 0 } boxNo) rows = rows.Where(r => r.g.ContainerNo == boxNo);
        if (truck.Clean() is { } plate) rows = rows.Where(r => r.TruckPlate.Contains(plate));
        if (bookingId is not null) rows = rows.Where(r => r.g.BookingId == bookingId);
        if (query.Search.Clean() is { } q)
        {
            var box = ContainerNumber.Normalise(q);
            rows = rows.Where(r => r.g.ContainerNo == box || r.g.EirNo.Contains(q) || r.OrderNo.Contains(q));
        }
        if (scope.BranchFilter(TosPermissions.GateView) is { } mine)
        {
            var allowed = mine.ToList();
            rows = rows.Where(r => allowed.Contains(r.g.BranchId));
        }

        var page = await rows.OrderByDescending(r => r.g.TransactionAt).ToPagedAsync(query.Page, query.PageSize, ct);

        return TypedResults.Ok(new PagedResult<GateTransactionSummaryResponse>(
            page.Items.Select(r => new GateTransactionSummaryResponse(
                r.g.GateTransactionId, r.g.EirNo, r.g.Direction, r.g.MovementCode, r.g.FullEmpty,
                r.g.ContainerNo, r.OrderNo, r.g.LinePartyCode, r.TruckPlate,
                r.g.TransactionAt, r.g.IsLate, r.g.Status)).ToList(),
            page.Page, page.PageSize, page.TotalCount));
    }

    private static async Task<Results<FileContentHttpResult, NotFound>> EirPdfAsync(
        Guid id, TosDbContext db, EirDocument eir, ICallerPermissions scope, CancellationToken ct)
    {
        var branchId = await db.GateTransactions.AsNoTracking()
            .Where(g => g.GateTransactionId == id).Select(g => (Guid?)g.BranchId).SingleOrDefaultAsync(ct);
        if (branchId is not { } b || !scope.HasAt(TosPermissions.GateView, b)) return TypedResults.NotFound();

        var rendered = await eir.RenderAsync(id, ct);
        return rendered is null
            ? TypedResults.NotFound()
            : TypedResults.File(rendered.Pdf, "application/pdf", rendered.FileName);
    }

    private static async Task<Results<FileContentHttpResult, NotFound>> TruckInPdfAsync(
        Guid id, TosDbContext db, TruckInDocument form, ICallerPermissions scope, CancellationToken ct)
    {
        var branchId = await db.TruckVisits.AsNoTracking()
            .Where(v => v.TruckVisitId == id).Select(v => (Guid?)v.BranchId).SingleOrDefaultAsync(ct);
        if (branchId is not { } b || !scope.HasAt(TosPermissions.GateView, b)) return TypedResults.NotFound();

        var rendered = await form.RenderAsync(id, ct);
        return rendered is null
            ? TypedResults.NotFound()
            : TypedResults.File(rendered.Pdf, "application/pdf", rendered.FileName);
    }

    private static async Task<Results<Ok<GateTransactionResponse>, NotFound>> GetAsync(
        Guid id, TosDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var branchId = await db.GateTransactions.AsNoTracking()
            .Where(g => g.GateTransactionId == id).Select(g => (Guid?)g.BranchId).SingleOrDefaultAsync(ct);
        if (branchId is not { } b || !scope.HasAt(TosPermissions.GateView, b)) return TypedResults.NotFound();

        return TypedResults.Ok(await ProjectAsync(db, id, null, false, ct));
    }

    private static async Task<Results<Ok<PagedResult<TruckVisitResponse>>, ValidationProblem>> ListVisitsAsync(
        [AsParameters] ListQuery query, TosDbContext db, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, bool? openOnly = null, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var rows = db.TruckVisits.AsNoTracking();
        if (branchId is not null) rows = rows.Where(v => v.BranchId == branchId);
        if (openOnly == true) rows = rows.Where(v => v.GateOutAt == null);
        if (from is not null) rows = rows.Where(v => v.ArrivedAt >= from);
        if (to is not null) rows = rows.Where(v => v.ArrivedAt < to);
        if (query.Search.Clean() is { } q) rows = rows.Where(v => v.VisitNo.Contains(q) || v.TruckPlate.Contains(q));
        if (scope.BranchFilter(TosPermissions.GateView) is { } mine)
        {
            var allowed = mine.ToList();
            rows = rows.Where(v => allowed.Contains(v.BranchId));
        }

        var page = await rows.OrderByDescending(v => v.ArrivedAt).ToPagedAsync(query.Page, query.PageSize, ct);

        // The page's moves that stand, counted per visit and direction: the visit's mode is derived from them.
        var ids = page.Items.Select(v => v.TruckVisitId).ToList();
        var moves = await db.GateTransactions.AsNoTracking()
            .Where(g => ids.Contains(g.TruckVisitId) && g.Status == "COMPLETED")
            .GroupBy(g => new { g.TruckVisitId, g.Direction })
            .Select(g => new { g.Key.TruckVisitId, g.Key.Direction, Count = g.Count() })
            .ToListAsync(ct);
        int Moves(Guid visitId, string way) => moves.Where(m => m.TruckVisitId == visitId && m.Direction == way).Sum(m => m.Count);

        return TypedResults.Ok(new PagedResult<TruckVisitResponse>(
            page.Items.Select(v => Project(v, [], GateRules.VisitMode(Moves(v.TruckVisitId, GateRules.In), Moves(v.TruckVisitId, GateRules.Out)))).ToList(),
            page.Page, page.PageSize, page.TotalCount));
    }

    private static async Task<Results<Ok<TruckVisitResponse>, NotFound>> GetVisitAsync(
        Guid id, TosDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var visit = await db.TruckVisits.AsNoTracking().SingleOrDefaultAsync(v => v.TruckVisitId == id, ct);
        if (visit is null || !scope.HasAt(TosPermissions.GateView, visit.BranchId)) return TypedResults.NotFound();

        var boxes = await (
            from t in db.GateTransactions.AsNoTracking().Where(x => x.TruckVisitId == id)
            join b in db.Bookings on t.BookingId equals b.BookingId
            orderby t.Direction, t.PositionNo
            select new GateTransactionSummaryResponse(
                t.GateTransactionId, t.EirNo, t.Direction, t.MovementCode, t.FullEmpty,
                t.ContainerNo, b.OrderNo, t.LinePartyCode, visit.TruckPlate,
                t.TransactionAt, t.IsLate, t.Status)).ToListAsync(ct);

        return TypedResults.Ok(Project(visit, boxes, ModeOf(boxes)));
    }

    private static string ModeOf(IReadOnlyList<GateTransactionSummaryResponse> boxes) => GateRules.VisitMode(
        boxes.Count(b => b.Status == "COMPLETED" && b.Direction == GateRules.In),
        boxes.Count(b => b.Status == "COMPLETED" && b.Direction == GateRules.Out));

    private static async Task<Results<Ok<TruckVisitResponse>, NotFound, ValidationProblem, ProblemHttpResult>> DepartAsync(
        Guid id, DepartTruckRequest? request, TosDbContext db, ICallerPermissions scope, TimeProvider time, CancellationToken ct)
    {
        var visit = await db.TruckVisits.SingleOrDefaultAsync(v => v.TruckVisitId == id, ct);
        if (visit is null || !scope.HasAt(TosPermissions.GateCreate, visit.BranchId)) return TypedResults.NotFound();
        if (visit.GateOutAt is not null) return TosSupport.Conflict($"Visit {visit.VisitNo} left at {visit.GateOutAt:u}.");

        var departedAt = request?.DepartedAt ?? time.GetUtcNow();
        if (visit.GateInAt is null) return TosSupport.Conflict($"Visit {visit.VisitNo} never came in.", "Record the gate-in first.");
        if (departedAt < visit.GateInAt) return TosSupport.Invalid("departedAt", "A truck cannot leave before it came in (V-13: Vector has 38,148 of these).");

        visit.GateOutAt = departedAt;
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        var ways = await db.GateTransactions.AsNoTracking()
            .Where(g => g.TruckVisitId == id && g.Status == "COMPLETED").Select(g => g.Direction).ToListAsync(ct);
        return TypedResults.Ok(Project(visit, [], GateRules.VisitMode(ways.Count(w => w == GateRules.In), ways.Count(w => w == GateRules.Out))));
    }

    private static async Task<Results<Ok<PagedResult<YardContainerResponse>>, ValidationProblem>> InYardAsync(
        [AsParameters] ListQuery query, TosDbContext db, ICallerPermissions scope, CancellationToken ct,
        Guid? branchId = null, string? lineCode = null, string? fullEmpty = null, bool? heldOnly = null, int? minDays = null)
    {
        var rows = db.VwContainerInYards.AsNoTracking();

        if (branchId is not null) rows = rows.Where(v => v.BranchId == branchId);
        if (lineCode.Clean() is { } line) rows = rows.Where(v => v.LinePartyCode == line);
        if (fullEmpty.Clean() is { } load)
        {
            if (load is not (GateRules.Full or GateRules.Empty)) return TosSupport.Invalid("fullEmpty", "Use FULL or EMPTY.");
            rows = rows.Where(v => v.FullEmpty == load);
        }
        if (heldOnly == true) rows = rows.Where(v => v.IsHeld == true);
        if (minDays is { } days) rows = rows.Where(v => v.DaysInYard >= days);
        if (query.Search.Clean() is { } q)
        {
            var box = ContainerNumber.Normalise(q);
            rows = rows.Where(v => v.ContainerNo == box || v.PositionText!.Contains(q));
        }
        if (scope.BranchFilter(TosPermissions.GateView) is { } mine)
        {
            var allowed = mine.ToList();
            rows = rows.Where(v => allowed.Contains(v.BranchId));
        }

        var page = await rows.OrderByDescending(v => v.GateInAt).ToPagedAsync(query.Page, query.PageSize, ct);

        return TypedResults.Ok(new PagedResult<YardContainerResponse>(
            page.Items.Select(v => new YardContainerResponse(
                v.ContainerVisitId, v.BranchId, v.ContainerNo, v.EquipmentTypeCode, v.LinePartyCode,
                v.FullEmpty, v.ConditionCode, v.GradeCode, v.PositionText,
                // The view computes both; EF types a view column as nullable.
                v.GateInAt, v.GateInEirNo, v.GateInMovementCode, v.DaysInYard ?? 0, v.IsHeld == true,
                v.CurrentBookingContainerId, v.LastEventAt)).ToList(),
            page.Page, page.PageSize, page.TotalCount));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static byte[]? Hash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant()));

    private static void AddEvent(TosDbContext db, ContainerVisit visit, string type, string? from, string? to,
        DateTimeOffset at, Guid? by, Guid? reference) =>
        VisitJournal.Add(db, visit, type, from, to, at, by, reference);

    /// <summary>
    /// The gate event. Notification turns it into the LINE message; Revenue prices
    /// it (PLAN_BILLING §4.3), so it carries every axis the resolver needs, because
    /// Revenue may not come and read gecko_tos (ADR-007). It goes in the SAME
    /// transaction as the EIR (11_outbox): if the gate event rolls back, the
    /// message was never queued.
    /// </summary>
    /// <remarks>
    /// Gate charging (GATE_CHARGING_DESIGN §2, S2): the truck facts Revenue prices
    /// the credit side with at gate time — the visit (the PER_TRIP gate charge is
    /// once per truck visit), its category, the trip type, and the VISIT's haulier
    /// (Vector overrides on the truck's haulier, GateIn.cs:1320). haulierPartyCode
    /// stays the booking's, as before.
    /// </remarks>
    private static Task QueueAsync(TosDbContext db, GateTransaction transaction, BarrierView view, TruckVisit visit, int visitBoxIndex,
        bool completed, CancellationToken ct)
    {
        var booking = view.Booking!;
        return EnqueueAsync(db, transaction,
            transaction.Direction == GateRules.In ? "ContainerGatedIn" : "ContainerGatedOut",
            new
            {
                gateTransactionId = transaction.GateTransactionId,
                eirNo = transaction.EirNo,
                branchId = transaction.BranchId,
                containerNo = transaction.ContainerNo,
                direction = transaction.Direction,
                movementCode = transaction.MovementCode,
                fullEmpty = transaction.FullEmpty,
                bookingId = transaction.BookingId,
                bookingContainerId = transaction.BookingContainerId,
                orderNo = booking.OrderNo,
                orderTypeCode = booking.OrderTypeCode,
                bookingTypeCode = booking.BookingTypeCode,
                lineCode = transaction.LinePartyCode,
                customerCode = booking.CustomerPartyCode,
                agentPartyCode = booking.AgentPartyCode,
                forwarderPartyCode = booking.ForwarderPartyCode,
                haulierPartyCode = booking.HaulierPartyCode,
                equipmentTypeCode = transaction.EquipmentTypeCode,
                isoCode = transaction.IsoCode,
                cargoClassCode = booking.CargoClassCode,
                cargoCategoryCode = booking.CargoCategoryCode,
                isDangerousGoods = BarrierReader.IsDangerous(booking, view.Requirement),
                grossWeightKg = transaction.GrossWeightKg,
                gateAuthorizationId = transaction.GateAuthorizationId,
                transactionAt = transaction.TransactionAt,
                isLate = transaction.IsLate,
                bookingContainerCompleted = completed,
                truckVisitId = visit.TruckVisitId,
                visitNo = visit.VisitNo,
                visitBoxIndex,
                truckCategoryCode = visit.TruckCategoryCode,
                tripTypeCode = transaction.TripTypeCode,
                visitHaulierPartyCode = visit.HaulierPartyCode,
            }, ct);
    }

    /// <summary>
    /// A void is news too: anything raised from that gate move — a charge, a
    /// LINE message, a CODECO — must be able to take it back.
    /// </summary>
    private sealed record VisitSurvivor(Guid GateTransactionId, string? EirNo, string ContainerNo, string Direction, string? MovementCode,
        Guid? BookingId, Guid? BookingContainerId, DateTimeOffset TransactionAt);

    private static Task QueueVoidAsync(TosDbContext db, GateTransaction transaction, TruckVisit? visit,
        IReadOnlyList<VisitSurvivor> survivors, CancellationToken ct) =>
        EnqueueAsync(db, transaction, "GateTransactionVoided", new
        {
            gateTransactionId = transaction.GateTransactionId,
            eirNo = transaction.EirNo,
            branchId = transaction.BranchId,
            containerNo = transaction.ContainerNo,
            direction = transaction.Direction,
            movementCode = transaction.MovementCode,
            bookingId = transaction.BookingId,
            bookingContainerId = transaction.BookingContainerId,
            voidedAt = transaction.VoidedAt,
            voidReason = transaction.VoidReason,
            truckVisitId = transaction.TruckVisitId,
            visitNo = visit?.VisitNo,
            truckCategoryCode = visit?.TruckCategoryCode,
            tripTypeCode = transaction.TripTypeCode,
            visitHaulierPartyCode = visit?.HaulierPartyCode,
            visitSurvivors = survivors,
        }, ct);

    private static Task EnqueueAsync(TosDbContext db, GateTransaction transaction, string messageType, object payload, CancellationToken ct) =>
        TosOutbox.EnqueueAsync(db, transaction.TenantId, "GATE_TRANSACTION", transaction.GateTransactionId, messageType, payload, ct);

    private static ProblemHttpResult Refused(BarrierView view) =>
        TypedResults.Problem(
            title: $"{view.ContainerNo} cannot go {view.Direction}.",
            detail: string.Join(" ", view.Findings.Where(f => f.Severity == GateSeverity.Block).Select(f => f.Message)),
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>
            {
                ["decision"] = view.Decision,
                ["findings"] = view.Findings.Select(f => new GateFindingResponse(f.Code, f.Message, f.Severity.ToString().ToUpperInvariant())).ToList(),
            });

    private static ProblemHttpResult Forbidden(string title, IReadOnlyList<GateFinding> findings) =>
        TypedResults.Problem(
            title: title,
            statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?>
            {
                ["findings"] = findings.Select(f => new GateFindingResponse(f.Code, f.Message, f.Severity.ToString().ToUpperInvariant())).ToList(),
            });

    private static TruckVisitResponse Project(TruckVisit v, IReadOnlyList<GateTransactionSummaryResponse> boxes, string? mode = null) => new(
        v.TruckVisitId, v.VisitNo, v.BranchId, v.TruckPlate, v.TrailerPlate, v.HaulierPartyCode, v.DriverName, v.LaneCode,
        v.ArrivedAt, v.GateInAt, v.GateOutAt, v.DwellMinutes,
        v.GateOutAt is not null ? "DEPARTED" : v.GateInAt is not null ? "ON_SITE" : "ARRIVED",
        v.Source, boxes, v.TruckCategoryCode, mode);

    private static async Task<GatePreflightResponse> ProjectAsync(TosDbContext db, BarrierView view, CancellationToken ct)
    {
        string? callRef = view.Booking?.VesselCallId is { } callId
            ? await db.VesselCalls.AsNoTracking().Where(c => c.VesselCallId == callId).Select(c => c.CallRef).SingleOrDefaultAsync(ct)
            : null;

        return new GatePreflightResponse(
            view.ContainerNo, view.Direction, view.At, view.Decision,
            view.Findings.Select(f => new GateFindingResponse(f.Code, f.Message, f.Severity.ToString().ToUpperInvariant())).ToList(),
            view.Booking is null || view.Assignment is null ? null : new GateBookingResponse(
                view.Booking.BookingId, view.Booking.OrderNo, view.Booking.BranchId, view.Booking.OrderTypeCode,
                view.Booking.DirectionCode, view.Booking.LinePartyCode, view.Booking.CustomerPartyCode,
                view.Booking.VesselCallId, callRef, view.Booking.ValidTo,
                view.Assignment.BookingContainerId, view.Assignment.DeclaredSealNo, view.Assignment.DeclaredVgmKg,
                view.Requirement?.EquipmentTypeCode),
            view.Step is null || view.StepRules is null ? null : new GateStepResponse(
                view.Step.MovementPlanId, view.Step.SequenceNo, view.Step.MovementCode, view.StepRules.Direction,
                view.StepRules.FullEmpty, view.Step.IsRequired, view.StepRules.CheckSealNo, view.StepRules.CheckGrossWeight,
                view.StepRules.RequireVesselVoyage, view.StepRules.AllowDamagedRelease, view.StepRules.RequiresSurvey,
                view.StepsToSkip.Select(s => s.MovementCode).ToList()),
            view.Holds.Select(h => new GateHoldResponse(
                h.Hold.ContainerHoldId, h.Hold.HoldCode, h.Definition?.DescriptionEn, h.Definition?.BlockingScope,
                h.Definition?.ReleaseAuthority, h.Hold.HeldVia,
                h.Definition is not null && HoldRules.Blocks(h.Definition.BlockingScope, view.Direction)
                && !GateRules.ReleasedByMovement(h.Definition.AutoApplyOnEvent, view.Direction, view.StepRules?.AllowDamagedRelease ?? false))).ToList(),
            view.OpenVisit is null ? null : new GateYardResponse(
                view.OpenVisit.ContainerVisitId, view.OpenVisit.BranchId, view.OpenVisit.FullEmpty,
                view.OpenVisit.PositionText, view.OpenVisit.LastEventAt),
            view.CutoffKind is null || view.CutoffAt is null ? null : new GateCutoffResponse(
                view.CutoffKind, view.CutoffAt.Value, view.IsLate, view.CoveringException?.CutoffExceptionId),
            view.Coupon is null ? null : new GateCouponResponse(
                view.Coupon.GateAuthorizationId, view.Coupon.CouponRef, view.Coupon.PaymentChannel, view.Coupon.ValidUntil),
            view.IsCheckDigitValid, view.Registry is not null);
    }

    private static async Task<GateTransactionResponse> ProjectAsync(
        TosDbContext db, Guid id, Guid? containerVisitId, bool completed, CancellationToken ct,
        IReadOnlyList<GateFinding>? findings = null)
    {
        var row = await (
            from t in db.GateTransactions.AsNoTracking().Where(x => x.GateTransactionId == id)
            join b in db.Bookings on t.BookingId equals b.BookingId
            join v in db.TruckVisits on t.TruckVisitId equals v.TruckVisitId
            select new { g = t, b.OrderNo, v.VisitNo, v.TruckPlate, v.TruckCategoryCode }).SingleAsync(ct);

        var seals = await db.GateTransactionSeals.AsNoTracking()
            .Where(s => s.GateTransactionId == id)
            .Select(s => new GateSealResponse(s.SealNo, s.SealType, s.IsIntact, s.MatchesDeclared))
            .ToListAsync(ct);

        var visitId = containerVisitId ?? await db.ContainerVisits.AsNoTracking()
            .Where(v => v.GateInTransactionId == id || v.GateOutTransactionId == id)
            .Select(v => (Guid?)v.ContainerVisitId).FirstOrDefaultAsync(ct);

        var g = row.g;
        return new GateTransactionResponse(
            g.GateTransactionId, g.EirNo, g.BranchId, g.Direction, g.PositionNo,
            g.MovementCode, g.FullEmpty, g.ContainerNo, g.IsCheckDigitValid,
            g.EquipmentTypeCode, g.BookingId, row.OrderNo, g.LinePartyCode,
            g.TruckVisitId, row.VisitNo, row.TruckPlate,
            g.GrossWeightKg, g.VgmKg, g.VgmMethod, g.WeightSource,
            g.ConditionCode, g.GradeCode, g.SealMismatch, seals,
            g.CutoffKindApplied, g.CutoffAtApplied, g.IsLate,
            g.CutoffExceptionId, g.LateOverrideReason, g.CheckDigitOverrideReason,
            g.GateAuthorizationId,
            g.TransactionAt, g.RecordedAt, g.Status,
            visitId, completed, Convert.ToBase64String(g.RowVersion),
            g.TareWeightKg, g.TempObservedC, g.IsoCode, g.PositionText, g.SurveyId, g.Remarks,
            g.VoidedAt, g.VoidedBy, g.VoidReason, g.ReplacesGateTransactionId,
            row.TruckCategoryCode, g.TripTypeCode, g.MaterialCode,
            g.MaxGrossWeightKg, g.CargoWeightKg, g.VentSetting, g.HumidityPct,
            g.GensetNo, g.ClipOnNo, g.CustomsPermitNo, g.PaperlessCode, g.NextLocationCode,
            findings?.Select(f => new GateFindingResponse(f.Code, f.Message, f.Severity.ToString().ToUpperInvariant())).ToList(),
            g.HeightCode);
    }
}
