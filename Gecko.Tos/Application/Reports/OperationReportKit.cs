using System.Globalization;
using Gecko.Data.Documents;

namespace Gecko.Tos.Application.Reports;

/// <summary>
/// What the Operation reports (TMS.Operation.*, owner 2026-10-11) share on top of <see cref="TabularReport"/>: the RDL's
/// count matrices (a row group × a column group of COUNT, with Total row and column) and the day formats Vector printed.
/// </summary>
internal static class OperationReportKit
{
    /// <summary>
    /// An SSRS count matrix under the grid: <paramref name="corner"/> over the row labels, a column per distinct column key
    /// (sorted ascending, as the RDL sorts its groups), a Total column, a row per row key (sorted) and a Total row.
    /// </summary>
    public static TabularBlock CountMatrix(
        string? caption, string corner, IEnumerable<(string? Row, string? Column)> items,
        string totalColumn = "Total", string totalRow = "Total", double rowLabelCm = 2.5, double cellCm = 2.5)
    {
        var list = items.Select(i => (Row: i.Row ?? "", Column: i.Column ?? "")).ToList();
        var columns = list.Select(i => i.Column).Distinct().Order(StringComparer.Ordinal).ToList();
        var rows = list.GroupBy(i => i.Row).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TabularRow([g.Key, .. columns.Select(c => (object?)g.Count(i => i.Column == c)), g.Count()]))
            .ToList();
        rows.Add(new TabularRow([totalRow, .. columns.Select(c => (object?)list.Count(i => i.Column == c)), list.Count], RowKind.Total));
        return new TabularBlock(caption,
            [new TabularColumn(rowLabelCm), .. columns.Select(_ => new TabularColumn(cellCm, null, CellAlign.Center)), new TabularColumn(cellCm, null, CellAlign.Center)],
            [[new HeaderCell(corner), .. columns.Select(c => new HeaderCell(c)), new HeaderCell(totalColumn)]],
            rows);
    }

    public const string Fcl = "FCL";
    public const string LclCfs = "LCL-CFS";
    public const string LclCyd = "LCL-CYD";

    private static readonly IReadOnlyDictionary<string, string> NotFcl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["EXP CFS"] = LclCfs, ["EXP CFS W/H"] = LclCfs, ["IMP CFS"] = LclCfs,
        ["EXP CY-IN (NOMINATING)"] = LclCyd, ["EXP CY-IN (NON-NOMINATING)"] = LclCyd, ["IMP CYD"] = LclCyd,
        ["INT IN"] = "NONE", ["INT OUT"] = "NONE",
    };

    /// <summary>
    /// Vector's shipment type of an order type (Master.OrderType.ShipmentType, read from TMSKORAKIT): the CFS order types
    /// LCL-CFS, the CY-direct ones LCL-CYD, the internal moves NONE, every other order type FCL — as Vector set every one of
    /// them. Gecko's order types carry no shipment type (owner to confirm this map, 2026-10-11).
    /// </summary>
    public static string ShipmentType(string orderTypeCode) => NotFcl.GetValueOrDefault(orderTypeCode) ?? Fcl;

    /// <summary>
    /// Vector's four movement names (the Movement combo: FULL IN, MTY IN, FULL OUT, MTY OUT; underscores accepted) as Gecko's
    /// direction and full/empty — what selects a move here, since movement codes differ by tenant. Null for anything else.
    /// </summary>
    public static (string Direction, string FullEmpty)? Movement(string? name) =>
        name?.Trim().Replace('_', ' ').ToUpperInvariant() switch
        {
            "FULL IN" => ("IN", "FULL"),
            "MTY IN" => ("IN", "EMPTY"),
            "FULL OUT" => ("OUT", "FULL"),
            "MTY OUT" => ("OUT", "EMPTY"),
            _ => null,
        };

    public static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    public static string Day(DateOnly? day, string format = "dd-MM-yyyy") => day?.ToString(format, CultureInfo.InvariantCulture) ?? "";

    /// <summary>A depot-local day, or null.</summary>
    public static DateOnly? LocalDay(BranchClock.Branch branch, DateTimeOffset? at) =>
        at is { } x ? DateOnly.FromDateTime(GateInOutReport.Local(branch, x)) : null;

    /// <summary>Vector's inclusive day count, DATEDIFF(day, in, out) + 1; 0 when either end is missing.</summary>
    public static int InclusiveDays(DateOnly? from, DateOnly? to) => from is { } a && to is { } b ? b.DayNumber - a.DayNumber + 1 : 0;
}
