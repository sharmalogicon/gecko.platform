using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Gecko.Data;
using Gecko.MasterData.Infrastructure.Persistence;
using Gecko.MasterData.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Endpoints.Commercial;

public sealed record OrderTypeResponse(
    Guid OrderTypeId, string OrderTypeCode, string DescriptionEn, string? DescriptionLocal,
    string DirectionCode, Guid? ServiceTypeId, string? ServiceCode, string CargoClassCode,
    string? BookingTypeCode, bool IsActive, string RowVersion, bool RequiresVesselSchedule = false,
    OrderTypeRulesItem? Rules = null);

/// <summary>
/// The order type's rules (gecko_master 34, WALK_IN_ORDERS_PLAN §2) — the form's Rules panel. Each one off / null is
/// what the gate did before they existed.
/// </summary>
/// <param name="IsWalkIn">R1: a truck may come with no paperwork; the gate raises one booking of this order type for it.
/// Such an order type has one step, and at most one walk-in order type per step direction × full/empty is active.</param>
/// <param name="PaymentRule">R2: CASH_ONLY, CREDIT_FOR_CREDIT_CUSTOMERS or CREDIT_WITHIN_LIMIT; null = the charge rows decide.</param>
/// <param name="ReleaseGradeCodes">R3: container grades a pick-up may take; empty = any.</param>
/// <param name="OwnerMatch">R4: REFUSE or ALLOW a box of another owner than the booking's line; null = the tenant setting.</param>
/// <param name="MaxBoxesPerTruck">R5: walk-in boxes of this order type on one truck; null = no cap.</param>
public sealed record OrderTypeRulesItem(
    bool IsWalkIn = false,
    [property: MaxLength(30)] string? PaymentRule = null,
    IReadOnlyList<string>? ReleaseGradeCodes = null,
    [property: MaxLength(10)] string? OwnerMatch = null,
    [property: Range(1, 99)] short? MaxBoxesPerTruck = null);

public sealed record OrderTypeDetailResponse(
    OrderTypeResponse OrderType,
    IReadOnlyList<OrderTypeMovementResponse> Movements,
    IReadOnlyList<OrderTypeChargeResponse> Charges);

/// <summary>
/// One step of an order type, and the gate rules that step enforces. These five
/// flags are the whole reason this table exists rather than a movement list.
/// </summary>
public sealed record OrderTypeMovementResponse(
    Guid OrderTypeMovementId, Guid MovementId, string MovementCode, string MovementDescription,
    short SequenceNo, bool IsRequired, bool IsBillable,
    bool CheckSealNo, bool CheckGrossWeight, bool RequireVesselVoyage, bool AllowDamagedRelease, bool SkipEdi,
    string? PudoMode, bool RequireSurvey = false, bool SealMustMatch = false);

public sealed record OrderTypeChargeResponse(
    Guid OrderTypeChargeId, Guid ChargeCodeId, string ChargeCode, string ChargeDescription,
    Guid? MovementId, string? MovementCode, string PaymentTo, string? PaymentTermCode,
    bool IsDefault, bool IsOptional, bool IsCargoCharge, bool IsValueAddedService, bool RaiseAtGateIn,
    decimal? DefaultQty);

public sealed record SaveOrderTypeRequest(
    // Real order type codes are human phrases: Vector's are 'EXP CY/CY',
    // 'IMP LOLO CR', 'EXP CY-IN (NON-NOMINATING)'. An identifier-shaped regex here
    // would reject every code the customer already has painted on their paperwork.
    [property: Required, RegularExpression("^[A-Z0-9][A-Z0-9 /()._-]{0,49}$", ErrorMessage = "Upper-case letters, digits, spaces and / ( ) . _ - , up to 50 chars — e.g. EXP CY/CY.")] string OrderTypeCode,
    [property: Required, MaxLength(255)] string DescriptionEn,
    // Not on the form any more (owner 2026-10-04): derived from the booking type when left out.
    [property: MaxLength(20)] string? DirectionCode,
    [property: Required, MaxLength(20)] string CargoClassCode,
    [property: MaxLength(255)] string? DescriptionLocal = null,
    [property: MaxLength(15)] string? ServiceCode = null,
    [property: MaxLength(20)] string? BookingTypeCode = null,
    bool IsActive = true,
    string? RowVersion = null,
    // gecko_master 27: on = a booking needs a vessel call and real ports; off = no vessel / port validation.
    // Null = leave as it is (off on create).
    bool? RequiresVesselSchedule = null,
    // gecko_master 34: the whole Rules panel. Null = leave the rules as they are (none on create).
    OrderTypeRulesItem? Rules = null);

public sealed record OrderTypeMovementItem(
    [property: Required, MaxLength(20)] string MovementCode,
    [property: Required, Range(1, 99)] short SequenceNo,
    bool IsRequired = true,
    bool IsBillable = true,
    bool CheckSealNo = false,
    bool CheckGrossWeight = false,
    bool RequireVesselVoyage = false,
    bool AllowDamagedRelease = false,
    bool SkipEdi = false,
    [property: MaxLength(20)] string? PudoMode = null,
    // gecko_master 34: R6 a damage survey on the same truck visit before the gate-in; R12 the keyed seal is a booked one.
    bool RequireSurvey = false,
    bool SealMustMatch = false);

/// <summary>The whole sequence, plus the order type's rowVersion: replacing the steps is an edit of the order type.</summary>
public sealed record ReplaceOrderTypeMovementsRequest(
    [property: Required, MinLength(1)] IReadOnlyList<OrderTypeMovementItem> Movements,
    string? RowVersion = null);

public sealed record OrderTypeChargeItem(
    [property: Required, MaxLength(15)] string ChargeCode,
    [property: Required, MaxLength(20)] string PaymentTo,   // soft ref -> lookup.bill_to_role
    [property: MaxLength(20)] string? MovementCode = null,
    [property: MaxLength(20)] string? PaymentTermCode = null,
    bool IsDefault = true,
    bool IsOptional = false,
    bool IsCargoCharge = false,
    bool IsValueAddedService = false,
    bool RaiseAtGateIn = false,
    [property: Range(0.001, 99999)] decimal? DefaultQty = null);

/// <summary>The whole charge set, plus the order type's rowVersion: replacing the charges is an edit of the order type.</summary>
public sealed record ReplaceOrderTypeChargesRequest(IReadOnlyList<OrderTypeChargeItem> Charges, string? RowVersion = null);

/// <summary>
/// An order type is what the depot is being asked to do — "export CY to CY",
/// "import empty return". It expands into an ordered list of MOVEMENTS, and each
/// movement carries the rules the gate enforces when that step happens.
///
/// THE FIVE GATE RULES (Vector: IsCheckSealNo, IsCheckGrossWgt,
/// IsVslVoyMandatory, IsReleaseDamageContainer, IsSkipEDI) are configuration, not
/// code. The 2026-09-15 gap report found them missing from the first cut of this
/// schema; they are the difference between a gate that enforces a customer's
/// actual policy and one that hard-codes ours.
///
/// Vector's real data shows why they must be PER STEP and not per order type:
/// 'EXP CY/CY' checks the gross weight on the empty-out and the laden-in but NOT
/// on the laden-out, and 'IMP CY/CY' allows a damaged container to be released on
/// every step while 'EXP CY/CY' allows it on none.
/// </summary>
internal static class OrderTypeEndpoints
{
    public static RouteGroupBuilder MapOrderTypeEndpoints(this RouteGroupBuilder master)
    {
        var orderTypes = master.MapGroup("/order-types").WithTags("Master data — order types");

        orderTypes.MapGet("/", ListAsync).RequirePermission(MasterDataPermissions.CommercialView).WithSummary("List order types");
        orderTypes.MapGet("/{orderTypeCode}", GetAsync).RequirePermission(MasterDataPermissions.CommercialView).WithName("GetOrderType").WithSummary("Get one order type with its movements, gate rules and charges");
        orderTypes.MapPost("/", CreateAsync).RequirePermission(MasterDataPermissions.CommercialManage).Validate<SaveOrderTypeRequest>().WithSummary("Create an order type");
        orderTypes.MapPut("/{orderTypeCode}", UpdateAsync).RequirePermission(MasterDataPermissions.CommercialManage).Validate<SaveOrderTypeRequest>().WithSummary("Update an order type");
        orderTypes.MapPut("/{orderTypeCode}/movements", ReplaceMovementsAsync).RequirePermission(MasterDataPermissions.CommercialManage).Validate<ReplaceOrderTypeMovementsRequest>().WithSummary("Replace the movement sequence and its gate rules");
        orderTypes.MapPut("/{orderTypeCode}/charges", ReplaceChargesAsync).RequirePermission(MasterDataPermissions.CommercialManage).Validate<ReplaceOrderTypeChargesRequest>().WithSummary("Replace the charges an order type raises");
        orderTypes.MapDelete("/{orderTypeCode}", DeleteAsync).RequirePermission(MasterDataPermissions.CommercialManage).WithSummary("Soft-delete an order type");

        return master;
    }

    private static async Task<Ok<PagedResult<OrderTypeResponse>>> ListAsync(
        [AsParameters] ListQuery query, MasterDataDbContext db, CancellationToken ct,
        string? directionCode = null, string? cargoClassCode = null, bool includeInactive = false)
    {
        var orderTypes = db.OrderTypes.AsNoTracking();
        if (!includeInactive) orderTypes = orderTypes.Where(o => o.IsActive);
        if (!string.IsNullOrWhiteSpace(directionCode)) orderTypes = orderTypes.Where(o => o.DirectionCode == directionCode.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(cargoClassCode)) orderTypes = orderTypes.Where(o => o.CargoClassCode == cargoClassCode.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(query.Search))
            orderTypes = orderTypes.Where(o => o.OrderTypeCode.Contains(query.Search) || o.DescriptionEn.Contains(query.Search));

        return TypedResults.Ok(await Project(db, orderTypes.OrderBy(o => o.OrderTypeCode))
            .ToPagedAsync(query.Page, query.PageSize, ct));
    }

    /// <summary>The order type with its steps and charges — what GET returns, and what a child-set replace answers with (carrying the new rowVersion).</summary>
    private static async Task<OrderTypeDetailResponse> DetailAsync(MasterDataDbContext db, Guid orderTypeId, CancellationToken ct) =>
        new(await Project(db, db.OrderTypes.AsNoTracking().Where(o => o.OrderTypeId == orderTypeId)).SingleAsync(ct),
            await MovementsOfAsync(db, orderTypeId, ct),
            await ChargesOfAsync(db, orderTypeId, ct));

    private static async Task<Results<Ok<OrderTypeDetailResponse>, NotFound>> GetAsync(
        string orderTypeCode, MasterDataDbContext db, CancellationToken ct)
    {
        var code = orderTypeCode.FromRouteCode();
        var orderType = await Project(db, db.OrderTypes.AsNoTracking().Where(o => o.OrderTypeCode == code)).SingleOrDefaultAsync(ct);
        if (orderType is null) return TypedResults.NotFound();

        return TypedResults.Ok(new OrderTypeDetailResponse(
            orderType,
            await MovementsOfAsync(db, orderType.OrderTypeId, ct),
            await ChargesOfAsync(db, orderType.OrderTypeId, ct)));
    }

    private static async Task<Results<CreatedAtRoute<OrderTypeDetailResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SaveOrderTypeRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var code = request.OrderTypeCode.ToUpperInvariant();
        if (await ValidateAsync(db, request, ct) is { } problem) return problem;
        if (request.Rules is { } rules && await ValidateRulesAsync(db, null, rules, ct) is { } invalidRules) return invalidRules;
        if (await db.OrderTypes.AnyAsync(o => o.OrderTypeCode == code, ct))
            return MasterDataSupport.Conflict($"Order type '{code}' already exists.");

        var orderType = new OrderType { TenantId = caller.TenantId(), OrderTypeCode = code };
        await ApplyAsync(db, orderType, request, ct);
        db.OrderTypes.Add(orderType);
        await db.SaveChangesAsync(ct);

        var response = await Project(db, db.OrderTypes.AsNoTracking().Where(o => o.OrderTypeId == orderType.OrderTypeId)).SingleAsync(ct);
        return TypedResults.CreatedAtRoute(
            new OrderTypeDetailResponse(response, [], []), "GetOrderType", new { orderTypeCode = code });
    }

    private static async Task<Results<Ok<OrderTypeResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        string orderTypeCode, SaveOrderTypeRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var orderType = await db.OrderTypes.SingleOrDefaultAsync(o => o.OrderTypeCode == orderTypeCode.FromRouteCode(), ct);
        if (orderType is null) return TypedResults.NotFound();
        if (!db.TrySetExpectedVersion(orderType, request.RowVersion))
            return MasterDataSupport.InvalidReference("rowVersion", "Send the rowVersion you received when reading the record.");
        if (await ValidateAsync(db, request, ct) is { } problem) return problem;
        if (request.RequiresVesselSchedule == false
            && await db.OrderTypeMovements.AnyAsync(m => m.OrderTypeId == orderType.OrderTypeId && m.RequireVesselVoyage, ct))
            return MasterDataSupport.InvalidReference("requiresVesselSchedule",
                "A step of this order type requires the vessel/voyage at the gate; untick that step first.");
        if (request.Rules is { } rules && await ValidateRulesAsync(db, orderType, rules, ct) is { } invalidRules) return invalidRules;

        await ApplyAsync(db, orderType, request, ct);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        return TypedResults.Ok(await Project(db, db.OrderTypes.AsNoTracking().Where(o => o.OrderTypeId == orderType.OrderTypeId)).SingleAsync(ct));
    }

    /// <summary>
    /// The movement sequence and its gate rules, replaced as a set. Two unique
    /// indexes make partial edits hazardous — uq_otm__movement (one row per
    /// movement) and uq_otm__sequence (one row per position) — and a sequence with
    /// a gap or a repeat is a gate that does not know what happens next.
    /// </summary>
    private static async Task<Results<Ok<OrderTypeDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> ReplaceMovementsAsync(
        string orderTypeCode, ReplaceOrderTypeMovementsRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var orderType = await db.OrderTypes.SingleOrDefaultAsync(o => o.OrderTypeCode == orderTypeCode.FromRouteCode(), ct);
        if (orderType is null) return TypedResults.NotFound();

        // Rows keep the caller's order for error keys (movements[1].movementCode);
        // the sequence check below works on the sorted copy.
        var rows = request.Movements
            .Select(m => m with { MovementCode = m.MovementCode.ToUpperInvariant(), PudoMode = m.PudoMode?.ToUpperInvariant() })
            .ToList();
        var errors = new RowErrors("movements");

        var movements = await db.Movements.AsNoTracking()
            .Select(m => new { m.MovementId, m.MovementCode }).ToListAsync(ct);
        var modes = await db.VwCodeLists.AsNoTracking()
            .Where(v => v.CategoryCode == "PICKUP_DROPOFF_MODE" && v.IsActive == true).Select(v => v.Code!).ToListAsync(ct);

        var seenMovement = new HashSet<string>();
        var seenSequence = new HashSet<short>();
        for (var i = 0; i < rows.Count; i++)
        {
            var m = rows[i];
            if (movements.All(x => x.MovementCode != m.MovementCode))
                errors.Add(i, "movementCode", $"Unknown movement(s): {m.MovementCode}.");
            else if (!seenMovement.Add(m.MovementCode))
                errors.Add(i, "movementCode", $"{m.MovementCode} appears twice. Each movement may appear once per order type.");
            if (!seenSequence.Add(m.SequenceNo))
                errors.Add(i, "sequenceNo", $"Two steps share sequence number {m.SequenceNo}.");
            if (m.PudoMode is not null && !modes.Contains(m.PudoMode))
                errors.Add(i, "pudoMode", $"'{m.PudoMode}' is not a PICKUP_DROPOFF_MODE.");
            if (m.RequireVesselVoyage && !orderType.RequiresVesselSchedule)
                errors.Add(i, "requireVesselVoyage", $"{orderType.OrderTypeCode} does not require a vessel schedule; tick that on the order type first.");
            if (m.SealMustMatch && !m.CheckSealNo)
                errors.Add(i, "sealMustMatch", "Tick Check Seal Number too: the seal is matched only where the gate records one.");
        }
        if (orderType.IsWalkIn && rows.Count != 1)
            errors.Add(null, null, $"{orderType.OrderTypeCode} is a walk-in order type: it has exactly one step (the truck's drop-off or pick-up).");
        else if (orderType.IsWalkIn && errors.Count == 0
                 && await WalkInStepProblemAsync(db, orderType.OrderTypeId, movements.Single(x => x.MovementCode == rows[0].MovementCode).MovementId, ct) is { } taken)
            errors.Add(0, "movementCode", taken);

        // 1, 2, 3 with no gaps: the gate walks the sequence, and a hole in it means
        // a step that silently never runs.
        var wanted = rows.OrderBy(m => m.SequenceNo).ToList();
        if (errors.Count == 0)
            for (var i = 0; i < wanted.Count; i++)
                if (wanted[i].SequenceNo != i + 1)
                {
                    errors.Add(null, null, $"Sequence numbers must run 1..{wanted.Count} with no gaps; found {string.Join(", ", wanted.Select(m => m.SequenceNo))}.");
                    break;
                }

        // A charge pinned to a step that is being removed would never be raised again.
        var dropped = await (
            from c in db.OrderTypeCharges.AsNoTracking()
            join mv in db.Movements on c.MovementId equals mv.MovementId
            where c.OrderTypeId == orderType.OrderTypeId
            select mv.MovementCode).Distinct().ToListAsync(ct);
        foreach (var code in dropped.Where(d => !seenMovement.Contains(d)))
            errors.Add(null, null, $"{code} still has charges pinned to it. Move or remove those charges first.");

        if (errors.Count > 0) return errors.Problem();

        if (db.TouchParent(orderType, request.RowVersion) is { } missing) return missing;

        // One transaction: the soft deletes flush (past the filtered unique indexes)
        // with the version-guarded touch of the order type, so a stale editor is
        // refused before anything changes and a failed insert loses nothing.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.OrderTypeMovements.Where(m => m.OrderTypeId == orderType.OrderTypeId).ToListAsync(ct);
        foreach (var gone in existing) db.OrderTypeMovements.Remove(gone);
        if (await db.SaveOrConflictAsync(ct) is { } stale) return stale;

        foreach (var m in wanted)
        {
            db.OrderTypeMovements.Add(new OrderTypeMovement
            {
                TenantId = caller.TenantId(),
                OrderTypeId = orderType.OrderTypeId,
                MovementId = movements.Single(x => x.MovementCode == m.MovementCode).MovementId,
                SequenceNo = m.SequenceNo,
                IsRequired = m.IsRequired,
                IsBillable = m.IsBillable,
                CheckSealNo = m.CheckSealNo,
                CheckGrossWeight = m.CheckGrossWeight,
                RequireVesselVoyage = m.RequireVesselVoyage,
                AllowDamagedRelease = m.AllowDamagedRelease,
                SkipEdi = m.SkipEdi,
                PudoMode = m.PudoMode,
                RequireSurvey = m.RequireSurvey,
                SealMustMatch = m.SealMustMatch,
            });
        }

        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await tx.CommitAsync(ct);
        return TypedResults.Ok(await DetailAsync(db, orderType.OrderTypeId, ct));
    }

    private static async Task<Results<Ok<OrderTypeDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> ReplaceChargesAsync(
        string orderTypeCode, ReplaceOrderTypeChargesRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var orderType = await db.OrderTypes.SingleOrDefaultAsync(o => o.OrderTypeCode == orderTypeCode.FromRouteCode(), ct);
        if (orderType is null) return TypedResults.NotFound();

        var wanted = request.Charges
            .Select(c => c with
            {
                ChargeCode = c.ChargeCode.ToUpperInvariant(),
                MovementCode = c.MovementCode?.ToUpperInvariant(),
                PaymentTo = c.PaymentTo.ToUpperInvariant(),
                PaymentTermCode = c.PaymentTermCode?.ToUpperInvariant(),
            })
            .ToList();
        var errors = new RowErrors("charges");

        var unknownPayers = (await db.UnknownBillToAsync(wanted.Select(c => c.PaymentTo), ct)).ToHashSet();
        var terms = wanted.Select(c => c.PaymentTermCode).OfType<string>().Distinct().ToList();
        var knownTerms = (await db.PaymentTerms.AsNoTracking().Where(p => terms.Contains(p.Code) && p.IsActive)
            .Select(p => p.Code).ToListAsync(ct)).ToHashSet();
        var charges = await db.ChargeCodes.AsNoTracking().Select(c => new { c.ChargeCodeId, c.ChargeCode1 }).ToListAsync(ct);
        var movements = await db.Movements.AsNoTracking().Select(m => new { m.MovementId, m.MovementCode }).ToListAsync(ct);
        // A charge pinned to a movement that is not one of THIS order type's steps is never raised.
        var steps = await (
            from s in db.OrderTypeMovements.AsNoTracking()
            join mv in db.Movements on s.MovementId equals mv.MovementId
            where s.OrderTypeId == orderType.OrderTypeId
            select mv.MovementCode).ToListAsync(ct);

        var seen = new HashSet<(string, string?, string)>();
        for (var i = 0; i < wanted.Count; i++)
        {
            var c = wanted[i];
            if (charges.All(x => x.ChargeCode1 != c.ChargeCode))
                errors.Add(i, "chargeCode", $"Unknown charge code(s): {c.ChargeCode}.");
            if (unknownPayers.Contains(c.PaymentTo))
                errors.Add(i, "paymentTo", $"Unknown bill-to role(s): {c.PaymentTo}.");
            // payment_term_code is a soft ref too, and was not checked here before.
            if (c.PaymentTermCode is not null && !knownTerms.Contains(c.PaymentTermCode))
                errors.Add(i, "paymentTermCode", $"Unknown payment term '{c.PaymentTermCode}'.");
            if (c.MovementCode is not null)
            {
                if (movements.All(x => x.MovementCode != c.MovementCode))
                    errors.Add(i, "movementCode", $"Unknown movement(s): {c.MovementCode}.");
                else if (!steps.Contains(c.MovementCode))
                    errors.Add(i, "movementCode", $"{c.MovementCode} is not a step of this order type, so the charge would never be raised.");
            }
            // Mirrors ck_otc__default_optional: a charge cannot be both raised by
            // default and offered as an option.
            if (c is { IsDefault: true, IsOptional: true })
                errors.Add(i, null, $"'{c.ChargeCode}' is both default and optional. Pick one.");
            // uq_otc: one row per (charge, step, payer).
            if (!seen.Add((c.ChargeCode, c.MovementCode, c.PaymentTo)))
                errors.Add(i, null, $"{c.ChargeCode} to {c.PaymentTo} on {c.MovementCode ?? "every step"} is already a row above.");
        }
        if (errors.Count > 0) return errors.Problem();

        if (db.TouchParent(orderType, request.RowVersion) is { } missing) return missing;

        // One transaction: the soft deletes flush (past the filtered unique indexes)
        // with the version-guarded touch of the order type, so a stale editor is
        // refused before anything changes and a failed insert loses nothing.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.OrderTypeCharges.Where(c => c.OrderTypeId == orderType.OrderTypeId).ToListAsync(ct);
        foreach (var gone in existing) db.OrderTypeCharges.Remove(gone);
        if (await db.SaveOrConflictAsync(ct) is { } stale) return stale;

        foreach (var c in wanted)
        {
            db.OrderTypeCharges.Add(new OrderTypeCharge
            {
                TenantId = caller.TenantId(),
                OrderTypeId = orderType.OrderTypeId,
                ChargeCodeId = charges.Single(x => x.ChargeCode1 == c.ChargeCode).ChargeCodeId,
                MovementId = c.MovementCode is null ? null : movements.Single(x => x.MovementCode == c.MovementCode).MovementId,
                PaymentTo = c.PaymentTo,
                PaymentTermCode = c.PaymentTermCode?.ToUpperInvariant(),
                IsDefault = c.IsDefault,
                IsOptional = c.IsOptional,
                IsCargoCharge = c.IsCargoCharge,
                IsValueAddedService = c.IsValueAddedService,
                RaiseAtGateIn = c.RaiseAtGateIn,
                DefaultQty = c.DefaultQty,
            });
        }

        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await tx.CommitAsync(ct);
        return TypedResults.Ok(await DetailAsync(db, orderType.OrderTypeId, ct));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> DeleteAsync(
        string orderTypeCode, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var orderType = await db.OrderTypes.SingleOrDefaultAsync(o => o.OrderTypeCode == orderTypeCode.FromRouteCode(), ct);
        if (orderType is null) return TypedResults.NotFound();
        if (db.ExpectVersion(orderType, rowVersion) is { } missing) return missing;

        foreach (var m in await db.OrderTypeMovements.Where(m => m.OrderTypeId == orderType.OrderTypeId).ToListAsync(ct))
            db.OrderTypeMovements.Remove(m);
        foreach (var c in await db.OrderTypeCharges.Where(c => c.OrderTypeId == orderType.OrderTypeId).ToListAsync(ct))
            db.OrderTypeCharges.Remove(c);
        // A haulier's term for this order type's charges goes with them (gecko_master 22).
        foreach (var t in await db.HaulierChargeTerms.Where(t => t.OrderTypeId == orderType.OrderTypeId).ToListAsync(ct))
            db.HaulierChargeTerms.Remove(t);
        db.OrderTypes.Remove(orderType);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        return TypedResults.NoContent();
    }

    private static async Task ApplyAsync(MasterDataDbContext db, OrderType orderType, SaveOrderTypeRequest request, CancellationToken ct)
    {
        orderType.DescriptionEn = request.DescriptionEn;
        orderType.DescriptionLocal = request.DescriptionLocal;
        orderType.BookingTypeCode = request.BookingTypeCode?.ToUpperInvariant();
        orderType.DirectionCode = request.DirectionCode?.ToUpperInvariant()
                                  ?? DirectionOf(orderType.BookingTypeCode, orderType.DirectionCode)
                                  ?? (string.IsNullOrEmpty(orderType.DirectionCode) ? "DOMESTIC" : orderType.DirectionCode);
        orderType.CargoClassCode = request.CargoClassCode.ToUpperInvariant();
        if (request.RequiresVesselSchedule is { } requires) orderType.RequiresVesselSchedule = requires;
        if (request.Rules is { } rules)
        {
            orderType.IsWalkIn = rules.IsWalkIn;
            orderType.PaymentRule = Upper(rules.PaymentRule);
            orderType.ReleaseGradeCodes = GradeList(rules.ReleaseGradeCodes) is { Count: > 0 } grades ? string.Join(",", grades) : null;
            orderType.OwnerMatch = Upper(rules.OwnerMatch);
            orderType.MaxBoxesPerTruck = rules.MaxBoxesPerTruck;
        }
        orderType.IsActive = request.IsActive;
        orderType.ServiceTypeId = request.ServiceCode is null
            ? null
            : await db.ServiceTypes.Where(s => s.ServiceCode == request.ServiceCode.ToUpperInvariant())
                .Select(s => (Guid?)s.ServiceTypeId).SingleOrDefaultAsync(ct);
    }

    /// <summary>
    /// The direction the gate and the handover-mode lists read, derived from the booking type
    /// (owner 2026-10-04: no longer typed on the order type form). Vector's four: EXPORT and IMPORT
    /// are themselves; REPO and INTERNAL are DOMESTIC, except that an order type already
    /// INTRA_TERMINAL (a yard move) stays so.
    /// </summary>
    private static string? DirectionOf(string? bookingTypeCode, string? currentDirection) => bookingTypeCode switch
    {
        "EXPORT" => "EXPORT",
        "IMPORT" => "IMPORT",
        null => null,
        _ => currentDirection == "INTRA_TERMINAL" ? "INTRA_TERMINAL" : "DOMESTIC",
    };

    private static async Task<ValidationProblem?> ValidateAsync(
        MasterDataDbContext db, SaveOrderTypeRequest request, CancellationToken ct)
    {
        if (request.DirectionCode?.ToUpperInvariant() is { } direction
            && !await db.DirectionTypes.AnyAsync(d => d.Code == direction && d.IsActive, ct))
            return MasterDataSupport.InvalidReference("directionCode", $"Unknown direction '{direction}'.");

        var cargoClass = request.CargoClassCode.ToUpperInvariant();
        if (!await db.CargoClasses.AnyAsync(c => c.Code == cargoClass && c.IsActive, ct))
            return MasterDataSupport.InvalidReference("cargoClassCode", $"Unknown cargo class '{cargoClass}'.");

        if (request.ServiceCode is not null)
        {
            var service = request.ServiceCode.ToUpperInvariant();
            if (!await db.ServiceTypes.AnyAsync(s => s.ServiceCode == service, ct))
                return MasterDataSupport.InvalidReference("serviceCode", $"Unknown service type '{service}'.");
        }

        if (request.BookingTypeCode is not null)
        {
            var booking = request.BookingTypeCode.ToUpperInvariant();
            if (!await db.VwCodeLists.AnyAsync(v => v.CategoryCode == "BOOKING_TYPE" && v.Code == booking && v.IsActive == true, ct))
                return MasterDataSupport.InvalidReference("bookingTypeCode", $"'{booking}' is not a BOOKING_TYPE.");
        }

        return null;
    }

    private static readonly string[] PaymentRules = ["CASH_ONLY", "CREDIT_FOR_CREDIT_CUSTOMERS", "CREDIT_WITHIN_LIMIT"];
    private static readonly string[] OwnerMatches = ["REFUSE", "ALLOW"];

    private static string? Upper(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static List<string> GradeList(IReadOnlyList<string>? codes) =>
        (codes ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim().ToUpperInvariant()).Distinct().Order().ToList();

    /// <summary>
    /// The Rules panel (gecko_master 34). <paramref name="orderType"/> is null on create (no steps yet). A walk-in order
    /// type has at most one step, and no other active walk-in order type may have a step of the same direction and
    /// full/empty: the gate finds the order type by those two answers, so two would be a coin toss.
    /// </summary>
    private static async Task<ValidationProblem?> ValidateRulesAsync(MasterDataDbContext db, OrderType? orderType, OrderTypeRulesItem rules, CancellationToken ct)
    {
        if (Upper(rules.PaymentRule) is { } payment && !PaymentRules.Contains(payment))
            return MasterDataSupport.InvalidReference("rules.paymentRule", $"'{payment}' is not a payment rule: {string.Join(", ", PaymentRules)}, or none.");
        if (Upper(rules.OwnerMatch) is { } owner && !OwnerMatches.Contains(owner))
            return MasterDataSupport.InvalidReference("rules.ownerMatch", $"'{owner}' is not REFUSE or ALLOW (or none: the tenant setting).");
        var grades = GradeList(rules.ReleaseGradeCodes);
        if (grades.Count > 0)
        {
            var known = await db.ContainerGrades.AsNoTracking().Where(g => grades.Contains(g.GradeCode) && g.IsActive).Select(g => g.GradeCode).ToListAsync(ct);
            if (grades.Except(known, StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } unknown)
                return MasterDataSupport.InvalidReference("rules.releaseGradeCodes", $"Unknown container grade(s): {string.Join(", ", unknown)}.");
            if (string.Join(",", grades).Length > 200)
                return MasterDataSupport.InvalidReference("rules.releaseGradeCodes", "Too many grades: the list holds up to 200 characters.");
        }
        if (!rules.IsWalkIn || orderType is null) return null;

        var steps = await db.OrderTypeMovements.AsNoTracking().Where(m => m.OrderTypeId == orderType.OrderTypeId).Select(m => m.MovementId).ToListAsync(ct);
        if (steps.Count > 1)
            return MasterDataSupport.InvalidReference("rules.isWalkIn",
                $"{orderType.OrderTypeCode} has {steps.Count} steps. A walk-in order type has exactly one (the truck's drop-off or pick-up).");
        if (steps.Count == 1 && await WalkInStepProblemAsync(db, orderType.OrderTypeId, steps[0], ct) is { } taken)
            return MasterDataSupport.InvalidReference("rules.isWalkIn", taken);
        return null;
    }

    /// <summary>
    /// Why this step cannot be a walk-in order type's, as a message; null when it can: the step must be a gate drop-off or
    /// pick-up of a full or an empty box, and no other active walk-in order type may have one that goes the same way with the same load.
    /// </summary>
    private static async Task<string?> WalkInStepProblemAsync(MasterDataDbContext db, Guid orderTypeId, Guid movementId, CancellationToken ct)
    {
        var mine = await db.Movements.AsNoTracking().Where(m => m.MovementId == movementId).Select(m => new { m.MovementCode, m.Direction, m.FullEmpty }).SingleAsync(ct);
        if (mine.Direction is not ("IN" or "OUT") || mine.FullEmpty is not ("FULL" or "EMPTY"))
            return $"{mine.MovementCode} is not a gate drop-off or pick-up of a full or an empty box, so a walk-in truck cannot use it.";
        var other = await (
            from o in db.OrderTypes.AsNoTracking()
            join s in db.OrderTypeMovements on o.OrderTypeId equals s.OrderTypeId
            join m in db.Movements on s.MovementId equals m.MovementId
            where o.OrderTypeId != orderTypeId && o.IsWalkIn && o.IsActive && m.Direction == mine.Direction && m.FullEmpty == mine.FullEmpty
            select o.OrderTypeCode).FirstOrDefaultAsync(ct);
        return other is null ? null
            : $"{other} is already the walk-in order type for a {mine.FullEmpty} {(mine.Direction == "IN" ? "drop-off" : "pick-up")}. " +
              "The gate picks the order type by those two answers, so only one may be active.";
    }

    private static async Task<IReadOnlyList<OrderTypeMovementResponse>> MovementsOfAsync(
        MasterDataDbContext db, Guid orderTypeId, CancellationToken ct) =>
        await (
            from otm in db.OrderTypeMovements.AsNoTracking().Where(m => m.OrderTypeId == orderTypeId)
            join m in db.Movements on otm.MovementId equals m.MovementId
            orderby otm.SequenceNo
            select new OrderTypeMovementResponse(
                otm.OrderTypeMovementId, otm.MovementId, m.MovementCode, m.DescriptionEn,
                otm.SequenceNo, otm.IsRequired, otm.IsBillable,
                otm.CheckSealNo, otm.CheckGrossWeight, otm.RequireVesselVoyage,
                otm.AllowDamagedRelease, otm.SkipEdi, otm.PudoMode, otm.RequireSurvey, otm.SealMustMatch)
        ).ToListAsync(ct);

    private static async Task<IReadOnlyList<OrderTypeChargeResponse>> ChargesOfAsync(
        MasterDataDbContext db, Guid orderTypeId, CancellationToken ct) =>
        await (
            from otc in db.OrderTypeCharges.AsNoTracking().Where(c => c.OrderTypeId == orderTypeId)
            join cc in db.ChargeCodes on otc.ChargeCodeId equals cc.ChargeCodeId
            join mv in db.Movements on otc.MovementId equals mv.MovementId into movements
            from mv in movements.DefaultIfEmpty()
            orderby cc.ChargeCode1
            select new OrderTypeChargeResponse(
                otc.OrderTypeChargeId, otc.ChargeCodeId, cc.ChargeCode1, cc.DescriptionEn,
                otc.MovementId, mv == null ? null : mv.MovementCode,
                otc.PaymentTo, otc.PaymentTermCode, otc.IsDefault, otc.IsOptional,
                otc.IsCargoCharge, otc.IsValueAddedService, otc.RaiseAtGateIn, otc.DefaultQty)
        ).ToListAsync(ct);

    private static IQueryable<OrderTypeResponse> Project(MasterDataDbContext db, IQueryable<OrderType> orderTypes) =>
        from o in orderTypes
        join s in db.ServiceTypes on o.ServiceTypeId equals s.ServiceTypeId into services
        from s in services.DefaultIfEmpty()
        select new OrderTypeResponse(
            o.OrderTypeId, o.OrderTypeCode, o.DescriptionEn, o.DescriptionLocal, o.DirectionCode,
            o.ServiceTypeId, s == null ? null : s.ServiceCode, o.CargoClassCode, o.BookingTypeCode,
            o.IsActive, Convert.ToBase64String(o.RowVersion), o.RequiresVesselSchedule,
            new OrderTypeRulesItem(o.IsWalkIn, o.PaymentRule,
                o.ReleaseGradeCodes == null ? Array.Empty<string>() : o.ReleaseGradeCodes.Split(',', StringSplitOptions.None),
                o.OwnerMatch, o.MaxBoxesPerTruck));
}
