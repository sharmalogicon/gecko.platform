using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Gecko.Data.Documents;

/// <summary>
/// The one place GECKO's printed documents (EIR, receipt / tax invoice) get their
/// licence, fonts and house style.
///
/// Fonts: Noto Sans for Latin text, falling back to Noto Sans Thai for Thai —
/// both embedded in this assembly (SIL OFL), because customer and depot names are
/// often Thai and a cloud host has no Tahoma to fall back on.
///
/// Licence: QuestPDF Community — free while annual gross revenue is under US$1M.
/// </summary>
public static class GeckoPdf
{
    public const string LatinFont = "Noto Sans";
    public const string ThaiFont = "Noto Sans Thai";
    private static readonly Lazy<bool> Ready = new(Initialise);

    public static void EnsureInitialised() => _ = Ready.Value;

    private static bool Initialise()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var assembly = typeof(GeckoPdf).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
        {
            using var font = assembly.GetManifestResourceStream(name)!;
            FontManager.RegisterFontFromStream(font);
        }
        return true;
    }

    /// <summary>A4 page, margins, base text style with the Thai fallback.</summary>
    public static void Page(PageDescriptor page)
    {
        page.Size(PageSizes.A4);
        page.Margin(28);
        page.DefaultTextStyle(t => t.FontSize(9).FontFamily(LatinFont, ThaiFont));
    }

    /// <summary>A labelled value, the building block of a document header.</summary>
    public static void Field(this ColumnDescriptor column, string label, string? value)
    {
        column.Item().Text(text =>
        {
            text.Span(label + "  ").FontSize(7).FontColor(Colors.Grey.Darken1);
            text.Span(string.IsNullOrWhiteSpace(value) ? "—" : value).SemiBold();
        });
    }

    /// <summary>Diagonal VOID across the page: a voided document keeps its number and is still printed (TOS Q4, billing Q4).</summary>
    public static void VoidWatermark(this IContainer container) =>
        container.AlignCenter().AlignMiddle().Rotate(-30)
            .Text("VOID").FontSize(110).Bold().FontColor(Colors.Red.Lighten3);
}
