using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace Gecko.Revenue.Endpoints.Window;

/// <summary>
/// Reproducing a Vector SSRS layout (an .rdl) in QuestPDF: report items sit at the RDL's own
/// Top/Left/Width/Height, given here in inches as the RDL writes them; text boxes keep SSRS's 2pt padding
/// and solid 1pt black borders.
/// </summary>
internal static class RdlLayout
{
    public const float Inch = 72f;

    public static float In(double inches) => (float)(inches * Inch);

    /// <summary>A report item at its RDL position inside a <see cref="LayersDescriptor"/> whose primary layer sets the band height.
    /// Text that would overflow the box is shrunk to fit rather than spilling (SSRS grew the box instead).</summary>
    public static void At(this LayersDescriptor layers, double top, double left, double width, double height, Action<IContainer> content) =>
        layers.Layer().PaddingTop(In(top)).PaddingLeft(In(left)).Width(In(width)).Height(In(height)).ScaleToFit().Element(content);

    /// <summary>An SSRS cell: its solid borders, then the default 2pt padding.</summary>
    public static IContainer Box(this IContainer cell, bool top = false, bool left = false, bool right = false, bool bottom = false, float minHeight = 0)
    {
        if (top) cell = cell.BorderTop(1);
        if (left) cell = cell.BorderLeft(1);
        if (right) cell = cell.BorderRight(1);
        if (bottom) cell = cell.BorderBottom(1);
        if (minHeight > 0) cell = cell.MinHeight(minHeight);
        return cell.Padding(2);
    }
}
