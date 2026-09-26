namespace Gecko.Revenue.Domain;

/// <summary>
/// The closed vocabularies the tariff engine BRANCHES on. They mirror CHECK
/// constraints in gecko_revenue 04–07 — deliberately CHECKs, not data, because
/// adding a value means writing code anyway (see PLAN.md §3: module, bill-to
/// and payment term are the opposite case and are replicated DATA).
/// </summary>
public static class ScheduleTypes
{
    public const string Public = "PUBLIC";
    public const string Contract = "CONTRACT";
    public const string Spot = "SPOT";
    public static readonly string[] All = [Public, Contract, Spot];
}

/// <summary>What is stored. Everything date-dependent is <see cref="ScheduleLifecycle"/>.</summary>
public static class ScheduleStatuses
{
    public const string Draft = "DRAFT";
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Withdrawn = "WITHDRAWN";
}

public static class PricingMethods
{
    public const string Flat = "FLAT";
    public const string TieredIncremental = "TIERED_INCREMENTAL";
    public const string TieredBand = "TIERED_BAND";
    public const string TieredBlock = "TIERED_BLOCK";
    public static readonly string[] All = [Flat, TieredIncremental, TieredBand, TieredBlock];
}

public static class TierBases
{
    public const string Day = "DAY";
    public const string Hour = "HOUR";
    public const string Teu = "TEU";
    public const string FleetTeu = "FLEET_TEU";
    public static readonly string[] All = [Day, Hour, Teu, FleetTeu];

    /// <summary>Counted from 1 after free time is removed (Q4).</summary>
    public static bool IsTimeBased(string basis) => basis is Day or Hour;
}

public static class ConditionAxes
{
    public const string EquipmentSize = "EQUIPMENT_SIZE";
    public const string EquipmentType = "EQUIPMENT_TYPE";
    public const string CargoCategory = "CARGO_CATEGORY";
    public const string TruckCategory = "TRUCK_CATEGORY";
    public const string IsReefer = "IS_REEFER";
    public const string IsDg = "IS_DG";
    public const string IsOog = "IS_OOG";
    public const string WeightKg = "WEIGHT_KG";

    public static readonly string[] ListAxes = [EquipmentSize, EquipmentType, CargoCategory, TruckCategory];
    public static readonly string[] FlagAxes = [IsReefer, IsDg, IsOog];
    public static readonly string[] NumericAxes = [WeightKg];
    public static readonly string[] All = [.. ListAxes, .. FlagAxes, .. NumericAxes];
}

public static class ConditionOps
{
    public const string Eq = "EQ";
    public const string In = "IN";
    public const string Is = "IS";
    public const string Gt = "GT";
    public const string Gte = "GTE";
    public const string Lt = "LT";
    public const string Lte = "LTE";

    public static readonly string[] ListOps = [Eq, In];
    public static readonly string[] NumericOps = [Gt, Gte, Lt, Lte];
    public static readonly string[] All = [.. ListOps, Is, .. NumericOps];
}

public static class ModifierOps
{
    public const string Add = "ADD";
    public const string Multiply = "MULTIPLY";
    public const string Replace = "REPLACE";
    public static readonly string[] All = [Add, Multiply, Replace];
}

public static class FreeTimeKinds
{
    public const string Storage = "STORAGE";
    public const string Chassis = "CHASSIS";
    public const string TruckWaiting = "TRUCK_WAITING";
    public static readonly string[] All = [Storage, Chassis, TruckWaiting];

    /// <summary>Storage and chassis are counted in days, truck waiting in hours (ck_free_time_rule__unit).</summary>
    public static string UnitFor(string kind) => kind == TruckWaiting ? "HOUR" : "DAY";
}
