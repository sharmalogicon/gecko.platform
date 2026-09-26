namespace Gecko.Revenue.Domain;

/// <summary>What a shipment looks like to a surcharge condition.</summary>
public sealed record ConditionContext(
    string? EquipmentSize, string? EquipmentTypeCode, string? CargoCategoryCode, string? TruckCategoryCode,
    bool IsReefer, bool IsDangerousGoods, bool IsOutOfGauge, decimal? GrossWeightKg);

public sealed record ConditionSpec(short SequenceNo, string Axis, string Op, IReadOnlyCollection<string> Values,
    decimal? Number, bool? Flag, string ModifierOp, decimal ModifierValue, string? Label);

public static class ConditionEvaluator
{
    /// <summary>
    /// Whether a condition holds. A value the shipment does not carry (no weight,
    /// no cargo category) never matches — a surcharge is not applied on a guess.
    /// </summary>
    public static bool Holds(ConditionSpec c, ConditionContext x)
    {
        string? code = c.Axis switch
        {
            ConditionAxes.EquipmentSize => x.EquipmentSize,
            ConditionAxes.EquipmentType => x.EquipmentTypeCode,
            ConditionAxes.CargoCategory => x.CargoCategoryCode,
            ConditionAxes.TruckCategory => x.TruckCategoryCode,
            _ => null,
        };

        return c.Op switch
        {
            ConditionOps.Eq or ConditionOps.In => code is not null && c.Values.Contains(code, StringComparer.OrdinalIgnoreCase),
            ConditionOps.Is => c.Axis switch
            {
                ConditionAxes.IsReefer => x.IsReefer == c.Flag,
                ConditionAxes.IsDg => x.IsDangerousGoods == c.Flag,
                ConditionAxes.IsOog => x.IsOutOfGauge == c.Flag,
                _ => false,
            },
            _ when x.GrossWeightKg is null || c.Number is null => false,
            ConditionOps.Gt => x.GrossWeightKg > c.Number,
            ConditionOps.Gte => x.GrossWeightKg >= c.Number,
            ConditionOps.Lt => x.GrossWeightKg < c.Number,
            ConditionOps.Lte => x.GrossWeightKg <= c.Number,
            _ => false,
        };
    }

    public static string Describe(ConditionSpec c)
    {
        if (!string.IsNullOrWhiteSpace(c.Label)) return c.Label;
        var modifier = c.ModifierOp switch
        {
            ModifierOps.Add => $"+{c.ModifierValue:0.##}",
            ModifierOps.Multiply => $"×{c.ModifierValue:0.####}",
            _ => $"={c.ModifierValue:0.##}",
        };
        var test = c.Op switch
        {
            ConditionOps.Is => $"{c.Axis} is {c.Flag}",
            ConditionOps.Eq or ConditionOps.In => $"{c.Axis} in ({string.Join(", ", c.Values)})",
            _ => $"{c.Axis} {c.Op} {c.Number:0.##}",
        };
        return $"{modifier} if {test}";
    }

    /// <summary>
    /// A code condition on an axis the rate row already FIXES can never change
    /// anything: on a GENERAL-cargo row, "if cargo is USED_ENGINE" is always false.
    /// Such a condition is a mistake, and saying so is kinder than ignoring it.
    /// </summary>
    public static bool IsDead(string axis, string op, bool rowFixesSize, bool rowFixesType, bool rowFixesCargo, bool rowFixesTruck) =>
        op is ConditionOps.Eq or ConditionOps.In && axis switch
        {
            ConditionAxes.EquipmentSize => rowFixesSize,
            ConditionAxes.EquipmentType => rowFixesType,
            ConditionAxes.CargoCategory => rowFixesCargo,
            ConditionAxes.TruckCategory => rowFixesTruck,
            _ => false,
        };
}

public sealed record FreeTimeSpec(string Kind, string? FullEmpty, string? Direction, string? CargoGroup, string? EquipmentSize, short FreeUnits);

/// <summary>
/// Chooses the free-time rule for a shipment within ONE schedule.
/// A rule applies when every dimension it names matches; among those, the most
/// specific wins: size 8 · cargo group 4 · direction 2 · full/empty 1. Two
/// applicable rules can name different dimensions with the same count
/// (IMPORT vs DG), so the weights — not the count — decide.
/// </summary>
public static class FreeTimeMatcher
{
    public const string Normal = "NORMAL";
    public const string Reefer = "REEFER";
    public const string Dangerous = "DG";

    /// <summary>DG outranks reefer: a hazardous reefer is handled as DG.</summary>
    public static string CargoGroup(bool isDangerous, bool isReefer) => isDangerous ? Dangerous : isReefer ? Reefer : Normal;

    public static FreeTimeSpec? Best(IEnumerable<FreeTimeSpec> rules, string kind, string? fullEmpty, string? direction, string cargoGroup, string? size) =>
        rules
            .Where(r => r.Kind == kind
                && (r.FullEmpty is null || r.FullEmpty == fullEmpty)
                && (r.Direction is null || r.Direction == direction)
                && (r.CargoGroup is null || r.CargoGroup == cargoGroup)
                && (r.EquipmentSize is null || r.EquipmentSize == size))
            .OrderByDescending(r => (r.EquipmentSize is null ? 0 : 8) + (r.CargoGroup is null ? 0 : 4)
                                    + (r.Direction is null ? 0 : 2) + (r.FullEmpty is null ? 0 : 1))
            .FirstOrDefault();
}
