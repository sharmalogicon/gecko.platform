using System.Globalization;
using Gecko.Data.Documents;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Gecko.Tos.Application.Reports.Forms;

internal enum Align { General, Left, Center, Right }

/// <summary>A report item of an RDL form at its own place: Top/Left/Width/Height in inches from the body's origin.</summary>
internal abstract record FormItem(double Top, double Left, double Width, double Height);

/// <param name="Border">Sides drawn: A (all), T, B, L, R — the RDL's Border / TopBorder / … styles that are not None.</param>
internal sealed record FormText(double Top, double Left, double Width, double Height, string Text, float Size, Align Align,
    bool Bold = false, string Border = "", bool Dotted = false) : FormItem(Top, Left, Width, Height);

/// <summary>A line: horizontal when Height is 0, vertical when Width is 0.</summary>
internal sealed record FormLine(double Top, double Left, double Width, double Height, bool Dotted) : FormItem(Top, Left, Width, Height);

/// <summary>A rectangle's own border (its items are placed as the form's).</summary>
internal sealed record FormBox(double Top, double Left, double Width, double Height, string Border) : FormItem(Top, Left, Width, Height);

/// <summary>
/// A tablix: it starts at <see cref="Top"/> and grows with its rows; the items below its RDL bottom
/// (<see cref="Top"/> + <see cref="Height"/>) move down with it, as SSRS moved them.
/// </summary>
internal sealed record FormTable(double Top, double Left, double Height, Action<IContainer> Render);

/// <summary>
/// An RDL form (an SSRS body of positioned textboxes, lines and growing tablixes) drawn with QuestPDF: the items above
/// the first table at their places, the table flowing, the items between it and the next table at their places
/// relative to its bottom, and so on — Tahoma's metrics replaced by Gecko's fonts, SSRS's 2pt text padding kept.
/// </summary>
internal static class FormLayout
{
    public static float In(double inches) => (float)(inches * 72);

    /// <summary>A Letter-size form (the RDLs set no page size) with 0.1in margins, as the booking forms are.</summary>
    public static byte[] Pdf(IReadOnlyList<FormItem> items, IReadOnlyList<FormTable> tables, double marginIn = 0.1)
    {
        GeckoPdf.EnsureInitialised();
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(In(marginIn));
            page.DefaultTextStyle(t => t.FontSize(10).FontFamily(GeckoPdf.LatinFont, GeckoPdf.ThaiFont));
            page.Content().Column(column =>
            {
                var start = 0.0;
                foreach (var table in tables.OrderBy(t => t.Top))
                {
                    Band(column, items.Where(i => i.Top >= start && i.Top < table.Top).ToList(), start, table.Top);
                    column.Item().PaddingLeft(In(table.Left)).Element(table.Render);
                    start = table.Top + table.Height;
                }
                var rest = items.Where(i => i.Top >= start).ToList();
                if (rest.Count > 0) Band(column, rest, start, rest.Max(i => i.Top + Math.Max(i.Height, 0.01)));
            });
        })).GeneratePdf();
    }

    /// <summary>
    /// The items between two tables, at their places: split into strips of items that overlap vertically, so a page can
    /// break between strips (SSRS moved items that no longer fit to the next page, keeping their order).
    /// </summary>
    private static void Band(ColumnDescriptor column, IReadOnlyList<FormItem> items, double start, double end)
    {
        if (end <= start) return;
        var at = start;
        var strips = new List<(double Top, double Bottom, List<FormItem> Items)>();
        foreach (var item in items.OrderBy(i => i.Top))
        {
            var bottom = item.Top + Math.Max(item.Height, 0.01);
            if (strips.Count > 0 && item.Top < strips[^1].Bottom)
            {
                strips[^1].Items.Add(item);
                strips[^1] = (strips[^1].Top, Math.Max(strips[^1].Bottom, bottom), strips[^1].Items);
            }
            else strips.Add((item.Top, bottom, [item]));
        }
        foreach (var (top, bottom, strip) in strips)
        {
            if (top > at) column.Item().Height(In(top - at));
            Strip(column, strip, top, bottom);
            at = bottom;
        }
        if (end > at) column.Item().Height(In(end - at));
    }

    private static void Strip(ColumnDescriptor column, IReadOnlyList<FormItem> items, double start, double end)
    {
        column.Item().MinHeight(In(end - start)).Layers(layers =>
        {
            layers.PrimaryLayer();
            foreach (var item in items)
            {
                // Unconstrained: a grown textbox may run past its strip, as an SSRS item grew over the space below it.
                var at = layers.Layer().PaddingTop(In(item.Top - start)).PaddingLeft(In(item.Left)).Unconstrained();
                switch (item)
                {
                    case FormText t:
                        // SSRS textboxes CanGrow: the box keeps its width and grows down rather than shrinking its text.
                        var box = Borders(at.Width(In(t.Width)).MinHeight(In(t.Height)), t.Border, t.Dotted).Padding(2);
                        box = t.Align switch
                        {
                            Align.Center => box.AlignCenter(),
                            Align.Right => box.AlignRight(),
                            _ => box.AlignLeft(),
                        };
                        var text = box.Text(t.Text).FontSize(t.Size).LineHeight(1);
                        if (t.Bold) text.Bold();
                        break;
                    case FormLine { Height: 0 } l:
                        var across = at.Width(In(l.Width)).LineHorizontal(0.75f).LineColor(Colors.Black);
                        if (l.Dotted) across.LineDashPattern([1f, 2f]);
                        break;
                    case FormLine l:
                        var down = at.Height(In(l.Height)).LineVertical(0.75f).LineColor(Colors.Black);
                        if (l.Dotted) down.LineDashPattern([1f, 2f]);
                        break;
                    case FormBox b:
                        Borders(at.Width(In(b.Width)).Height(In(b.Height)), b.Border, false);
                        break;
                }
            }
        });
    }

    public static IContainer Borders(IContainer cell, string sides, bool dotted)
    {
        var width = dotted ? 0.5f : 0.75f;
        if (sides.Contains('A') || sides.Contains('T')) cell = cell.BorderTop(width);
        if (sides.Contains('A') || sides.Contains('B')) cell = cell.BorderBottom(width);
        if (sides.Contains('A') || sides.Contains('L')) cell = cell.BorderLeft(width);
        if (sides.Contains('A') || sides.Contains('R')) cell = cell.BorderRight(width);
        return cell;
    }

    // ── what the generated item lists call ──

    public static FormText T(double top, double left, double width, double height, string text, float size, Align align,
        bool bold = false, string border = "", bool dotted = false) => new(top, left, width, height, text, size, align, bold, border, dotted);

    public static FormLine L(double top, double left, double width, double height, bool dotted = false) => new(top, left, width, height, dotted);

    public static FormBox R(double top, double left, double width, double height, string border) => new(top, left, width, height, border);

    /// <summary>An RDL value as text: null prints blank, numbers invariant.</summary>
    public static string S(object? value) => value switch
    {
        null => "",
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>The RDL's Format(date, "…"): blank for no date.</summary>
    public static string D(DateTime? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? "";
}
