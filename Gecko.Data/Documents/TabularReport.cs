using System.Globalization;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Gecko.Data.Documents;

/// <summary>A line of a report's heading or preamble, sized as the RDL sizes it (points).</summary>
public sealed record ReportText(string Text, float FontSize = 8, bool Bold = false);

public enum CellAlign { Left, Center, Right }

/// <summary>
/// One grid column: width in cm (scaled to the page with the others), the display format
/// (.NET and Excel read the same patterns: dd/MM/yy, #,0.00;(#,0.00)) and alignment.
/// </summary>
public sealed record TabularColumn(double WidthCm, string? Format = null, CellAlign Align = CellAlign.Left);

/// <summary>A header cell; spans let an RDL's two-row merged headers be drawn as it draws them.</summary>
public sealed record HeaderCell(string Text, int ColSpan = 1, int RowSpan = 1);

public enum RowKind
{
    Detail,

    /// <summary>One text across the whole width (a group or section heading, e.g. "CANCELLED RECEIPTS").</summary>
    Banner,

    /// <summary>A group's sums: bold.</summary>
    Subtotal,

    /// <summary>The report's sums: bold, shaded.</summary>
    Total,
}

/// <summary>Cells line up with the columns; a Banner row carries its text in the first cell.</summary>
public sealed record TabularRow(IReadOnlyList<object?> Cells, RowKind Kind = RowKind.Detail)
{
    public static TabularRow Banner(string text) => new([text], RowKind.Banner);
}

/// <summary>
/// A table under the main grid, as an RDL places a second tablix below the first (e.g. the Company listing's
/// "Summary by Charge Code"): an optional caption above it, its own columns and header rows.
/// </summary>
public sealed record TabularBlock(
    string? Caption,
    IReadOnlyList<TabularColumn> Columns,
    IReadOnlyList<IReadOnlyList<HeaderCell>> HeaderRows,
    IReadOnlyList<TabularRow> Rows);

public enum ReportPage { A4Portrait, A4Landscape, A3Landscape }

/// <summary>
/// A Vector SSRS report rebuilt (owner 2026-10-08 / 2026-10-10): the RDL's heading, columns in order with
/// their exact headers and formats, its group and total rows — as a PDF (every column on one sheet, type
/// scaled down only if the columns do not fit) and as an Excel sheet carrying the same cells.
///
/// <c>Heading</c>/<c>HeadingRight</c> repeat on every page (the RDL page header); <c>Preamble</c>/<c>PreambleRight</c>
/// print once above the grid (body textboxes); the column headers repeat on every page. <c>After</c> holds the
/// tables printed once below the grid.
/// </summary>
public sealed record TabularReport(
    string FileName,
    ReportPage Page,
    double MarginCm,
    IReadOnlyList<ReportText> Heading,
    IReadOnlyList<ReportText> HeadingRight,
    IReadOnlyList<ReportText> Preamble,
    IReadOnlyList<ReportText> PreambleRight,
    IReadOnlyList<TabularColumn> Columns,
    IReadOnlyList<IReadOnlyList<HeaderCell>> HeaderRows,
    IReadOnlyList<TabularRow> Rows,
    string PageLabel = "Page No#",
    IReadOnlyList<TabularBlock>? After = null)
{
    public const string PdfType = "application/pdf";
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const float BaseSize = 8f;
    private const string HeaderFill = "#D3D3D3";
    private const string TotalFill = "#F2F2F2";

    private TabularBlock Main => new(null, Columns, HeaderRows, Rows);

    private IEnumerable<TabularBlock> Blocks => After is null ? [Main] : [Main, .. After];

    /// <summary>A cell as the RDL prints it: the column's format applied, invariant culture, null as blank.</summary>
    public static string Text(object? value, string? format) => value switch
    {
        null => "",
        string s => s,
        DateTime d => d.ToString(format ?? "dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString(format ?? "dd/MM/yyyy", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(format, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>Where each header cell lands (row, first column), spans honoured — the same layout for PDF and Excel.</summary>
    public static IReadOnlyList<(int Row, int Column, HeaderCell Cell)> HeaderLayout(TabularBlock block)
    {
        var width = block.Columns.Count;
        var taken = new bool[block.HeaderRows.Count, width];
        var placed = new List<(int, int, HeaderCell)>();
        for (var r = 0; r < block.HeaderRows.Count; r++)
        {
            var c = 0;
            foreach (var cell in block.HeaderRows[r])
            {
                while (c < width && taken[r, c]) c++;
                if (c >= width) throw new InvalidOperationException($"Header row {r} is wider than the {width} columns.");
                for (var rr = r; rr < Math.Min(block.HeaderRows.Count, r + cell.RowSpan); rr++)
                    for (var cc = c; cc < Math.Min(width, c + cell.ColSpan); cc++) taken[rr, cc] = true;
                placed.Add((r, c, cell));
                c += cell.ColSpan;
            }
        }
        return placed;
    }

    // ── PDF ─────────────────────────────────────────────────────────────────

    public byte[] Pdf()
    {
        GeckoPdf.EnsureInitialised();
        var page = Page switch
        {
            ReportPage.A4Portrait => PageSizes.A4,
            ReportPage.A4Landscape => PageSizes.A4.Landscape(),
            _ => PageSizes.A3.Landscape(),
        };
        var margin = (float)(MarginCm / 2.54 * 72);
        var available = (page.Width - 2 * margin) / 72.0 * 2.54;   // cm
        var scale = (float)Math.Min(1.0, available / Columns.Sum(c => c.WidthCm));
        var size = BaseSize * scale;
        var pointsPerCm = (float)((page.Width - 2 * margin) / Math.Max(available, Columns.Sum(c => c.WidthCm)));

        return Document.Create(doc => doc.Page(p =>
        {
            p.Size(page);
            p.Margin(margin);
            p.DefaultTextStyle(t => t.FontSize(size).FontFamily(GeckoPdf.LatinFont, GeckoPdf.ThaiFont));

            p.Header().PaddingBottom(4).Row(row =>
            {
                row.RelativeItem().Column(c => Lines(c, Heading, scale));
                row.ConstantItem(200 * scale + 40).Column(c => Lines(c, HeadingRight, scale));
            });

            p.Content().Column(c =>
            {
                if (Preamble.Count + PreambleRight.Count > 0)
                {
                    c.Item().Row(row =>
                    {
                        row.RelativeItem().Column(x => Lines(x, Preamble, scale));
                        row.ConstantItem(200 * scale + 40).Column(x => Lines(x, PreambleRight, scale));
                    });
                    c.Item().Height(6);
                }
                c.Item().Element(x => Grid(x, Main));
                foreach (var block in After ?? [])
                {
                    c.Item().Height(10);
                    if (block.Caption is { } caption) c.Item().PaddingBottom(2).Text(caption).Bold();
                    // A later table keeps its own width (cm), on the same scale as the grid.
                    c.Item().AlignLeft().Width((float)block.Columns.Sum(x => x.WidthCm) * pointsPerCm).Element(x => Grid(x, block));
                }
            });

            p.Footer().AlignRight().Text(t =>
            {
                t.Span(PageLabel + " ").FontSize(size);
                t.CurrentPageNumber().FontSize(size);
            });
        })).GeneratePdf();
    }

    private static void Lines(ColumnDescriptor column, IReadOnlyList<ReportText> lines, float scale)
    {
        foreach (var line in lines)
            column.Item().Text(t =>
            {
                var span = t.Span(line.Text).FontSize(line.FontSize * scale);
                if (line.Bold) span.Bold();
            });
    }

    private static void Grid(IContainer container, TabularBlock block) => container.Table(table =>
    {
        var columns = block.Columns;
        table.ColumnsDefinition(cols =>
        {
            foreach (var c in columns) cols.RelativeColumn((float)c.WidthCm);
        });
        if (block.HeaderRows.Count > 0)
            table.Header(h =>
            {
                foreach (var (row, column, cell) in HeaderLayout(block))
                    h.Cell().Row((uint)row + 1).Column((uint)column + 1).RowSpan((uint)cell.RowSpan).ColumnSpan((uint)cell.ColSpan)
                        .Border(0.5f).Background(HeaderFill).Padding(2).AlignCenter().AlignMiddle()
                        .Text(cell.Text).Bold();
            });
        foreach (var row in block.Rows)
        {
            if (row.Kind == RowKind.Banner)
            {
                table.Cell().ColumnSpan((uint)columns.Count).Border(0.5f).Padding(2)
                    .Text(Text(row.Cells.FirstOrDefault(), null)).Bold();
                continue;
            }
            for (var i = 0; i < columns.Count; i++)
            {
                var cell = table.Cell().Border(0.5f);
                if (row.Kind == RowKind.Total) cell = cell.Background(TotalFill);
                cell = columns[i].Align switch
                {
                    CellAlign.Right => cell.Padding(2).AlignRight(),
                    CellAlign.Center => cell.Padding(2).AlignCenter(),
                    _ => cell.Padding(2),
                };
                var text = cell.Text(Text(i < row.Cells.Count ? row.Cells[i] : null, columns[i].Format));
                if (row.Kind is RowKind.Subtotal or RowKind.Total) text.Bold();
            }
        }
    });

    // ── Excel ───────────────────────────────────────────────────────────────

    /// <summary>In an Excel format "/" is the viewer's locale date separator; the RDL means a slash.</summary>
    private static string ExcelDate(string format) => format.Replace("/", @"\/");

    public byte[] Xlsx()
    {
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Report");
        var last = Columns.Count;
        var right = Math.Max(2, last - 1);
        var r = 1;

        void Line(ReportText line, int column)
        {
            var cell = sheet.Cell(r, column);
            cell.Value = line.Text;
            cell.Style.Font.FontSize = line.FontSize;
            cell.Style.Font.Bold = line.Bold;
        }
        void Block(IReadOnlyList<ReportText> left, IReadOnlyList<ReportText> rightLines)
        {
            for (var i = 0; i < Math.Max(left.Count, rightLines.Count); i++, r++)
            {
                if (i < left.Count) Line(left[i], 1);
                if (i < rightLines.Count) Line(rightLines[i], right);
            }
        }
        Block(Heading, HeadingRight);
        Block(Preamble, PreambleRight);
        r++;

        for (var i = 0; i < last; i++) sheet.Column(i + 1).Width = Math.Max(6, Columns[i].WidthCm * 4.4);
        var headerBottom = Write(sheet, Main, ref r);
        sheet.SheetView.FreezeRows(headerBottom);

        foreach (var block in After ?? [])
        {
            r++;
            if (block.Caption is { } caption)
            {
                sheet.Cell(r, 1).Value = caption;
                sheet.Cell(r, 1).Style.Font.Bold = true;
                r++;
            }
            Write(sheet, block, ref r);
        }

        sheet.PageSetup.PageOrientation = Page == ReportPage.A4Portrait ? XLPageOrientation.Portrait : XLPageOrientation.Landscape;
        sheet.PageSetup.PaperSize = Page == ReportPage.A3Landscape ? XLPaperSize.A3Paper : XLPaperSize.A4Paper;
        sheet.PageSetup.FitToPages(1, 0);
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>Writes one table from row <paramref name="r"/> on (left at column 1); returns its last header row.</summary>
    private static int Write(IXLWorksheet sheet, TabularBlock block, ref int r)
    {
        var last = block.Columns.Count;
        var top = r;
        foreach (var (row, column, cell) in HeaderLayout(block))
        {
            var range = sheet.Range(top + row, column + 1, top + row + cell.RowSpan - 1, column + cell.ColSpan);
            if (cell.RowSpan > 1 || cell.ColSpan > 1) range.Merge();
            range.FirstCell().Value = cell.Text;
        }
        if (block.HeaderRows.Count > 0)
        {
            var header = sheet.Range(top, 1, top + block.HeaderRows.Count - 1, last);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml(HeaderFill);
            header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            header.Style.Alignment.WrapText = true;
        }
        r = top + block.HeaderRows.Count;
        var headerBottom = r - 1;

        foreach (var row in block.Rows)
        {
            if (row.Kind == RowKind.Banner)
            {
                var banner = sheet.Range(r, 1, r, last).Merge();
                banner.FirstCell().Value = Text(row.Cells.FirstOrDefault(), null);
                banner.Style.Font.Bold = true;
                r++;
                continue;
            }
            for (var i = 0; i < last; i++)
            {
                var cell = sheet.Cell(r, i + 1);
                var format = block.Columns[i].Format;
                switch (i < row.Cells.Count ? row.Cells[i] : null)
                {
                    case null: break;
                    case DateTime d: cell.Value = d; cell.Style.NumberFormat.Format = ExcelDate(format ?? "dd/MM/yyyy HH:mm"); break;
                    case DateOnly d: cell.Value = d.ToDateTime(TimeOnly.MinValue); cell.Style.NumberFormat.Format = ExcelDate(format ?? "dd/MM/yyyy"); break;
                    case int n: cell.Value = n; if (format is not null) cell.Style.NumberFormat.Format = format; break;
                    case long n: cell.Value = n; if (format is not null) cell.Style.NumberFormat.Format = format; break;
                    case decimal m: cell.Value = m; if (format is not null) cell.Style.NumberFormat.Format = format; break;
                    case var other: cell.Value = Text(other, format); break;
                }
                cell.Style.Alignment.Horizontal = block.Columns[i].Align switch
                {
                    CellAlign.Right => XLAlignmentHorizontalValues.Right,
                    CellAlign.Center => XLAlignmentHorizontalValues.Center,
                    _ => XLAlignmentHorizontalValues.Left,
                };
            }
            if (row.Kind is RowKind.Subtotal or RowKind.Total) sheet.Range(r, 1, r, last).Style.Font.Bold = true;
            if (row.Kind == RowKind.Total) sheet.Range(r, 1, r, last).Style.Fill.BackgroundColor = XLColor.FromHtml(TotalFill);
            r++;
        }

        var grid = sheet.Range(top, 1, Math.Max(top, r - 1), last);
        grid.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        grid.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        return headerBottom;
    }
}
