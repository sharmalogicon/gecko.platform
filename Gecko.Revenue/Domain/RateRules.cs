namespace Gecko.Revenue.Domain;

/// <summary>
/// How a rate row is chosen inside one schedule (PLAN.md §5.2, decision D-2).
/// The weights match the persisted `specificity` column in 05_tariff_tos_rate.sql.
/// A NULL axis means "any"; the highest total among matching rows wins, and
/// because equal totals mean an identical axis set, the unique axis signature
/// makes a tie impossible.
/// </summary>
public static class RateSpecificity
{
    public const int OrderType = 32;
    public const int Movement = 16;
    public const int EquipmentType = 8;
    public const int EquipmentSize = 4;
    public const int CargoCategory = 2;
    public const int TruckCategory = 1;

    public static int Of(bool orderType, bool movement, bool equipmentType, bool equipmentSize, bool cargo, bool truck) =>
        (orderType ? OrderType : 0) + (movement ? Movement : 0) + (equipmentType ? EquipmentType : 0)
        + (equipmentSize ? EquipmentSize : 0) + (cargo ? CargoCategory : 0) + (truck ? TruckCategory : 0);
}

/// <summary>
/// Surcharge condition shape rules — the same pairing the CHECKs in
/// tariff.rate_condition enforce, stated where a 400 can name the field.
/// </summary>
public static class ConditionRules
{
    /// <returns>null when the condition is well formed, else the reason.</returns>
    public static string? Problem(string axis, string op, int valueCount, decimal? number, bool? flag, string modifierOp, decimal modifierValue)
    {
        if (!ConditionAxes.All.Contains(axis)) return $"'{axis}' is not a condition axis.";
        if (!ConditionOps.All.Contains(op)) return $"'{op}' is not a condition operator.";
        if (!ModifierOps.All.Contains(modifierOp)) return $"'{modifierOp}' is not a modifier.";

        if (ConditionOps.NumericOps.Contains(op))
        {
            if (!ConditionAxes.NumericAxes.Contains(axis)) return $"{op} compares numbers; {axis} is not numeric.";
            if (number is null || flag is not null || valueCount > 0) return $"{op} needs a number and nothing else.";
        }
        else if (op == ConditionOps.Is)
        {
            if (!ConditionAxes.FlagAxes.Contains(axis)) return $"IS tests a flag; {axis} is not one.";
            if (flag is null || number is not null || valueCount > 0) return "IS needs true or false and nothing else.";
        }
        else
        {
            if (!ConditionAxes.ListAxes.Contains(axis)) return $"{op} matches codes; {axis} is not a code axis.";
            if (number is not null || flag is not null) return $"{op} takes a list of codes, not a number or flag.";
            if (valueCount == 0) return $"{op} needs at least one value.";
            if (op == ConditionOps.Eq && valueCount != 1) return "EQ takes exactly one value — use IN for several.";
        }

        if (modifierOp == ModifierOps.Multiply && modifierValue <= 0) return "A MULTIPLY factor must be greater than zero.";
        if (modifierOp == ModifierOps.Replace && modifierValue < 0) return "A REPLACE amount cannot be negative.";
        return null;
    }

    /// <summary>Applies modifiers IN SEQUENCE: +200 then ×1.5 is not ×1.5 then +200.</summary>
    public static decimal Apply(decimal unitRate, string modifierOp, decimal modifierValue) => modifierOp switch
    {
        ModifierOps.Add => unitRate + modifierValue,
        ModifierOps.Multiply => unitRate * modifierValue,
        ModifierOps.Replace => modifierValue,
        _ => throw new ArgumentOutOfRangeException(nameof(modifierOp), modifierOp, null),
    };
}
