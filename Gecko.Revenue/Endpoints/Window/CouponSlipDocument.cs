using System.Globalization;
using Gecko.Data.Documents;
using Gecko.MasterData.Contracts;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using static Gecko.Revenue.Endpoints.Window.RdlLayout;

namespace Gecko.Revenue.Endpoints.Window;

/// <summary>
/// The coupon receipt as Vector's KPS depots printed it (Tms_CouponReceipt_KPS.rdl, Report.usp_CouponReceiptKPS): one
/// Letter-landscape "บิลเงินสด / CASH SALE" bill per receipt — bill no., date, name and address; the receipt's lines
/// (quantity, charge name, unit price, amount) padded to 8 rows; the total; the collector's signature line. The money is the
/// receipt's (gecko_revenue). A voided receipt's bill still prints, marked VOID.
///
/// Two deliberate differences: the bill's total is the receipt's total (the RDL summed the header total over every line,
/// so a two-line bill printed it twice); and a VOIDED receipt carries the VOID mark.
/// </summary>
internal sealed class CouponSlipDocument(ReceiptDocument receipts)
{
    public sealed record Rendered(string FileName, byte[] Pdf);

    /// <summary>The proc pads the lines to 8 so the bill is always the same height.</summary>
    private const int BilledRows = 8;

    public async Task<Rendered?> RenderAsync(Guid receiptId, CancellationToken ct)
    {
        var r = await receipts.ReadAsync(receiptId, ct);
        if (r is null) return null;
        var facts = await receipts.PrintFactsAsync(r, ct);
        return new Rendered($"{r.ReceiptNo.Replace('/', '-')}-coupons.pdf", Render(r, facts));
    }

    internal static byte[] Render(ReceiptResponse r, ReceiptPrintFacts? facts = null)
    {
        static string Money(decimal value) => value.ToString("#,0.00;(#,0.00)", CultureInfo.InvariantCulture);
        var voided = r.Status == "VOIDED";
        // The proc's rows: lines with a rate (InDt.SellRate > 0), one row each.
        var lines = r.Lines.Where(l => l.UnitRate > 0).OrderBy(l => l.LineNo).ToList();

        GeckoPdf.EnsureInitialised();
        return Document.Create(document => document.Page(page =>
        {
            GeckoPdf.Page(page);
            page.Size(new PageSize(In(11), In(8.5)));
            page.Margin(In(0.1));
            page.DefaultTextStyle(t => t.FontSize(10).FontFamily(GeckoPdf.LatinFont, GeckoPdf.ThaiFont));

            // Page header (2.125in, every page).
            page.Header().Height(In(2.125)).Layers(layers =>
            {
                layers.PrimaryLayer();
                layers.At(0.13417, 0.12375, 0.68264, 0.25, c => c.Text("เลขที่"));
                layers.At(0.45361, 0.12375, 0.68264, 0.25, c => c.Text("Bill No."));
                layers.At(0.45361, 0.87375, 2.08333, 0.25, c => c.BorderBottom(1).Text(r.ReceiptNo));
                layers.At(0.83854, 0.12375, 7.95833, 0.25, c => c.AlignCenter().Text("บิลเงินสด"));
                layers.At(1.15799, 0.12375, 7.95833, 0.25, c => c.AlignCenter().Text("CASH SALE"));
                layers.At(1.47743, 7.05083, 1.03125, 0.25, c => c.Text(r.ReceiptAt.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)));
                layers.At(1.47952, 0.12375, 1.42708, 0.25, c => c.Text("นาม/Name"));
                layers.At(1.79896, 0.12375, 1.42708, 0.25, c => c.Text("ที่อยู่/Address"));
                layers.At(1.47952, 1.62027, 3.85417, 0.25, c => c.Text(r.PayerName));
                layers.At(1.79896, 1.62027, 6.46181, 0.25, c => c.Text(r.PayerAddress ?? ""));
            });

            page.Content().PaddingTop(In(0.06944)).PaddingLeft(In(0.12375)).Width(In(8.45834)).Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    foreach (var width in new[] { 0.68264, 5.93403, 0.78542, 1.05625 }) cols.ConstantColumn(In(width));
                });

                var row = In(0.25);
                foreach (var label in new[] { "จำนวน", "รายการ", "หน่วยละ", "จำนวนเงิน" })
                    table.Cell().Box(top: true, left: true, right: true, minHeight: row).AlignCenter().Text(label);
                foreach (var label in new[] { "Quantity", "Description", "Unit Price", "Amount" })
                    table.Cell().Box(left: true, right: true, bottom: true, minHeight: row).AlignCenter().Text(label);

                foreach (var l in lines)
                {
                    table.Cell().Box(left: true, right: true, minHeight: row).AlignCenter().Text(l.Quantity.ToString("0;(0)", CultureInfo.InvariantCulture));
                    table.Cell().Box(left: true, right: true, minHeight: row).Text(facts?.ChargeNames.GetValueOrDefault(l.ChargeCode) ?? l.Description);
                    table.Cell().Box(minHeight: row).AlignRight().Text(Money(l.UnitRate!.Value));
                    table.Cell().Box(left: true, right: true, minHeight: row).AlignRight().Text(Money(l.Amount));
                }
                for (var i = lines.Count; i < BilledRows; i++)
                {
                    table.Cell().Box(left: true, right: true, minHeight: row);
                    table.Cell().Box(left: true, right: true, minHeight: row);
                    table.Cell().Box(minHeight: row);
                    table.Cell().Box(left: true, right: true, minHeight: row);
                }

                table.Cell().Box(top: true, left: true, right: true, minHeight: row).AlignCenter().Text("บาท");
                table.Cell().Box(top: true, left: true, right: true, minHeight: row);
                table.Cell().Box(top: true, left: true, right: true, minHeight: row).AlignCenter().Text("รวมเงิน");
                table.Cell().Box(top: true, left: true, right: true, minHeight: row);
                table.Cell().Box(left: true, right: true, bottom: true, minHeight: row).AlignCenter().Text("Baht");
                table.Cell().Box(left: true, right: true, bottom: true, minHeight: row);
                table.Cell().Box(left: true, right: true, bottom: true, minHeight: row).AlignCenter().Text("Total");
                table.Cell().Box(left: true, right: true, bottom: true, minHeight: row).AlignRight().Text(Money(r.Total));
            });

            // Page footer (1in, every page): the collector signs.
            page.Footer().Height(In(1)).Layers(layers =>
            {
                layers.PrimaryLayer();
                layers.At(0.16195, 0.12375, 0.68264, 0.25, c => c.Text("ผู้รับเงิน"));
                layers.At(0.46751, 0.97854, 5.33333, 0.25, c => c.BorderBottom(1));
                layers.At(0.48139, 0.12375, 0.68264, 0.25, c => c.Text("Collector"));
            });

            if (voided) page.Foreground().VoidWatermark();
        })).GeneratePdf();
    }
}
