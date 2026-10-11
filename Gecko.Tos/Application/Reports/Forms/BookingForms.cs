using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using static Gecko.Tos.Application.Reports.Forms.FormLayout;

namespace Gecko.Tos.Application.Reports.Forms;

/// <summary>
/// Vector's booking forms (Booking Entry → Print, the SCT menu: CY/CY, CFS and CYD), owner 2026-10-11 — the 13 RDLs
/// TMS.Booking.{CY,CYD,CFS}.* over one booking (Report.usp_Booking_CustomForm, here <see cref="BookingForm"/>). Each is the
/// RDL's items at their places (BookingFormItems.g.cs) and its tablixes, written here. The clerk picks the form; nothing
/// ties a form to a booking type, as on Vector. Defaults the owner may change (2026-10-11): line numbers as Vector's
/// RunningValue (an un-nominated place repeats the previous number); the customs office «ด่านศุลกากรบางเสาธง» and other
/// texts word for word; the CY delivery order's size totals per size (Vector printed the count of every line on each).
/// </summary>
internal static partial class BookingForms
{
    /// <summary>The forms by key, in the desktop menu's order, with the menu's names.</summary>
    public static readonly IReadOnlyList<(string Key, string Menu, Func<BookingForm, byte[]> Render)> All =
    [
        ("cy-cargo-release-permit", "CY/CY › Cargo Release Permit Form", f => FormLayout.Pdf(CyCargoReleasePermitItems(f), [GoodsTable(f, 4.020, 0.049, 0.969, 0.719, 0.255, 0.464)])),
        ("cy-container-release-permit", "CY/CY › Container Release Permit Form", f => FormLayout.Pdf(CyContainerReleasePermitItems(f), [BoxTable(f, 4.444, 0.072, 0.229, 0.229)])),
        ("cy-customs-permit", "CY/CY › Customs Permit", f => FormLayout.Pdf(CyCustomsPermitItems(f), [WeightTable(f, 2.978, 0.061, 0.888, 0.638), BoxTable(f, 6.283, 0.085, 0.229, 0.229)])),
        ("cy-delivery-order", "CY/CY › Delivery Order", f => FormLayout.Pdf(CyDeliveryOrderItems(f), [CyDeliveryOrderTable(f)])),
        ("cfs-cargo-release-permit-format-1", "CFS › Cargo Release Permit Form (Format 1)", f => FormLayout.Pdf(CfsCargoReleasePermitFormat1Items(f), [GoodsTable(f, 3.700, 0.064, 0.979, 0.729, 0.255, 0.464)])),
        ("cfs-cargo-release-form-format-3", "CFS › Cargo Release Permit Form (Format 3)", f => FormLayout.Pdf(CfsCargoReleaseFormat3Items(f), [PackagesTable(f), ManifestTable(f)])),
        ("cfs-container-release-permit", "CFS › Container Release Permit Form", f => FormLayout.Pdf(CfsContainerReleasePermitItems(f), [BoxTable(f, 4.360, 0.020, 0.229, 0.229)])),
        ("cfs-delivery-order", "CFS › Delivery Order", f => FormLayout.Pdf(CfsDeliveryOrderItems(f), [CfsDeliveryOrderTable(f)])),
        ("cfs-unstuffing-permit", "CFS › UnStuffing Permit Form", f => FormLayout.Pdf(CfsUnstuffingPermitItems(f), [BoxTable(f, 4.319, 0.072, 0.229, 0.229)])),
        ("cyd-cargo-release-permit", "CYD › Cargo Release Permit Form", f => FormLayout.Pdf(CydCargoReleasePermitItems(f), [GoodsTable(f, 3.978, 0.061, 1.000, 0.750, 0.255, 0.464)])),
        ("cyd-container-release-permit", "CYD › Container Release Permit Form", f => FormLayout.Pdf(CydContainerReleasePermitItems(f), [BoxTable(f, 4.399, 0.217, 0.264, 0.264)])),
        ("cyd-delivery-order", "CYD › Delivery Order", f => FormLayout.Pdf(CydDeliveryOrderItems(f), [CydDeliveryOrderTable(f)])),
        ("cyd-customs-permit", "CYD › Customs Permit Form", f => FormLayout.Pdf(CydCustomsPermitItems(f), [WeightTable(f, 2.853, 0.061, 0.781, 0.531), BoxTable(f, 6.117, 0.074, 0.271, 0.740, spacer: 0.469)])),
    ];

    // ── table pieces ──

    private const float Small = 8f;
    private const float Body = 9f;

    private static IContainer Cell(IContainer cell, bool border, double minHeight) =>
        (border ? cell.Border(0.5f) : cell).MinHeight(In(minHeight)).Padding(2);

    private static void Text(IContainer cell, string text, float size, Align align = Align.Left)
    {
        cell = align switch { Align.Center => cell.AlignCenter(), Align.Right => cell.AlignRight(), _ => cell.AlignLeft() };
        cell.Text(text).FontSize(size).LineHeight(1);
    }

    /// <summary>The container list: a line per booked box ("n. CONTAINER SIZE TYPE"), then the RDL's blank spacer row.</summary>
    private static void Boxes(IContainer container, BookingForm f, double lineHeight, double spacer, bool border = false) =>
        (border ? container.Border(0.5f) : container).Column(column =>
        {
            foreach (var line in f.BoxLines) column.Item().MinHeight(In(lineHeight)).Padding(2).Text(line).FontSize(Body);
            if (spacer > 0) column.Item().Height(In(spacer));
        });

    /// <summary>The permit forms' box list (Tablix6): no header, no border, a line per box.</summary>
    private static FormTable BoxTable(BookingForm f, double top, double left, double lineHeight, double height, double spacer = 0) =>
        new(top, left, height, c => c.Width(In(2.152)).Element(x => Boxes(x, f, lineHeight, spacer)));

    /// <summary>The cargo release permits' goods table: marks, packages, goods, and the box list.</summary>
    private static FormTable GoodsTable(BookingForm f, double top, double left, double height, double row, double line, double spacer) =>
        new(top, left, height, c => c.Width(In(7.774)).Table(table =>
        {
            table.ColumnsDefinition(cols => { foreach (var w in new[] { 1.858, 1.715, 2.049, 2.152 }) cols.ConstantColumn(In(w)); });
            table.Header(h =>
            {
                foreach (var text in new[]
                         {
                             "เครื่องหมายและเลขหมาย\nMARKS & NOS", "จำนวนและลักษณะหีบห่อ\nNo. & DESCRIPTION",
                             "รายการสินค้า\nPARTICULARS OF GOODS", "เบอร์ตู้สินค้า\nCONTAINER NO.",
                         })
                    Text(Cell(h.Cell(), true, 0.25), text, Body, Align.Center);
            });
            Text(Cell(table.Cell(), true, row), " " + S(f.MarksAndNos), Body, Align.Center);
            Text(Cell(table.Cell(), true, row), " " + S(f.TotalQty) + " " + S(f.UOM), Body, Align.Center);
            Text(Cell(table.Cell(), true, row), " " + S(f.CommodityDesc), Body, Align.Center);
            table.Cell().Border(0.5f).Element(x => Boxes(x, f, line, spacer));
        }));

    /// <summary>The customs permits' table: marks, packages, goods, weight (one row).</summary>
    private static FormTable WeightTable(BookingForm f, double top, double left, double height, double row) =>
        new(top, left, height, c => c.Width(In(7.774)).Table(table =>
        {
            table.ColumnsDefinition(cols => { foreach (var w in new[] { 1.858, 1.715, 2.049, 2.152 }) cols.ConstantColumn(In(w)); });
            table.Header(h =>
            {
                foreach (var text in new[] { "เครื่องหมาย", "จำนวนหีบห่อ", "รายการสินค้า", "น้ำหนัก" })
                    Text(Cell(h.Cell(), true, 0.25), text, Body, Align.Center);
            });
            Text(Cell(table.Cell(), true, row), " " + S(f.MarksAndNos), Body, Align.Center);
            Text(Cell(table.Cell(), true, row), " " + S(f.TotalQty) + " " + S(f.UOM), Body, Align.Center);
            Text(Cell(table.Cell(), true, row), " " + S(f.CommodityDesc), Body, Align.Center);
            Text(Cell(table.Cell(), true, row), " " + S(f.TotalWeight), Body, Align.Center);
        }));

    private static void DeliveryHeader(TableDescriptor table)
    {
        table.ColumnsDefinition(cols => { foreach (var w in new[] { 2.447, 2.638, 2.741 }) cols.ConstantColumn(In(w)); });
        table.Header(h =>
        {
            foreach (var text in new[] { "เครื่องหมายและเลขหมาย", "ซีลเลขที่", "จำนวนหีบห่อ" })
                Text(Cell(h.Cell(), true, 0.25), text, Body, Align.Center);
        });
    }

    /// <summary>CY delivery order: the box list, then "รพส." + terminal, "จำนวนรวม" and a line per size — the boxes of that size
    /// (Vector printed the count of every line on each size).</summary>
    private static FormTable CyDeliveryOrderTable(BookingForm f) =>
        new(3.819, 0.075, 1.302, c => c.Width(In(7.826)).Table(table =>
        {
            DeliveryHeader(table);
            table.Cell().Border(0.5f).Element(x => Boxes(x, f, 0.245, 0.453));
            Cell(table.Cell(), true, 0.698);
            Cell(table.Cell(), true, 0.698);
            Text(Cell(table.Cell(), false, 0.354), "รพส." + S(f.Terminal), Body);
            Text(Cell(table.Cell(), true, 0.354), "จำนวนรวม", Small, Align.Right);
            table.Cell().Border(0.5f).Column(sizes =>
            {
                foreach (var size in f.Boxes.GroupBy(b => b.Size))
                    sizes.Item().MinHeight(In(0.354)).Padding(2).Text($" {size.Count()} x {size.Key}").FontSize(Body);
            });
        }));

    /// <summary>CYD delivery order: marks and packages, the box list, then "รพส." + terminal and the keyed package total.</summary>
    private static FormTable CydDeliveryOrderTable(BookingForm f) =>
        new(3.819, 0.037, 1.760, c => c.Width(In(7.826)).Table(table =>
        {
            DeliveryHeader(table);
            var packages = S(f.TotalQty) + " " + S(f.UOM);
            Text(Cell(table.Cell(), false, 0.25), " " + S(f.MarksAndNos), Body);
            Cell(table.Cell(), true, 0.25);
            Text(Cell(table.Cell(), true, 0.25), packages, Small, Align.Center);
            table.Cell().Border(0.5f).Element(x => Boxes(x, f, 0.292, 0.490));
            Cell(table.Cell(), true, 0.781);
            Cell(table.Cell(), true, 0.781);
            Text(Cell(table.Cell(), false, 0.25), "รพส." + S(f.Terminal), Body);
            Text(Cell(table.Cell(), true, 0.25), "จำนวนรวม", Small, Align.Right);
            Text(Cell(table.Cell(), true, 0.25), packages, Small, Align.Center);
        }));

    /// <summary>CFS delivery order: marks and packages (no box list), then "รพส." + terminal and the keyed package total.</summary>
    private static FormTable CfsDeliveryOrderTable(BookingForm f) =>
        new(3.819, 0.037, 1.104, c => c.Width(In(7.826)).Table(table =>
        {
            DeliveryHeader(table);
            var packages = S(f.TotalQty) + " " + S(f.UOM);
            Text(Cell(table.Cell(), true, 0.51042), S(f.MarksAndNos), Body);
            Cell(table.Cell(), true, 0.51042);
            Text(Cell(table.Cell(), true, 0.51042), packages, Small, Align.Center);
            Text(Cell(table.Cell(), false, 0.34375), "รพส." + S(f.Terminal), Body);
            Text(Cell(table.Cell(), true, 0.34375), "จำนวนรวม", Small, Align.Right);
            Text(Cell(table.Cell(), true, 0.34375), packages, Small, Align.Center);
        }));

    /// <summary>CFS Format 3's package table (Tablix5): marks, the package size (width / length / height, left for the clerk) and count.</summary>
    private static FormTable PackagesTable(BookingForm f) =>
        new(2.903, 0.061, 0.760, c => c.Width(In(7.841)).Table(table =>
        {
            var widths = new[] { 2.17911, 0.88754, 0.83693, 0.89113, 1.04182, 1.06106, 0.94306 };
            table.ColumnsDefinition(cols => { foreach (var w in widths) cols.ConstantColumn(In(w)); });
            table.Header(h =>
            {
                Text(h.Cell().RowSpan(2).Border(0.5f).Padding(2), "เครื่องหมาย", Body, Align.Center);
                Text(h.Cell().ColumnSpan(3).Border(0.5f).MinHeight(In(0.28125)).Padding(2), "ขนาดแต่ละหีบห่อ", Body, Align.Center);
                foreach (var text in new[] { "จำนวนหีบห่อ", "ปริมาตร", "น้ำหนักรวม" })
                    Text(h.Cell().RowSpan(2).Border(0.5f).Padding(2), text, Body, Align.Center);
                foreach (var text in new[] { "กว้าง", "ยาว", "สูง" })
                    Text(Cell(h.Cell(), true, 0.25), text, Body, Align.Center);
            });
            Text(Cell(table.Cell(), true, 0.22917), " " + S(f.MarksAndNos), Body, Align.Center);
            for (var i = 0; i < 3; i++) Cell(table.Cell(), true, 0.22917);
            Text(Cell(table.Cell(), true, 0.22917), " " + S(f.TotalQty) + " " + S(f.UOM), Body, Align.Center);
            Cell(table.Cell(), true, 0.22917);
            Cell(table.Cell(), true, 0.22917);
        }));

    /// <summary>CFS Format 3's manifest table (Tablix7): the goods with the box list, and the manifest's packages, volume and weight.</summary>
    private static FormTable ManifestTable(BookingForm f) =>
        new(3.759, 0.061, 1.203, c => c.Width(In(7.841)).Table(table =>
        {
            table.ColumnsDefinition(cols => { foreach (var w in new[] { 2.187, 1.99189, 1.91532, 1.74642 }) cols.ConstantColumn(In(w)); });
            table.Header(h =>
            {
                Text(h.Cell().Border(0.5f).MinHeight(In(0.28125)).Padding(2), "รายการสินค้า", Body, Align.Center);
                Text(h.Cell().ColumnSpan(3).Border(0.5f).MinHeight(In(0.28125)).Padding(2), "บัญชีเรือสำแดง", Body, Align.Center);
                Text(Cell(h.Cell(), true, 0.25), S(f.CommodityDesc), Body, Align.Center);
                foreach (var text in new[] { "จำนวนหีบห่อ", "ปริมาตร", "น้ำหนักรวม" }) Text(Cell(h.Cell(), true, 0.25), text, Body, Align.Center);
            });
            table.Cell().Border(0.5f).Element(x => Boxes(x, f, 0.25, 0.42188));
            Text(Cell(table.Cell(), true, 0.67188), " " + S(f.TotalQty) + " " + S(f.UOM), Body, Align.Center);
            Text(Cell(table.Cell(), true, 0.67188), S(f.TotalVolume), Body, Align.Center);
            Text(Cell(table.Cell(), true, 0.67188), S(f.TotalWeight), Body, Align.Center);
        }));
}
