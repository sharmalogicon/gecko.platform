using System.Globalization;

namespace Gecko.Revenue.Domain;

/// <summary>
/// Tiers as ONE spreadsheet cell: <c>1-7:160; 8-14:275; 15+:390</c>.
///
/// Why one cell and not a row per tier: a tariff sheet is read as "one line per
/// price", which is how Vector's users think of a slab rate and how the export
/// stays sortable and filterable. The format is small enough to type, and
/// <see cref="Parse"/> says exactly what is wrong when it is mistyped.
/// </summary>
public static class TierText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Format(IEnumerable<Tier> tiers) =>
        string.Join("; ", tiers.OrderBy(t => t.FromQty).Select(t =>
            (t.ToQty is { } to ? $"{Num(t.FromQty)}-{Num(to)}" : $"{Num(t.FromQty)}+") + ":" + Num(t.Rate)));

    private static string Num(decimal value) => value.ToString("0.####", Invariant);

    /// <returns>The tiers, or the reason the text is not a tier list.</returns>
    public static (IReadOnlyList<Tier> Tiers, string? Error) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ([], null);

        var tiers = new List<Tier>();
        foreach (var raw in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = raw.LastIndexOf(':');
            if (colon <= 0) return ([], $"'{raw}' should look like 1-7:160 (range, colon, rate).");

            var range = raw[..colon].Replace(" ", "");
            if (!decimal.TryParse(raw[(colon + 1)..].Trim().Replace(",", ""), NumberStyles.Number, Invariant, out var rate))
                return ([], $"'{raw}': the rate after ':' is not a number.");

            decimal from;
            decimal? to;
            if (range.EndsWith('+'))
            {
                if (!decimal.TryParse(range[..^1], NumberStyles.Number, Invariant, out from))
                    return ([], $"'{raw}': '{range}' should look like 15+.");
                to = null;
            }
            else
            {
                var parts = range.Split('-');
                if (parts.Length != 2
                    || !decimal.TryParse(parts[0], NumberStyles.Number, Invariant, out from)
                    || !decimal.TryParse(parts[1], NumberStyles.Number, Invariant, out var end))
                    return ([], $"'{raw}': '{range}' should look like 1-7 or 15+.");
                to = end;
            }
            tiers.Add(new Tier(from, to, rate));
        }
        return (tiers, null);
    }
}
