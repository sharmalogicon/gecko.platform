using System.Text.RegularExpressions;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Domain;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Endpoints.Tariffs;

/// <summary>
/// Turns a submitted rate table into rows, or into EVERY problem it has.
///
/// One pass, all errors, keyed rates[i].field — a 700-row tariff (Vector's
/// largest is 696) that reports one mistake per round trip is unusable, and
/// the same validator will serve the Excel import in 2.7.
///
/// Codes are resolved in bulk: one master-data call per vocabulary, not per row.
/// </summary>
internal sealed partial class RateSetValidator(RevenueDbContext db, IMasterDataReferences masterData)
{
    public sealed record Result(IReadOnlyList<ValidRate> Rates, Dictionary<string, List<string>> Errors)
    {
        public bool IsValid => Errors.Count == 0;
    }

    /// <param name="Index">Position of the input item this row came from; -1 when not from an input list.</param>
    public sealed record ValidRate(int Index, TosRate Rate, IReadOnlyList<RateTier> Tiers, IReadOnlyList<(RateCondition Condition, IReadOnlyList<string> Values)> Conditions);

    [GeneratedRegex("^[1-9][0-9]$")]
    private static partial Regex SizePattern();

    private static string? Up(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    public async Task<Result> ValidateAsync(Schedule schedule, IReadOnlyList<RateItem> items, CancellationToken ct)
    {
        var errors = new Dictionary<string, List<string>>();
        var rows = items.Select(i => i with
        {
            ChargeCode = Up(i.ChargeCode)!, BillTo = Up(i.BillTo)!, PaymentTermCode = Up(i.PaymentTermCode)!,
            OrderTypeCode = Up(i.OrderTypeCode), MovementCode = Up(i.MovementCode),
            EquipmentTypeCode = Up(i.EquipmentTypeCode), EquipmentSize = Up(i.EquipmentSize),
            CargoCategoryCode = Up(i.CargoCategoryCode), TruckCategoryCode = Up(i.TruckCategoryCode),
            BillingUnitCode = Up(i.BillingUnitCode), PricingMethod = Up(i.PricingMethod)!, TierBasis = Up(i.TierBasis),
        }).ToList();

        // ── vocabularies, in bulk ───────────────────────────────────────────
        var charges = await masterData.ChargeCodesAsync(rows.Select(r => r.ChargeCode), ct);
        var orderTypes = await masterData.OrderTypesAsync(rows.Select(r => r.OrderTypeCode).OfType<string>(), ct);
        var movements = await masterData.MovementsAsync(rows.Select(r => r.MovementCode).OfType<string>(), ct);

        var conditionTypeCodes = rows.SelectMany(r => r.Conditions ?? [])
            .Where(c => Up(c.Axis) == ConditionAxes.EquipmentType).SelectMany(c => c.Values ?? []);
        var equipment = await masterData.EquipmentTypesAsync(
            rows.Select(r => r.EquipmentTypeCode).OfType<string>().Concat(conditionTypeCodes), ct);

        var conditionCargo = rows.SelectMany(r => r.Conditions ?? []).Where(c => Up(c.Axis) == ConditionAxes.CargoCategory).SelectMany(c => c.Values ?? []);
        var conditionTruck = rows.SelectMany(r => r.Conditions ?? []).Where(c => Up(c.Axis) == ConditionAxes.TruckCategory).SelectMany(c => c.Values ?? []);
        var cargo = await masterData.CodeListValuesAsync("CARGO_CATEGORY", rows.Select(r => r.CargoCategoryCode).OfType<string>().Concat(conditionCargo), ct);
        var trucks = await masterData.CodeListValuesAsync("TRUCK_CATEGORY", rows.Select(r => r.TruckCategoryCode).OfType<string>().Concat(conditionTruck), ct);

        var billTo = (await db.BillToRoles.AsNoTracking().Where(b => b.IsActive).Select(b => b.Code).ToListAsync(ct)).ToHashSet();
        var terms = (await db.PaymentTerms.AsNoTracking().Where(p => p.IsActive).Select(p => p.Code).ToListAsync(ct)).ToHashSet();
        var units = await db.BillingUnits.AsNoTracking().Where(b => b.IsActive)
            .ToDictionaryAsync(b => b.Code, b => new { b.QuantitySource, b.IsTimeBased }, ct);

        var valid = new List<ValidRate>();
        var signatures = new Dictionary<string, int>();

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var at = $"rates[{i}]";
            var before = errors.Count;

            // charge — must belong to THIS tariff's module
            charges.TryGetValue(r.ChargeCode, out var charge);
            if (charge is null) errors.Add($"{at}.chargeCode", $"Unknown charge code '{r.ChargeCode}'.");
            else if (!charge.IsActive) errors.Add($"{at}.chargeCode", $"'{r.ChargeCode}' is inactive.");
            else if (charge.ModuleCode != schedule.ModuleCode)
                errors.Add($"{at}.chargeCode", $"'{r.ChargeCode}' is a {charge.ModuleCode} charge; this is a {schedule.ModuleCode} tariff.");

            if (!billTo.Contains(r.BillTo)) errors.Add($"{at}.billTo", $"Unknown bill-to role '{r.BillTo}'.");
            if (!terms.Contains(r.PaymentTermCode)) errors.Add($"{at}.paymentTermCode", $"Unknown payment term '{r.PaymentTermCode}'.");

            CodeRef? orderType = null, movement = null;
            if (r.OrderTypeCode is { } ot && !orderTypes.TryGetValue(ot, out orderType)) errors.Add($"{at}.orderTypeCode", $"Unknown order type '{ot}'.");
            if (r.MovementCode is { } mv && !movements.TryGetValue(mv, out movement)) errors.Add($"{at}.movementCode", $"Unknown movement '{mv}'.");

            // equipment — a specific type fixes the size
            var size = r.EquipmentSize;
            EquipmentTypeRef? type = null;
            if (r.EquipmentTypeCode is { } et)
            {
                if (!equipment.TryGetValue(et, out type)) errors.Add($"{at}.equipmentTypeCode", $"Unknown equipment type '{et}'.");
                else if (size is not null && size != type.SizeCode)
                    errors.Add($"{at}.equipmentSize", $"'{et}' is a {type.SizeCode}ft type, not {size}ft.");
                else size = type.SizeCode;
            }
            if (size is not null && !SizePattern().IsMatch(size)) errors.Add($"{at}.equipmentSize", $"'{size}' is not a container length like 20, 40 or 45.");

            if (r.CargoCategoryCode is { } cc && !cargo.Contains(cc)) errors.Add($"{at}.cargoCategoryCode", $"'{cc}' is not a CARGO_CATEGORY for this depot.");
            if (r.TruckCategoryCode is { } tc && !trucks.Contains(tc)) errors.Add($"{at}.truckCategoryCode", $"'{tc}' is not a TRUCK_CATEGORY for this depot.");

            // unit — the charge's own unless overridden
            var unit = r.BillingUnitCode ?? charge?.BillingUnitCode;
            if (unit is null || !units.TryGetValue(unit, out var unitInfo))
            {
                errors.Add($"{at}.billingUnitCode", $"Unknown billing unit '{unit}'.");
                unitInfo = null;
            }

            // shape
            var tiered = r.PricingMethod != PricingMethods.Flat;
            var tiers = (r.Tiers ?? []).Select(t => new Tier(t.FromQty, t.ToQty, t.Rate)).ToList();
            if (!tiered)
            {
                if (r.Rate is null) errors.Add($"{at}.rate", "A FLAT row needs a rate.");
                if (r.TierBasis is not null) errors.Add($"{at}.tierBasis", "A FLAT row has no tier basis.");
                if (tiers.Count > 0) errors.Add($"{at}.tiers", "A FLAT row has no tiers.");
            }
            else
            {
                if (r.Rate is not null) errors.Add($"{at}.rate", "A tiered row is priced by its tiers — leave rate empty.");
                if (r.TierBasis is null) errors.Add($"{at}.tierBasis", "A tiered row needs a basis: DAY, HOUR, TEU or FLEET_TEU.");
                else
                {
                    foreach (var defect in TierPricing.Defects(tiers, r.TierBasis))
                        errors.Add($"{at}.tiers", DescribeDefect(defect));
                    if (TierBases.IsTimeBased(r.TierBasis) && unitInfo is not null
                        && (!unitInfo.IsTimeBased || unitInfo.QuantitySource != r.TierBasis))
                        errors.Add($"{at}.billingUnitCode", $"Tiers by {r.TierBasis} need a per-{r.TierBasis.ToLowerInvariant()} billing unit, not {unit}.");
                }
            }

            // conditions
            var conditions = new List<(RateCondition, IReadOnlyList<string>)>();
            var conditionItems = r.Conditions ?? [];
            for (var c = 0; c < conditionItems.Count; c++)
            {
                var item = conditionItems[c];
                var axis = Up(item.Axis)!;
                var op = Up(item.Op)!;
                var modifier = Up(item.ModifierOp)!;
                var values = (item.Values ?? []).Select(v => Up(v)).OfType<string>().Distinct().ToList();
                var cat = $"{at}.conditions[{c}]";

                if (ConditionRules.Problem(axis, op, values.Count, item.Number, item.Flag, modifier, item.ModifierValue) is { } problem)
                {
                    errors.Add(cat, problem);
                    continue;
                }
                if (ConditionEvaluator.IsDead(axis, op, size is not null, r.EquipmentTypeCode is not null,
                        r.CargoCategoryCode is not null, r.TruckCategoryCode is not null))
                    errors.Add(cat, $"This row already fixes {axis}, so a condition on it can never change the price. " +
                                    "Put it on a row that leaves the axis open.");

                var unknown = axis switch
                {
                    ConditionAxes.EquipmentType => values.Where(v => !equipment.ContainsKey(v)),
                    ConditionAxes.CargoCategory => values.Where(v => !cargo.Contains(v)),
                    ConditionAxes.TruckCategory => values.Where(v => !trucks.Contains(v)),
                    ConditionAxes.EquipmentSize => values.Where(v => !SizePattern().IsMatch(v)),
                    _ => [],
                };
                if (unknown.ToList() is { Count: > 0 } bad)
                    errors.Add(cat, $"Not valid for {axis}: {string.Join(", ", bad)}.");

                conditions.Add((new RateCondition
                {
                    RateConditionId = Guid.CreateVersion7(),
                    TenantId = schedule.TenantId,
                    OwnerType = "TOS_RATE",
                    SequenceNo = (short)(c + 1),
                    Axis = axis,
                    Op = op,
                    NumberValue = item.Number,
                    BoolValue = item.Flag,
                    ModifierOp = modifier,
                    ModifierValue = item.ModifierValue,
                    Label = item.Label,
                }, values));
            }

            // duplicate axis values for the same charge + payer + terms + unit (uq_tos_rate__signature)
            var signature = string.Join('|', r.ChargeCode, r.BillTo, r.PaymentTermCode,
                r.OrderTypeCode ?? "*", r.MovementCode ?? "*", r.EquipmentTypeCode ?? "*", size ?? "*",
                r.CargoCategoryCode ?? "*", r.TruckCategoryCode ?? "*", unit ?? "*");
            if (signatures.TryGetValue(signature, out var first))
                errors.Add($"{at}", $"Same charge, payer, terms, axes and billing unit as rates[{first}] — the resolver could not choose between them.");
            else
                signatures[signature] = i;

            if (errors.Count > before || charge is null) continue;

            var rateId = Guid.CreateVersion7();
            foreach (var (condition, _) in conditions) condition.OwnerId = rateId;
            valid.Add(new ValidRate(
                i,
                new TosRate
                {
                    TosRateId = rateId,
                    TenantId = schedule.TenantId,
                    ScheduleId = schedule.ScheduleId,
                    ChargeCodeId = charge.ChargeCodeId,
                    ChargeCode = charge.ChargeCode,
                    BillTo = r.BillTo,
                    PaymentTermCode = r.PaymentTermCode,
                    CreditTermDays = r.CreditTermDays,
                    OrderTypeId = orderType?.Id,
                    OrderTypeCode = orderType?.Code,
                    MovementId = movement?.Id,
                    MovementCode = movement?.Code,
                    EquipmentTypeId = type?.EquipmentTypeId,
                    EquipmentTypeCode = type?.TypeCode,
                    EquipmentSize = size,
                    CargoCategoryCode = r.CargoCategoryCode,
                    TruckCategoryCode = r.TruckCategoryCode,
                    BillingUnitCode = unit!,
                    PricingMethod = r.PricingMethod,
                    TierBasis = tiered ? r.TierBasis : null,
                    Rate = tiered ? null : r.Rate,
                    Source = "MANUAL",
                },
                tiers.Select(t => new RateTier
                {
                    RateTierId = Guid.CreateVersion7(),
                    TenantId = schedule.TenantId,
                    OwnerType = "TOS_RATE",
                    OwnerId = rateId,
                    FromQty = t.FromQty,
                    ToQty = t.ToQty,
                    Rate = t.Rate,
                }).ToList(),
                conditions));
        }

        return new Result(valid, errors);
    }

    private static string DescribeDefect(string defect) => defect switch
    {
        TierPricing.NoTiers => "A tiered row needs at least one tier.",
        TierPricing.FirstNotOne => "Day and hour tiers start at 1 — the first chargeable unit after free time.",
        TierPricing.GapOrOverlap => "Tiers must follow on without a gap or overlap (1–7, 8–14, 15+).",
        TierPricing.OpenNotLast => "Only the last tier may be open-ended.",
        TierPricing.BadRange => "A tier ends before it starts, or has a negative value.",
        _ => defect,
    };
}
