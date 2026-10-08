using System.Globalization;
using ClosedXML.Excel;
using Gecko.Data.Documents;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Gecko.Tos.Application.Reports;

/// <summary>A line of a report's heading block, sized as the Vector RDL sizes it (points).</summary>
internal sealed record ReportLine(string Text, float FontSize = 10, bool Bold = false);

/// <summary>
/// One column of a report's grid, as the RDL draws it: header text, width (cm, scaled to the page
/// together with every other column) and the cell's display format (.NET and Excel read the same
/// patterns: dd-MM-yyyy HH:mm, H:mm, #,0;(#,0), 0.00).
/// </summary>
internal sealed record ReportColumn(string Header, double WidthCm, string? Format = null);

/// <summary>
/// The size × type count under a Vector list report (an SSRS matrix): a row per party, a column per
/// size/type present in the data, a total column and a total row.
/// <c>SplitHeader</c>: the in-yard reports head the columns with two rows (size, then type) and
/// unshaded cells; the gate report with one shaded row ("20 GP").
/// </summary>
internal sealed record SummaryMatrix(
    string? Caption, bool SplitHeader, string TotalColumnLabel, string TotalRowLabel,
    IReadOnlyList<(string Size, string Type)> Columns,
    IReadOnlyList<(string Label, IReadOnlyList<int> Counts)> Rows)
{
    /// <summary>Counts <paramref name="items"/> (one entry per dataset row) by party, size and type, groups sorted as SSRS sorts them.</summary>
    public static SummaryMatrix Of(
        IEnumerable<(string? Party, string? Size, string? Type)> items,
        string? caption, bool splitHeader, string totalColumnLabel, string totalRowLabel)
    {
        var list = items.Select(i => (Party: i.Party ?? "", Size: i.Size ?? "", Type: i.Type ?? "")).ToList();
        var columns = list.Select(i => (i.Size, i.Type)).Distinct()
            .OrderBy(c => c.Size, StringComparer.Ordinal).ThenBy(c => c.Type, StringComparer.Ordinal).ToList();
        var rows = list.GroupBy(i => i.Party).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, (IReadOnlyList<int>)columns.Select(c => g.Count(i => i.Size == c.Size && i.Type == c.Type)).ToList()))
            .ToList();
        return new SummaryMatrix(caption, splitHeader, totalColumnLabel, totalRowLabel, columns, rows);
    }

    public IReadOnlyList<int> ColumnTotals =>
        Columns.Select((_, i) => Rows.Sum(r => r.Counts[i])).ToList();
}

/// <summary>
/// A Vector SSRS list report rebuilt (owner 2026-10-08): the same heading, the same columns in the same
/// order with the same headers and formats, the same summary — rendered as an A3 landscape PDF (every
/// column on one sheet, the type scaled down to fit) and as an Excel sheet carrying the same cells.
/// </summary>
internal sealed record ListReport(
    string FileName,
    double MarginInches,
    IReadOnlyList<ReportLine> Heading,
    IReadOnlyList<ReportLine> HeadingRight,
    IReadOnlyList<ReportLine> Preamble,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    SummaryMatrix Summary,
    string PageLabel)
{
    public const string PdfType = "application/pdf";
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const float BaseSize = 8f;
    private static readonly string SteelBlue = "#4682B4";

    /// <summary>A cell as the RDL prints it: the column's format applied, invariant culture, null as blank.</summary>
    public static string Text(object? value, string? format) => value switch
    {
        null => "",
        string s => s,
        // An RDL field with no format printed in the server's culture; the depot's own day-first form is used.
        DateTime d => d.ToString(format ?? "dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(format, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    // ── PDF ─────────────────────────────────────────────────────────────────

    public byte[] Pdf()
    {
        GeckoPdf.EnsureInitialised();
        var page = PageSizes.A3.Landscape();
        var margin = (float)(MarginInches * 72);
        var available = (page.Width - 2 * margin) / 72.0 * 2.54;   // cm
        var scale = (float)Math.Min(1.0, available / Columns.Sum(c => c.WidthCm));
        var size = BaseSize * scale;

        return Document.Create(doc => doc.Page(p =>
        {
            p.Size(page);
            p.Margin(margin);
            p.DefaultTextStyle(t => t.FontSize(size).FontFamily(GeckoPdf.LatinFont, GeckoPdf.ThaiFont));

            p.Header().PaddingBottom(4).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    foreach (var line in Heading) c.Item().Text(t => Styled(t.Span(line.Text), line, scale));
                });
                row.ConstantItem(180 * scale + 60).Column(c =>
                {
                    foreach (var line in HeadingRight) c.Item().Text(t => Styled(t.Span(line.Text), line, scale));
                });
            });

            p.Content().Column(c =>
            {
                foreach (var line in Preamble) c.Item().Text(t => Styled(t.Span(line.Text), line, scale));
                if (Preamble.Count > 0) c.Item().Height(6);
                c.Item().Element(Grid);
                c.Item().Height(14);
                c.Item().Element(x => Matrix(x, scale));
            });

            p.Footer().AlignRight().Text(t =>
            {
                t.Span(PageLabel + "  ").FontSize(size);
                t.CurrentPageNumber().FontSize(size);
            });
        })).GeneratePdf();
    }

    private static void Styled(TextSpanDescriptor span, ReportLine line, float scale)
    {
        span.FontSize(line.FontSize * scale);
        if (line.Bold) span.Bold();
    }

    private void Grid(IContainer container) => container.Table(table =>
    {
        table.ColumnsDefinition(cols =>
        {
            foreach (var c in Columns) cols.RelativeColumn((float)c.WidthCm);
        });
        table.Header(h =>
        {
            foreach (var c in Columns)
                h.Cell().Border(0.5f).Background(SteelBlue).Padding(2).AlignMiddle()
                    .Text(c.Header).Bold().FontColor(Colors.White);
        });
        foreach (var row in Rows)
            for (var i = 0; i < Columns.Count; i++)
                table.Cell().Border(0.5f).Padding(2).Text(Text(row[i], Columns[i].Format));
    });

    private void Matrix(IContainer container, float scale)
    {
        var s = Summary;
        container.Column(col =>
        {
            if (s.Caption is { } caption) col.Item().PaddingBottom(2).Text(caption).Bold();
            col.Item().AlignLeft().Width((float)((72 + 30 * (s.Columns.Count + 1)) * Math.Max(scale, 0.8f))).Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.ConstantColumn(72 * Math.Max(scale, 0.8f));
                    for (var i = 0; i <= s.Columns.Count; i++) cols.RelativeColumn();
                });

                IContainer Head(IContainer cell) => s.SplitHeader
                    ? cell.Border(0.5f).Padding(2)
                    : cell.Border(0.5f).Background(SteelBlue).Padding(2);
                void HeadText(IContainer cell, string text, bool bold = true)
                {
                    var t = Head(cell).AlignCenter().Text(text);
                    if (bold || !s.SplitHeader) t.Bold();
                    if (!s.SplitHeader) t.FontColor(Colors.White);
                }

                if (s.SplitHeader)
                {
                    // Row 1: the corner, each size over its types, Total; row 2: the types.
                    table.Cell().RowSpan(2).Border(0.5f);
                    foreach (var group in s.Columns.GroupBy(c => c.Size))
                        HeadText(table.Cell().ColumnSpan((uint)group.Count()), group.Key, bold: false);
                    HeadText(table.Cell().RowSpan(2), s.TotalColumnLabel);
                    foreach (var c in s.Columns) HeadText(table.Cell(), c.Type, bold: false);
                }
                else
                {
                    table.Cell().Border(0.5f).Background(SteelBlue);
                    foreach (var c in s.Columns) HeadText(table.Cell(), $"{c.Size} {c.Type}");
                    HeadText(table.Cell(), s.TotalColumnLabel);
                }

                var boldCounts = s.SplitHeader;
                void Count(int n, bool bold)
                {
                    var t = table.Cell().Border(0.5f).Padding(2).AlignCenter().Text(n.ToString(CultureInfo.InvariantCulture));
                    if (bold) t.Bold();
                }
                foreach (var (label, counts) in s.Rows)
                {
                    var t = table.Cell().Border(0.5f).Padding(2).Text(label);
                    if (s.SplitHeader) t.Bold();
                    foreach (var n in counts) Count(n, boldCounts);
                    Count(counts.Sum(), boldCounts);
                }
                table.Cell().Border(0.5f).Padding(2).Text(s.TotalRowLabel).Bold();
                foreach (var n in s.ColumnTotals) Count(n, boldCounts);
                Count(s.ColumnTotals.Sum(), boldCounts);
            });
        });
    }

    // ── Excel ───────────────────────────────────────────────────────────────

    public byte[] Xlsx()
    {
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Report");
        var r = 1;

        void Line(ReportLine line, int column = 1)
        {
            var cell = sheet.Cell(r, column);
            cell.Value = line.Text;
            cell.Style.Font.FontSize = line.FontSize;
            cell.Style.Font.Bold = line.Bold;
        }
        var right = Math.Max(2, Columns.Count - 2);
        for (var i = 0; i < Math.Max(Heading.Count, HeadingRight.Count); i++, r++)
        {
            if (i < Heading.Count) Line(Heading[i]);
            if (i < HeadingRight.Count) Line(HeadingRight[i], right);
        }
        foreach (var line in Preamble) { Line(line); r++; }
        r++;

        var headerRow = r;
        for (var i = 0; i < Columns.Count; i++)
        {
            var cell = sheet.Cell(r, i + 1);
            cell.Value = Columns[i].Header;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(SteelBlue);
            cell.Style.Alignment.WrapText = true;
            sheet.Column(i + 1).Width = Math.Max(6, Columns[i].WidthCm * 4.4);
        }
        r++;
        foreach (var row in Rows)
        {
            for (var i = 0; i < Columns.Count; i++)
            {
                var cell = sheet.Cell(r, i + 1);
                var format = Columns[i].Format;
                switch (row[i])
                {
                    case null: break;
                    case DateTime d: cell.Value = d; cell.Style.NumberFormat.Format = format ?? "dd-MM-yyyy HH:mm"; break;
                    case int n: cell.Value = n; if (format is not null) cell.Style.NumberFormat.Format = format; break;
                    case long n: cell.Value = n; if (format is not null) cell.Style.NumberFormat.Format = format; break;
                    case decimal m: cell.Value = m; if (format is not null) cell.Style.NumberFormat.Format = format; break;
                    default: cell.Value = Text(row[i], format); break;
                }
            }
            r++;
        }
        var grid = sheet.Range(headerRow, 1, Math.Max(headerRow, r - 1), Columns.Count);
        grid.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        grid.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        sheet.SheetView.FreezeRows(headerRow);
        r++;

        // The summary, under the grid as on the printed page.
        var s = Summary;
        if (s.Caption is { } caption) { sheet.Cell(r, 1).Value = caption; sheet.Cell(r, 1).Style.Font.Bold = true; r++; }
        var top = r;
        if (s.SplitHeader)
        {
            var c = 2;
            foreach (var group in s.Columns.GroupBy(x => x.Size))
            {
                sheet.Cell(r, c).Value = group.Key;
                if (group.Count() > 1) sheet.Range(r, c, r, c + group.Count() - 1).Merge();
                c += group.Count();
            }
            sheet.Cell(r, c).Value = s.TotalColumnLabel;
            sheet.Cell(r, c).Style.Font.Bold = true;
            sheet.Range(r, c, r + 1, c).Merge();
            r++;
            for (var i = 0; i < s.Columns.Count; i++) sheet.Cell(r, 2 + i).Value = s.Columns[i].Type;
            r++;
        }
        else
        {
            for (var i = 0; i < s.Columns.Count; i++) sheet.Cell(r, 2 + i).Value = $"{s.Columns[i].Size} {s.Columns[i].Type}";
            sheet.Cell(r, 2 + s.Columns.Count).Value = s.TotalColumnLabel;
            var head = sheet.Range(r, 1, r, 2 + s.Columns.Count);
            head.Style.Fill.BackgroundColor = XLColor.FromHtml(SteelBlue);
            head.Style.Font.FontColor = XLColor.White;
            head.Style.Font.Bold = true;
            r++;
        }
        foreach (var (label, counts) in s.Rows)
        {
            sheet.Cell(r, 1).Value = label;
            for (var i = 0; i < counts.Count; i++) sheet.Cell(r, 2 + i).Value = counts[i];
            sheet.Cell(r, 2 + counts.Count).Value = counts.Sum();
            if (s.SplitHeader) sheet.Range(r, 1, r, 2 + counts.Count).Style.Font.Bold = true;
            r++;
        }
        sheet.Cell(r, 1).Value = s.TotalRowLabel;
        var totals = s.ColumnTotals;
        for (var i = 0; i < totals.Count; i++) sheet.Cell(r, 2 + i).Value = totals[i];
        sheet.Cell(r, 2 + totals.Count).Value = totals.Sum();
        sheet.Range(r, 1, r, 2 + totals.Count).Style.Font.Bold = true;
        var matrix = sheet.Range(top, 1, r, 2 + s.Columns.Count);
        matrix.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        matrix.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        matrix.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.PaperSize = XLPaperSize.A3Paper;
        sheet.PageSetup.FitToPages(1, 0);
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }
}
