using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Endpoints.Tariffs;

/// <summary>
/// Replace-as-set for a schedule's rates. Remove() is a soft delete (the
/// AuditStampInterceptor rewrites it; DELETE is denied to the login anyway),
/// and system versioning keeps every replaced row in history.tariff_*.
/// </summary>
internal static class RateSetWriter
{
    public static async Task RemoveRatesAsync(RevenueDbContext db, Guid scheduleId, CancellationToken ct)
    {
        var rates = await db.TosRates.Where(r => r.ScheduleId == scheduleId).ToListAsync(ct);
        var rateIds = rates.Select(r => r.TosRateId).ToList();

        var conditions = await db.RateConditions.Where(c => c.OwnerType == "TOS_RATE" && rateIds.Contains(c.OwnerId)).ToListAsync(ct);
        var conditionIds = conditions.Select(c => c.RateConditionId).ToList();

        db.RateConditionValues.RemoveRange(await db.RateConditionValues.Where(v => conditionIds.Contains(v.RateConditionId)).ToListAsync(ct));
        db.RateConditions.RemoveRange(conditions);
        db.RateTiers.RemoveRange(await db.RateTiers.Where(t => t.OwnerType == "TOS_RATE" && rateIds.Contains(t.OwnerId)).ToListAsync(ct));
        db.TosRates.RemoveRange(rates);
    }

    public static void Add(RevenueDbContext db, IEnumerable<RateSetValidator.ValidRate> rates)
    {
        foreach (var valid in rates)
        {
            db.TosRates.Add(valid.Rate);
            db.RateTiers.AddRange(valid.Tiers);
            foreach (var (condition, values) in valid.Conditions)
            {
                db.RateConditions.Add(condition);
                db.RateConditionValues.AddRange(values.Select(v => new RateConditionValue
                {
                    RateConditionValueId = Guid.CreateVersion7(),
                    TenantId = condition.TenantId,
                    RateConditionId = condition.RateConditionId,
                    ValueCode = v,
                }));
            }
        }
    }

    /// <summary>Copies every rate, tier, condition and free-time rule of one schedule onto another (a revision).</summary>
    public static async Task CopyAsync(RevenueDbContext db, Guid fromScheduleId, Schedule to, CancellationToken ct)
    {
        var rates = await db.TosRates.AsNoTracking().Where(r => r.ScheduleId == fromScheduleId).ToListAsync(ct);
        var rateIds = rates.Select(r => r.TosRateId).ToList();
        var tiers = (await db.RateTiers.AsNoTracking()
            .Where(t => t.OwnerType == "TOS_RATE" && rateIds.Contains(t.OwnerId)).ToListAsync(ct)).ToLookup(t => t.OwnerId);
        var conditions = (await db.RateConditions.AsNoTracking()
            .Where(c => c.OwnerType == "TOS_RATE" && rateIds.Contains(c.OwnerId)).ToListAsync(ct)).ToLookup(c => c.OwnerId);
        var conditionIds = conditions.SelectMany(g => g).Select(c => c.RateConditionId).ToList();
        var values = (await db.RateConditionValues.AsNoTracking()
            .Where(v => conditionIds.Contains(v.RateConditionId)).ToListAsync(ct)).ToLookup(v => v.RateConditionId, v => v.ValueCode);

        Add(db, rates.Select(r =>
        {
            var id = Guid.CreateVersion7();
            var copy = new TosRate
            {
                TosRateId = id, TenantId = to.TenantId, ScheduleId = to.ScheduleId,
                ChargeCodeId = r.ChargeCodeId, ChargeCode = r.ChargeCode, BillTo = r.BillTo, PaymentTermCode = r.PaymentTermCode,
                CreditTermDays = r.CreditTermDays, OrderTypeId = r.OrderTypeId, OrderTypeCode = r.OrderTypeCode,
                MovementId = r.MovementId, MovementCode = r.MovementCode,
                EquipmentTypeId = r.EquipmentTypeId, EquipmentTypeCode = r.EquipmentTypeCode, EquipmentSize = r.EquipmentSize,
                CargoCategoryCode = r.CargoCategoryCode, TruckCategoryCode = r.TruckCategoryCode,
                BillingUnitCode = r.BillingUnitCode, PricingMethod = r.PricingMethod, TierBasis = r.TierBasis, Rate = r.Rate,
                Source = "COPIED",
            };
            var copiedTiers = tiers[r.TosRateId].Select(t => new RateTier
            {
                RateTierId = Guid.CreateVersion7(), TenantId = to.TenantId, OwnerType = "TOS_RATE", OwnerId = id,
                FromQty = t.FromQty, ToQty = t.ToQty, Rate = t.Rate,
            }).ToList();
            var copiedConditions = conditions[r.TosRateId].Select(c => (
                new RateCondition
                {
                    RateConditionId = Guid.CreateVersion7(), TenantId = to.TenantId, OwnerType = "TOS_RATE", OwnerId = id,
                    SequenceNo = c.SequenceNo, Axis = c.Axis, Op = c.Op, NumberValue = c.NumberValue, BoolValue = c.BoolValue,
                    ModifierOp = c.ModifierOp, ModifierValue = c.ModifierValue, Label = c.Label,
                },
                (IReadOnlyList<string>)values[c.RateConditionId].ToList())).ToList();
            return new RateSetValidator.ValidRate(-1, copy, copiedTiers, copiedConditions);
        }).ToList());

        db.FreeTimeRules.AddRange((await db.FreeTimeRules.AsNoTracking().Where(f => f.ScheduleId == fromScheduleId).ToListAsync(ct))
            .Select(f => new FreeTimeRule
            {
                FreeTimeRuleId = Guid.CreateVersion7(), TenantId = to.TenantId, ScheduleId = to.ScheduleId,
                FreeTimeKind = f.FreeTimeKind, FullEmpty = f.FullEmpty, Direction = f.Direction, CargoGroup = f.CargoGroup,
                EquipmentSize = f.EquipmentSize, FreeUnits = f.FreeUnits, Unit = f.Unit,
            }));
    }
}
