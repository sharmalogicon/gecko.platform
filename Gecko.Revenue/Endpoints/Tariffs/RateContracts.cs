using System.ComponentModel.DataAnnotations;

namespace Gecko.Revenue.Endpoints.Tariffs;

// ── requests ────────────────────────────────────────────────────────────────

/// <summary>A tier counts CHARGEABLE units (free time is removed first). Leave <see cref="ToQty"/> empty on the last tier for "and beyond".</summary>
public sealed record TierItem(
    [property: Range(0, 99999)] decimal FromQty,
    [property: Range(0, 99999)] decimal? ToQty,
    [property: Range(0, 99999999)] decimal Rate);

/// <summary>
/// "+฿300 if weight &gt; 30,000" = { axis: WEIGHT_KG, op: GT, number: 30000, modifierOp: ADD, modifierValue: 300 }.
/// Conditions apply in the order given.
/// </summary>
public sealed record ConditionItem(
    [property: Required, MaxLength(20)] string Axis,
    [property: Required, MaxLength(5)] string Op,
    [property: Required, MaxLength(10)] string ModifierOp,
    decimal ModifierValue,
    IReadOnlyList<string>? Values = null,
    decimal? Number = null,
    bool? Flag = null,
    [property: MaxLength(100)] string? Label = null);

/// <summary>
/// One priced row. Leave an axis empty for "any". The most specific matching row
/// wins (order type › movement › equipment type › size › cargo › truck).
/// </summary>
public sealed record RateItem(
    [property: Required, MaxLength(15)] string ChargeCode,
    [property: Required, MaxLength(20)] string BillTo,
    [property: Required, MaxLength(20)] string PaymentTermCode,
    [property: Range(0, 365)] short? CreditTermDays = null,
    [property: MaxLength(50)] string? OrderTypeCode = null,
    [property: MaxLength(20)] string? MovementCode = null,
    [property: MaxLength(10)] string? EquipmentTypeCode = null,
    [property: MaxLength(3)] string? EquipmentSize = null,
    [property: MaxLength(40)] string? CargoCategoryCode = null,
    [property: MaxLength(40)] string? TruckCategoryCode = null,
    [property: MaxLength(20)] string? BillingUnitCode = null,
    [property: AllowedValues("FLAT", "TIERED_INCREMENTAL", "TIERED_BAND", "TIERED_BLOCK")] string PricingMethod = "FLAT",
    [property: AllowedValues(null, "DAY", "HOUR", "TEU", "FLEET_TEU")] string? TierBasis = null,
    [property: Range(0, 99999999)] decimal? Rate = null,
    IReadOnlyList<TierItem>? Tiers = null,
    IReadOnlyList<ConditionItem>? Conditions = null);

public sealed record ReplaceRatesRequest(
    [property: Required] string RowVersion,
    [property: Required, MaxLength(2000)] IReadOnlyList<RateItem> Rates);

public sealed record FreeTimeItem(
    [property: Required, AllowedValues("STORAGE", "CHASSIS", "TRUCK_WAITING")] string FreeTimeKind,
    [property: Range(0, 3650)] short FreeUnits,
    [property: AllowedValues(null, "FULL", "EMPTY")] string? FullEmpty = null,
    [property: AllowedValues(null, "IMPORT", "EXPORT", "LOCAL")] string? Direction = null,
    [property: AllowedValues(null, "NORMAL", "REEFER", "DG")] string? CargoGroup = null,
    [property: RegularExpression("^[1-9][0-9]$")] string? EquipmentSize = null);

public sealed record ReplaceFreeTimeRequest(
    [property: Required] string RowVersion,
    [property: Required] IReadOnlyList<FreeTimeItem> Rules);

// ── responses ───────────────────────────────────────────────────────────────

public sealed record ConditionResponse(
    short SequenceNo, string Axis, string Op, IReadOnlyList<string> Values, decimal? Number, bool? Flag,
    string ModifierOp, decimal ModifierValue, string? Label);

public sealed record RateResponse(
    Guid TosRateId, string ChargeCode, string BillTo, string PaymentTermCode, short? CreditTermDays,
    string? OrderTypeCode, string? MovementCode, string? EquipmentTypeCode, string? EquipmentSize,
    string? CargoCategoryCode, string? TruckCategoryCode, string BillingUnitCode,
    string PricingMethod, string? TierBasis, decimal? Rate, int Specificity, string Source,
    IReadOnlyList<TierItem> Tiers, IReadOnlyList<ConditionResponse> Conditions);

/// <summary><see cref="RowVersion"/> is the SCHEDULE's — replacing its rates moves it.</summary>
public sealed record RateSetResponse(Guid ScheduleId, string Status, string RowVersion, IReadOnlyList<RateResponse> Rates);

public sealed record FreeTimeResponse(
    string FreeTimeKind, string? FullEmpty, string? Direction, string? CargoGroup, string? EquipmentSize, short FreeUnits, string Unit);

public sealed record FreeTimeSetResponse(Guid ScheduleId, string Status, string RowVersion, IReadOnlyList<FreeTimeResponse> Rules);
