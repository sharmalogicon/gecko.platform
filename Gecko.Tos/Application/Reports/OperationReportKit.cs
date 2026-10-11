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

    public static string Day(DateOnly? day, string format = "dd-MM-yyyy") => day?.ToString(format, CultureInfo.InvariantCulture) ?? "";

    /// <summary>A depot-local day, or null.</summary>
    public static DateOnly? LocalDay(BranchClock.Branch branch, DateTimeOffset? at) =>
        at is { } x ? DateOnly.FromDateTime(GateInOutReport.Local(branch, x)) : null;

    /// <summary>Vector's inclusive day count, DATEDIFF(day, in, out) + 1; 0 when either end is missing.</summary>
    public static int InclusiveDays(DateOnly? from, DateOnly? to) => from is { } a && to is { } b ? b.DayNumber - a.DayNumber + 1 : 0;
}
