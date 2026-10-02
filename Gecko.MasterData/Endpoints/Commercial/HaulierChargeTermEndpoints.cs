using System.ComponentModel.DataAnnotations;
using Gecko.Data;
using Gecko.MasterData.Infrastructure.Persistence;
using Gecko.MasterData.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using static Gecko.MasterData.Endpoints.Logistics.LogisticsSupport;

namespace Gecko.MasterData.Endpoints.Commercial;

public sealed record HaulierChargeTermResponse(
    Guid HaulierChargeTermId, string HaulierCode, string? HaulierName, string OrderTypeCode,
    string MovementCode, string ChargeCode, string PaymentTermCode, string RowVersion);

public sealed record SaveHaulierChargeTermRequest(
    [property: Required, MaxLength(25)] string HaulierCode,
    [property: Required, MaxLength(50)] string OrderTypeCode,
    [property: Required, MaxLength(20)] string MovementCode,
    [property: Required, MaxLength(15)] string ChargeCode,
    [property: Required, AllowedValues("CASH", "CREDIT")] string PaymentTermCode,
    string? RowVersion = null);

/// <summary>
/// Haulier charge terms (commercial.haulier_charge_term, gecko_master 22) — Vector
/// Master.HaulierChargeTerm. A haulier's CASH / CREDIT term for one charge of one
/// movement of one order type; at the gate it REPLACES the charge line's own term
/// (gate-in-vector-parity.md §3.3, GateIn.cs:1316 / 1508). It is commercial
/// master data, so it rides on mdm.commercial.view / manage like order types and
/// charge codes.
///
/// The gate's read is the list filtered by haulierCode AND orderTypeCode; other
/// modules call <c>IMasterDataReferences.HaulierChargeTermsAsync</c>.
/// </summary>
internal static class HaulierChargeTermEndpoints
{
    public static RouteGroupBuilder MapHaulierChargeTermEndpoints(this RouteGroupBuilder master)
    {
        var terms = master.MapGroup("/haulier-charge-terms").WithTags("Master data — haulier charge terms");
        terms.MapGet("/", ListAsync).RequirePermission(MasterDataPermissions.CommercialView)
            .WithSummary("List haulier charge terms — by haulierCode and orderTypeCode for the gate");
        terms.MapGet("/{haulierChargeTermId:guid}", GetAsync).RequirePermission(MasterDataPermissions.CommercialView)
            .WithName("GetHaulierChargeTerm").WithSummary("One haulier charge term");
        terms.MapPost("/", CreateAsync).RequirePermission(MasterDataPermissions.CommercialManage)
            .Validate<SaveHaulierChargeTermRequest>().WithSummary("Give a haulier its own payment term for a charge");
        terms.MapPut("/{haulierChargeTermId:guid}", UpdateAsync).RequirePermission(MasterDataPermissions.CommercialManage)
            .Validate<SaveHaulierChargeTermRequest>().WithSummary("Update a haulier charge term (optimistic concurrency on rowVersion)");
        terms.MapDelete("/{haulierChargeTermId:guid}", DeleteAsync).RequirePermission(MasterDataPermissions.CommercialManage)
            .WithSummary("Soft-delete a haulier charge term (?rowVersion=)");
        return master;
    }

    private static IQueryable<HaulierChargeTermResponse> Rows(MasterDataDbContext db, IQueryable<HaulierChargeTerm> terms) =>
        from t in terms
        select new HaulierChargeTermResponse(
            t.HaulierChargeTermId, t.HaulierPartyCode,
            db.Parties.Where(p => p.PartyId == t.HaulierPartyId).Select(p => p.NameEn).FirstOrDefault(),
            t.OrderTypeCode, t.MovementCode, t.ChargeCode, t.PaymentTermCode, Convert.ToBase64String(t.RowVersion));

    private static async Task<Ok<IReadOnlyList<HaulierChargeTermResponse>>> ListAsync(
        MasterDataDbContext db, CancellationToken ct,
        string? haulierCode = null, string? orderTypeCode = null, string? movementCode = null, string? chargeCode = null)
    {
        var terms = db.HaulierChargeTerms.AsNoTracking();
        if (Upper(haulierCode) is { } h) terms = terms.Where(t => t.HaulierPartyCode == h);
        if (Upper(orderTypeCode) is { } o) terms = terms.Where(t => t.OrderTypeCode == o);
        if (Upper(movementCode) is { } m) terms = terms.Where(t => t.MovementCode == m);
        if (Upper(chargeCode) is { } c) terms = terms.Where(t => t.ChargeCode == c);
        return TypedResults.Ok<IReadOnlyList<HaulierChargeTermResponse>>(await Rows(db, terms
                .OrderBy(t => t.HaulierPartyCode).ThenBy(t => t.OrderTypeCode).ThenBy(t => t.MovementCode).ThenBy(t => t.ChargeCode))
            .ToListAsync(ct));
    }

    private static async Task<Results<Ok<HaulierChargeTermResponse>, NotFound>> GetAsync(
        Guid haulierChargeTermId, MasterDataDbContext db, CancellationToken ct) =>
        await Rows(db, db.HaulierChargeTerms.AsNoTracking().Where(t => t.HaulierChargeTermId == haulierChargeTermId))
            .SingleOrDefaultAsync(ct) is { } row
            ? TypedResults.Ok(row)
            : TypedResults.NotFound();

    private static async Task<Results<Created<HaulierChargeTermResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SaveHaulierChargeTermRequest request, MasterDataDbContext db, ITenantContext caller, CancellationToken ct)
    {
        var (refs, invalid) = await ValidateAsync(db, request, ct);
        if (invalid is not null) return invalid;
        if (await DuplicateAsync(db, refs!, null, ct) is { } duplicate) return duplicate;

        var term = new HaulierChargeTerm { TenantId = caller.TenantId() };
        Apply(term, refs!, request);
        db.HaulierChargeTerms.Add(term);
        await db.SaveChangesAsync(ct);
        var saved = await Rows(db, db.HaulierChargeTerms.AsNoTracking().Where(t => t.HaulierChargeTermId == term.HaulierChargeTermId)).SingleAsync(ct);
        return TypedResults.Created($"/api/master/haulier-charge-terms/{term.HaulierChargeTermId}", saved);
    }

    private static async Task<Results<Ok<HaulierChargeTermResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid haulierChargeTermId, SaveHaulierChargeTermRequest request, MasterDataDbContext db, CancellationToken ct)
    {
        var term = await db.HaulierChargeTerms.SingleOrDefaultAsync(t => t.HaulierChargeTermId == haulierChargeTermId, ct);
        if (term is null) return TypedResults.NotFound();
        if (db.ExpectVersion(term, request.RowVersion) is { } missing) return missing;

        var (refs, invalid) = await ValidateAsync(db, request, ct);
        if (invalid is not null) return invalid;
        if (await DuplicateAsync(db, refs!, haulierChargeTermId, ct) is { } duplicate) return duplicate;

        Apply(term, refs!, request);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(await Rows(db, db.HaulierChargeTerms.AsNoTracking().Where(t => t.HaulierChargeTermId == haulierChargeTermId)).SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> DeleteAsync(
        Guid haulierChargeTermId, string? rowVersion, MasterDataDbContext db, CancellationToken ct)
    {
        var term = await db.HaulierChargeTerms.SingleOrDefaultAsync(t => t.HaulierChargeTermId == haulierChargeTermId, ct);
        if (term is null) return TypedResults.NotFound();
        if (db.ExpectVersion(term, rowVersion) is { } missing) return missing;
        db.HaulierChargeTerms.Remove(term);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private sealed record Refs(
        Guid HaulierId, string HaulierCode, Guid OrderTypeId, string OrderTypeCode,
        Guid MovementId, string MovementCode, Guid ChargeCodeId, string ChargeCode);

    /// <summary>
    /// The soft references (no FKs in gecko_master), every bad field in one 400:
    /// a party with the HAULIER role, an active order type, an active movement that
    /// is one of the order type's steps, and an active charge the order type raises
    /// at that movement or at order level (commercial.order_type_charge, movement_id NULL).
    /// </summary>
    private static async Task<(Refs? Refs, ValidationProblem? Invalid)> ValidateAsync(
        MasterDataDbContext db, SaveHaulierChargeTermRequest r, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        string haulierCode = Upper(r.HaulierCode)!, orderTypeCode = Upper(r.OrderTypeCode)!,
            movementCode = Upper(r.MovementCode)!, chargeCode = Upper(r.ChargeCode)!;

        var haulier = await db.Parties.AsNoTracking().Where(p => p.PartyCode == haulierCode)
            .Select(p => new { p.PartyId, p.IsActive, IsHaulier = db.HaulierExtensions.Any(x => x.PartyId == p.PartyId) })
            .SingleOrDefaultAsync(ct);
        if (haulier is null) errors["haulierCode"] = [$"There is no party '{haulierCode}'."];
        else if (!haulier.IsHaulier) errors["haulierCode"] = [$"{haulierCode} does not hold the HAULIER role."];
        else if (!haulier.IsActive) errors["haulierCode"] = [$"{haulierCode} is inactive."];

        var orderType = await db.OrderTypes.AsNoTracking().Where(o => o.OrderTypeCode == orderTypeCode)
            .Select(o => new { o.OrderTypeId, o.IsActive }).SingleOrDefaultAsync(ct);
        if (orderType is null) errors["orderTypeCode"] = [$"There is no order type '{orderTypeCode}'."];
        else if (!orderType.IsActive) errors["orderTypeCode"] = [$"Order type {orderTypeCode} is inactive."];

        var movement = await db.Movements.AsNoTracking().Where(m => m.MovementCode == movementCode)
            .Select(m => new { m.MovementId, m.IsActive }).SingleOrDefaultAsync(ct);
        if (movement is null) errors["movementCode"] = [$"There is no movement '{movementCode}'."];
        else if (!movement.IsActive) errors["movementCode"] = [$"Movement {movementCode} is inactive."];
        else if (orderType is not null
                 && !await db.OrderTypeMovements.AsNoTracking().AnyAsync(s => s.OrderTypeId == orderType.OrderTypeId && s.MovementId == movement.MovementId, ct))
            errors["movementCode"] = [$"{movementCode} is not a step of order type {orderTypeCode}."];

        var charge = await db.ChargeCodes.AsNoTracking().Where(c => c.ChargeCode1 == chargeCode)
            .Select(c => new { c.ChargeCodeId, c.IsActive }).SingleOrDefaultAsync(ct);
        if (charge is null) errors["chargeCode"] = [$"There is no charge code '{chargeCode}'."];
        else if (!charge.IsActive) errors["chargeCode"] = [$"Charge code {chargeCode} is inactive."];
        else if (orderType is not null && movement is not null
                 && !await db.OrderTypeCharges.AsNoTracking().AnyAsync(c =>
                     c.OrderTypeId == orderType.OrderTypeId && c.ChargeCodeId == charge.ChargeCodeId
                     && (c.MovementId == movement.MovementId || c.MovementId == null), ct))
            errors["chargeCode"] = [$"Order type {orderTypeCode} does not raise {chargeCode} at {movementCode}."];

        if (Problem(errors) is { } problem) return (null, problem);
        return (new Refs(haulier!.PartyId, haulierCode, orderType!.OrderTypeId, orderTypeCode,
            movement!.MovementId, movementCode, charge!.ChargeCodeId, chargeCode), null);
    }

    private static async Task<ProblemHttpResult?> DuplicateAsync(MasterDataDbContext db, Refs k, Guid? self, CancellationToken ct) =>
        await db.HaulierChargeTerms.AsNoTracking().AnyAsync(t =>
            t.HaulierPartyId == k.HaulierId && t.OrderTypeId == k.OrderTypeId && t.MovementId == k.MovementId
            && t.ChargeCodeId == k.ChargeCodeId && t.HaulierChargeTermId != (self ?? Guid.Empty), ct)
            ? MasterDataSupport.Conflict(
                $"{k.HaulierCode} already has a term for {k.ChargeCode} at {k.MovementCode} of {k.OrderTypeCode}.",
                "Edit that term instead.")
            : null;

    private static void Apply(HaulierChargeTerm t, Refs k, SaveHaulierChargeTermRequest r)
    {
        t.HaulierPartyId = k.HaulierId;
        t.HaulierPartyCode = k.HaulierCode;
        t.OrderTypeId = k.OrderTypeId;
        t.OrderTypeCode = k.OrderTypeCode;
        t.MovementId = k.MovementId;
        t.MovementCode = k.MovementCode;
        t.ChargeCodeId = k.ChargeCodeId;
        t.ChargeCode = k.ChargeCode;
        t.PaymentTermCode = r.PaymentTermCode.Trim().ToUpperInvariant();
    }
}
