using Gecko.Data;
using Gecko.Revenue.Domain;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Endpoints.Tariffs;

/// <summary>
/// A tariff's rate table and free-time rules, each REPLACED AS A SET.
///
/// Why not row-by-row PATCH: uq_tos_rate__signature makes a table of rates a
/// set with an invariant ("no two rows the resolver cannot choose between"),
/// and a partial edit discovers a clash halfway. Sending the whole intended
/// table lets it be checked once — the same reason MasterData replaces charge
/// variants and gate rules wholesale, and the shape the Excel import will send.
///
/// Both PUTs are guarded by the SCHEDULE's rowVersion and move it on success,
/// so two people editing the same draft cannot silently overwrite each other,
/// and a downloaded Excel template can tell that the tariff changed under it.
/// </summary>
internal static class RateEndpoints
{
    public static RouteGroupBuilder MapRateEndpoints(this RouteGroupBuilder revenue)
    {
        var tariffs = revenue.MapGroup("/tariffs").WithTags("Revenue — tariffs");

        tariffs.MapGet("/{scheduleId:guid}/rates", GetRatesAsync).RequirePermission(RevenuePermissions.TariffView).WithSummary("The tariff's rate table, with tiers and surcharge conditions");
        tariffs.MapPut("/{scheduleId:guid}/rates", ReplaceRatesAsync).RequirePermission(RevenuePermissions.TariffManage).Validate<ReplaceRatesRequest>().WithSummary("Replace the whole rate table of a DRAFT tariff");
        tariffs.MapGet("/{scheduleId:guid}/free-time", GetFreeTimeAsync).RequirePermission(RevenuePermissions.TariffView).WithSummary("The tariff's free-time rules");
        tariffs.MapPut("/{scheduleId:guid}/free-time", ReplaceFreeTimeAsync).RequirePermission(RevenuePermissions.TariffManage).Validate<ReplaceFreeTimeRequest>().WithSummary("Replace the free-time rules of a DRAFT tariff");

        return revenue;
    }

    // ── rates ───────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<RateSetResponse>, NotFound>> GetRatesAsync(
        Guid scheduleId, RevenueDbContext db, CancellationToken ct)
    {
        var schedule = await db.Schedules.AsNoTracking().SingleOrDefaultAsync(s => s.ScheduleId == scheduleId, ct);
        if (schedule is null) return TypedResults.NotFound();
        return TypedResults.Ok(await RateSetOfAsync(db, schedule, ct));
    }

    private static async Task<Results<Ok<RateSetResponse>, NotFound, ValidationProblem, ProblemHttpResult>> ReplaceRatesAsync(
        Guid scheduleId, ReplaceRatesRequest request, RevenueDbContext db, RateSetValidator validator,
        TimeProvider clock, CancellationToken ct)
    {
        var schedule = await db.Schedules.SingleOrDefaultAsync(s => s.ScheduleId == scheduleId, ct);
        if (schedule is null) return TypedResults.NotFound();
        if (ScheduleEndpoints.NotEditable(schedule) is { } locked) return locked;
        if (!db.TrySetExpectedVersion(schedule, request.RowVersion))
            return RevenueSupport.Invalid("rowVersion", "Send the tariff's rowVersion.");

        var result = await validator.ValidateAsync(schedule, request.Rates, ct);
        if (!result.IsValid) return RevenueSupport.Invalid(result.Errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Soft deletes first and flushed: the filtered unique index only ignores
        // a replaced row once its deleted_at is actually written.
        await RateSetWriter.RemoveRatesAsync(db, scheduleId, ct);
        schedule.UpdatedAt = clock.GetUtcNow();   // moves the schedule's rowVersion
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        RateSetWriter.Add(db, result.Rates);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return TypedResults.Ok(await RateSetOfAsync(db, schedule, ct));
    }

    private static async Task<RateSetResponse> RateSetOfAsync(RevenueDbContext db, Schedule schedule, CancellationToken ct)
    {
        var rates = await db.TosRates.AsNoTracking()
            .Where(r => r.ScheduleId == schedule.ScheduleId)
            .OrderBy(r => r.ChargeCode).ThenBy(r => r.BillTo).ThenBy(r => r.PaymentTermCode)
            .ThenByDescending(r => r.Specificity).ThenBy(r => r.AxisSignature)
            .ToListAsync(ct);
        var rateIds = rates.Select(r => r.TosRateId).ToList();

        var tiers = (await db.RateTiers.AsNoTracking()
                .Where(t => t.OwnerType == "TOS_RATE" && rateIds.Contains(t.OwnerId))
                .ToListAsync(ct))
            .ToLookup(t => t.OwnerId);
        var conditions = await db.RateConditions.AsNoTracking()
            .Where(c => c.OwnerType == "TOS_RATE" && rateIds.Contains(c.OwnerId))
            .ToListAsync(ct);
        var conditionIds = conditions.Select(c => c.RateConditionId).ToList();
        var values = (await db.RateConditionValues.AsNoTracking()
                .Where(v => conditionIds.Contains(v.RateConditionId))
                .ToListAsync(ct))
            .ToLookup(v => v.RateConditionId, v => v.ValueCode);
        var conditionsByRate = conditions.ToLookup(c => c.OwnerId);

        var rowVersion = await db.Schedules.AsNoTracking()
            .Where(s => s.ScheduleId == schedule.ScheduleId).Select(s => s.RowVersion).SingleAsync(ct);

        return new RateSetResponse(schedule.ScheduleId, schedule.Status, Convert.ToBase64String(rowVersion),
            rates.Select(r => new RateResponse(
                r.TosRateId, r.ChargeCode, r.BillTo, r.PaymentTermCode, r.CreditTermDays,
                r.OrderTypeCode, r.MovementCode, r.EquipmentTypeCode, r.EquipmentSize,
                r.CargoCategoryCode, r.TruckCategoryCode, r.BillingUnitCode,
                r.PricingMethod, r.TierBasis, r.Rate, r.Specificity ?? 0, r.Source,
                tiers[r.TosRateId].OrderBy(t => t.FromQty).Select(t => new TierItem(t.FromQty, t.ToQty, t.Rate)).ToList(),
                conditionsByRate[r.TosRateId].OrderBy(c => c.SequenceNo)
                    .Select(c => new ConditionResponse(c.SequenceNo, c.Axis, c.Op, values[c.RateConditionId].Order().ToList(),
                        c.NumberValue, c.BoolValue, c.ModifierOp, c.ModifierValue, c.Label))
                    .ToList()))
            .ToList());
    }

    // ── free time ───────────────────────────────────────────────────────────

    private static async Task<Results<Ok<FreeTimeSetResponse>, NotFound>> GetFreeTimeAsync(
        Guid scheduleId, RevenueDbContext db, CancellationToken ct)
    {
        var schedule = await db.Schedules.AsNoTracking().SingleOrDefaultAsync(s => s.ScheduleId == scheduleId, ct);
        if (schedule is null) return TypedResults.NotFound();
        return TypedResults.Ok(await FreeTimeOfAsync(db, schedule, ct));
    }

    private static async Task<Results<Ok<FreeTimeSetResponse>, NotFound, ValidationProblem, ProblemHttpResult>> ReplaceFreeTimeAsync(
        Guid scheduleId, ReplaceFreeTimeRequest request, RevenueDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var schedule = await db.Schedules.SingleOrDefaultAsync(s => s.ScheduleId == scheduleId, ct);
        if (schedule is null) return TypedResults.NotFound();
        if (ScheduleEndpoints.NotEditable(schedule) is { } locked) return locked;
        if (!db.TrySetExpectedVersion(schedule, request.RowVersion))
            return RevenueSupport.Invalid("rowVersion", "Send the tariff's rowVersion.");

        // NULL means "any" in every dimension, so two rules with the same
        // dimensions contradict each other whatever their numbers.
        var duplicates = request.Rules
            .GroupBy(r => (r.FreeTimeKind, r.FullEmpty, r.Direction, r.CargoGroup, r.EquipmentSize))
            .Where(g => g.Count() > 1)
            .Select(g => string.Join("/", new[] { g.Key.FreeTimeKind, g.Key.FullEmpty ?? "any", g.Key.Direction ?? "any", g.Key.CargoGroup ?? "any", g.Key.EquipmentSize ?? "any" }))
            .ToList();
        if (duplicates.Count > 0)
            return RevenueSupport.Invalid("rules", $"Stated twice: {string.Join(", ", duplicates)}.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.FreeTimeRules.RemoveRange(await db.FreeTimeRules.Where(f => f.ScheduleId == scheduleId).ToListAsync(ct));
        schedule.UpdatedAt = clock.GetUtcNow();
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        db.FreeTimeRules.AddRange(request.Rules.Select(r => new FreeTimeRule
        {
            FreeTimeRuleId = Guid.CreateVersion7(),
            TenantId = schedule.TenantId,
            ScheduleId = scheduleId,
            FreeTimeKind = r.FreeTimeKind,
            FullEmpty = r.FullEmpty,
            Direction = r.Direction,
            CargoGroup = r.CargoGroup,
            EquipmentSize = r.EquipmentSize,
            FreeUnits = r.FreeUnits,
            Unit = FreeTimeKinds.UnitFor(r.FreeTimeKind),
        }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return TypedResults.Ok(await FreeTimeOfAsync(db, schedule, ct));
    }

    private static async Task<FreeTimeSetResponse> FreeTimeOfAsync(RevenueDbContext db, Schedule schedule, CancellationToken ct)
    {
        var rules = await db.FreeTimeRules.AsNoTracking()
            .Where(f => f.ScheduleId == schedule.ScheduleId)
            .OrderBy(f => f.FreeTimeKind).ThenBy(f => f.FullEmpty).ThenBy(f => f.Direction).ThenBy(f => f.CargoGroup)
            .Select(f => new FreeTimeResponse(f.FreeTimeKind, f.FullEmpty, f.Direction, f.CargoGroup, f.EquipmentSize, f.FreeUnits, f.Unit))
            .ToListAsync(ct);
        var rowVersion = await db.Schedules.AsNoTracking()
            .Where(s => s.ScheduleId == schedule.ScheduleId).Select(s => s.RowVersion).SingleAsync(ct);
        return new FreeTimeSetResponse(schedule.ScheduleId, schedule.Status, Convert.ToBase64String(rowVersion), rules);
    }
}
