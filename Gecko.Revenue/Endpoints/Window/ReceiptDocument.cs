using System.Globalization;
using Gecko.Data.Documents;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Application;
using Gecko.Revenue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Gecko.Revenue.Endpoints.Window;

/// <summary>
/// A receipt as printed — the JSON the window shows and the A4 PDF, a Thai full
/// tax invoice (ใบเสร็จรับเงิน/ใบกำกับภาษี, Revenue Code s.86/4).
///
/// Everything money-related comes from gecko_revenue rows written when the receipt
/// was issued; the SELLER block is read from MDM at print time (a company's
/// address can move, the receipt number cannot). A field MDM does not hold prints
/// as "(not set)" — it is never made up. Times are the branch's clock.
///
/// A VOIDED receipt still prints, with its number and the VOID mark: in Thailand a
/// tax-invoice number that vanishes is a gap the Revenue Department asks about.
/// </summary>
internal sealed class ReceiptDocument(RevenueDbContext db, IMasterDataReferences master, BranchCalendar calendar)
{
    public sealed record Rendered(string FileName, byte[] Pdf);

    public async Task<ReceiptResponse?> ReadAsync(Guid receiptId, CancellationToken ct, IReadOnlyList<CouponResponse>? issued = null)
    {
        var receipt = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.ReceiptId == receiptId, ct);
        if (receipt is null) return null;

        var lines = await (
                from l in db.ReceiptLines.AsNoTracking()
                where l.ReceiptId == receiptId
                join c in db.Charges on l.ChargeId equals c.ChargeId into charge
                from c in charge.DefaultIfEmpty()
                orderby l.LineNo
                select new ReceiptLineResponse(l.LineNo, l.ChargeCode, l.Description, l.ContainerNo, l.MovementCode,
                    l.Quantity, l.UnitRate, l.Amount, l.TaxRate, l.TaxAmount,
                    c == null ? null : c.BillingUnitCode, c == null ? null : c.ServiceFrom, c == null ? null : c.ServiceTo))
            .ToListAsync(ct);
        var payments = await db.ReceiptPayments.AsNoTracking().Where(p => p.ReceiptId == receiptId).ToListAsync(ct);
        var coupons = await db.Charges.AsNoTracking()
            .Where(c => c.ReceiptId == receiptId && c.CouponRef != null)
            .Select(c => new { c.CouponRef, c.ContainerNo, c.MovementCode })
            .Distinct().ToListAsync(ct);

        var branch = await calendar.BranchAsync(receipt.BranchId, ct);
        var seller = await master.InvoicingCompanyAsync(receipt.BranchId, ct);

        // A void and its replacement name each other (cashier.receipt.replaces_receipt_id).
        var replaces = receipt.ReplacesReceiptId is { } replacedId
            ? await db.Receipts.AsNoTracking().Where(r => r.ReceiptId == replacedId).Select(r => r.ReceiptNo).SingleOrDefaultAsync(ct)
            : null;
        var replacedBy = await db.Receipts.AsNoTracking().Where(r => r.ReplacesReceiptId == receiptId).Select(r => r.ReceiptNo).SingleOrDefaultAsync(ct);
        // A split gate receipt and its parts name each other (split_from_receipt_id).
        var splitFrom = receipt.SplitFromReceiptId is { } fromId
            ? await db.Receipts.AsNoTracking().Where(r => r.ReceiptId == fromId).Select(r => r.ReceiptNo).SingleOrDefaultAsync(ct)
            : null;
        var splitInto = await db.Receipts.AsNoTracking().Where(r => r.SplitFromReceiptId == receiptId)
            .OrderBy(r => r.ReceiptNo).Select(r => r.ReceiptNo).ToListAsync(ct);

        return new ReceiptResponse(receipt.ReceiptId, receipt.ReceiptNo,
            branch is null ? receipt.ReceiptAt : branch.Local(receipt.ReceiptAt),
            receipt.Status, receipt.OrderNo ?? "",
            receipt.PayerName, receipt.PayerTaxId, receipt.SubtotalAmount, receipt.TaxAmount, receipt.TotalAmount,
            payments.Sum(p => p.ChangeAmount ?? 0), receipt.CurrencyCode,
            lines,
            payments.Select(p => new ReceiptPaymentResponse(p.Channel, p.Amount, p.TenderedAmount, p.ChangeAmount, p.ReferenceNo, p.BankName)).ToList(),
            issued ?? coupons.GroupBy(c => c.CouponRef!).Select(g => g.First())
                .OrderBy(c => c.CouponRef)
                .Select(c => new CouponResponse(c.CouponRef!, c.ContainerNo, c.MovementCode ?? "", null)).ToList(),
            receipt.BranchId, branch?.BranchCode, receipt.PayerBranchNo, receipt.PayerAddress,
            receipt.ShiftId, receipt.CashierUserId,
            seller is null ? null : new SellerResponse(seller.CompanyCode, seller.LegalNameEn, seller.LegalNameLocal,
                seller.TaxId, seller.TaxBranchNo, seller.IsHeadOffice, seller.Address, seller.Phone, seller.Email),
            receipt.VoidedAt is { } voided && branch is not null ? branch.Local(voided) : receipt.VoidedAt, receipt.VoidReason,
            replaces, replacedBy,
            receipt.WithholdingTaxRate, receipt.WithholdingTaxAmount, receipt.TotalAmount - receipt.WithholdingTaxAmount,
            receipt.PayerPartyCode, receipt.Remarks, receipt.IssuedFrom, splitFrom, splitInto.Count > 0 ? splitInto : null);
    }

    public async Task<Rendered?> RenderAsync(Guid receiptId, CancellationToken ct)
    {
        var r = await ReadAsync(receiptId, ct);
        return r is null ? null : new Rendered($"{r.ReceiptNo.Replace('/', '-')}.pdf", Render(r, await PrintFactsAsync(r, master, ct)));
    }

    /// <summary>What the printed bill shows beyond the receipt itself, read from MDM at print time: the payer's phone and the
    /// charge names Vector printed (its Master.ChargeCode description — MDM's local description, else the English one).</summary>
    internal static async Task<ReceiptPrintFacts> PrintFactsAsync(ReceiptResponse r, IMasterDataReferences master, CancellationToken ct)
    {
        var phone = r.PayerPartyCode is { } payer ? (await master.PartiesAsync([payer], ct)).GetValueOrDefault(payer)?.Phone : null;
        var codes = r.Lines.Select(l => l.ChargeCode).Distinct().ToList();
        IReadOnlyDictionary<string, string> names = codes.Count == 0 ? new Dictionary<string, string>() : (await master.ChargeVariantsAsync(codes, ct))
            .GroupBy(v => v.ChargeCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DescriptionLocal ?? g.First().DescriptionEn, StringComparer.OrdinalIgnoreCase);
        return new ReceiptPrintFacts(phone, null, names);
    }

    // ── the KPS full tax invoice (TMS.Operation.Gate.FullTaxInvoice(KPS).rdl, Report.usp_Operation_FullTaxInvoiceReceiptKPS) ──

    /// <summary>The proc pads the lines to 19 so the bill is always the same height.</summary>
    private const int BilledRows = 19;

    private static string Plain(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// The A4 bill as Vector printed it: same blocks at the same places, the same Thai labels, the lines grouped by
    /// charge code ("description (containers)", quantity, unit price, amount) padded to 19 rows, then the totals block
    /// with the amount in words and the receipt / authorised-signatory lines. Differences, each deliberate:
    ///   – the seller block is MDM's invoicing company for the branch, not KORAKIT's names hard-coded in the proc;
    ///   – the buyer's tax-id line also carries the buyer's head office / branch (Revenue Code s.86/4), which the RDL lacked;
    ///   – a charge whose lines were priced at different rates prints one row per rate, so every row's amount is right;
    ///   – a VOIDED receipt still prints, with the VOID mark.
    /// </summary>
    internal static byte[] Render(ReceiptResponse r, ReceiptPrintFacts? facts = null)
    {
        GeckoPdf.EnsureInitialised();
        var voided = r.Status == "VOIDED";
        var s = r.Seller;
        var sellerName = s is null ? null : s.NameLocal ?? s.NameEn;
        var date = r.ReceiptAt.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

        // The proc's rows: lines with a rate (InDt.SellRate > 0), RowID their order; the RDL groups them by charge code.
        var billed = r.Lines.Where(l => l.UnitRate > 0).OrderBy(l => l.LineNo).ToList();
        var rows = billed.Select((line, i) => (line, rowId: i + 1))
            .GroupBy(x => (x.line.ChargeCode, x.line.UnitRate))
            .Select(g => new
            {
                g.First().rowId,
                Description = (facts?.ChargeNames.GetValueOrDefault(g.Key.ChargeCode) ?? g.First().line.Description)
                    + " (" + string.Join(",", billed.Where(l => l.ChargeCode == g.Key.ChargeCode).Select(l => l.ContainerNo).OfType<string>()
                        .Distinct().Order(StringComparer.Ordinal)) + ")",
                Quantity = g.Sum(x => x.line.Quantity),
                Rate = g.Key.UnitRate!.Value,
                Amount = g.Sum(x => x.line.Amount),
            })
            .OrderBy(x => x.rowId).ToList();
        var padding = Math.Max(0, BilledRows - billed.Count);
        var rates = r.Lines.Where(l => l.TaxAmount != 0).Select(l => l.TaxRate).Distinct().ToList();
        var vatRate = rates.Count == 1 ? rates[0] : 7m;

        return Document.Create(document => document.Page(page =>
        {
            GeckoPdf.Page(page);
            page.Size(PageSizes.A4);
            page.Margin(In(0.1));
            page.DefaultTextStyle(t => t.FontSize(10).FontFamily(GeckoPdf.LatinFont, GeckoPdf.ThaiFont));

            // Page header (1.4in, every page): the seller, and the title.
            page.Header().Height(In(1.4)).Layers(layers =>
            {
                layers.PrimaryLayer();
                layers.At(0.125, 0.24016, 7.50205, 0.30904, c => c.AlignBottom().Text(sellerName ?? "(not set)").FontSize(12));
                layers.At(0.47778, 3.66235, 2.08539, 0.23958, c => c.AlignCenter().Text("บิลเงินสด/ใบกำกับภาษี ").FontSize(12));
                layers.At(0.44098, 0.24016, 3.35274, 0.81805, c => c.Text(
                    (s?.Address ?? "") + "\n" + "เลขประจำตัวผู้เสียภาษี " + (s?.TaxId ?? "") + "\n"
                    + (s is null ? "" : s.IsHeadOffice == true ? "(สำนักงานใหญ่)" : s.TaxBranchNo is { } b ? $"(สาขาที่ {b})" : "")));
            });

            page.Content().Column(body =>
            {
                // The customer block and the bill's number / date, at the RDL's places above the lines (Tablix4 at 2.18834in).
                body.Item().Height(In(2.18834)).Layers(layers =>
                {
                    layers.PrimaryLayer();
                    layers.At(0.04042, 0.25182, 4.03125, 0.25, c => c.Text("ลูกค้า " + (r.PayerPartyCode ?? "")).FontSize(9));
                    layers.At(0.3125, 0.25182, 4.03125, 0.25, c => c.Text("บริษัท " + r.PayerName).FontSize(9));
                    layers.At(0.60986, 0.25182, 4.70709, 0.54098, c => c.Text("ที่อยู่ " + (r.PayerAddress ?? "")).FontSize(9));
                    layers.At(1.22028, 0.24016, 4.52083, 0.2, c => c.Text("เลขประจำตัวผู้เสียภาษี " + (r.PayerTaxId ?? "") + BuyerBranch(r.PayerBranchNo)).FontSize(9));
                    layers.At(1.44806, 0.24016, 4.52083, 0.2, c => c.Text("โทร. " + (facts?.PayerPhone ?? "")).FontSize(9));
                    layers.At(1.67584, 0.24016, 4.52083, 0.2, c => c.Text("อ้างอิง").FontSize(9));
                    layers.At(1.91889, 0.24016, 4.52083, 0.2, c => c.Text("ขนส่งโดย " + (facts?.HaulierName ?? "")).FontSize(9));
                    layers.At(1.46334, 5.27446, 1.36663, 0.2, c => c.Text("เลขที่ใบสั่งขาย ").FontSize(9));
                    layers.At(1.67723, 5.27446, 1.36663, 0.2, c => c.Text("พนักงานขาย").FontSize(9));
                    layers.At(1.91889, 5.27446, 1.36663, 0.2, c => c.Text("เขตการขาย ").FontSize(9));
                    layers.At(0.04042, 5.13903, 2.50206, 0.26042, c => c.AlignRight().Text("เลขที่บิลเงินสด " + r.ReceiptNo));
                    layers.At(0.3125, 5.5557, 2.08539, 0.26042, c => c.AlignRight().Text("วันที่ " + r.ReceiptAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)));
                });

                body.Item().PaddingLeft(In(0.24016)).Width(In(7.50205)).DefaultTextStyle(t => t.FontSize(9)).Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        foreach (var width in new[] { 0.66951, 4.35675, 0.73157, 0.84388, 0.90034 }) cols.ConstantColumn(In(width));
                    });

                    var head = In(0.23958);
                    table.Cell().Box(true, true, true, true, head).AlignCenter().Text("No.");
                    table.Cell().Box(true, true, true, true, head).AlignCenter().Text("รหัสสินค้า/รายละเอียด");
                    table.Cell().Box(true, true, true, true, head).AlignCenter().Text("จำนวน");
                    table.Cell().Box(true, true, true, true, head).AlignCenter().Text("หน่วยละ");
                    table.Cell().Box(true, true, true, true, head).AlignCenter().Text("จำนวนเงิน");

                    var line = In(0.19271);
                    foreach (var row in rows)
                    {
                        table.Cell().Box(left: true, right: true, minHeight: line).AlignCenter().Text(row.rowId.ToString(CultureInfo.InvariantCulture));
                        table.Cell().Box(left: true, right: true, minHeight: line).Text(row.Description);
                        table.Cell().Box(left: true, right: true, minHeight: line).AlignCenter().Text(row.Quantity.ToString("0;(0)", CultureInfo.InvariantCulture));
                        table.Cell().Box(left: true, right: true, minHeight: line).AlignCenter().Text(row.Rate.ToString("0.00;(0.00)", CultureInfo.InvariantCulture));
                        table.Cell().Box(left: true, right: true, minHeight: line).AlignRight().Text(row.Amount.ToString("0.00;(0.00)", CultureInfo.InvariantCulture));
                    }
                    for (var i = 0; i < padding; i++)
                        for (var c = 0; c < 5; c++)
                            table.Cell().Box(left: true, right: true, minHeight: line);

                    var foot = In(0.25);
                    table.Cell().ColumnSpan(2).Box(top: true, left: true, minHeight: foot).Text("หมายเหตุ " + (r.Remarks ?? ""));
                    table.Cell().ColumnSpan(2).Box(top: true, right: true, minHeight: foot).AlignRight().Text("รวมเป็นเงิน");
                    table.Cell().Box(true, true, true, minHeight: foot).AlignRight().Text(Plain(r.Subtotal));

                    // Gecko prices net of any discount, so the discount the bill deducts is always 0.00; the deposit rows were always blank.
                    void Total(string label, string amount)
                    {
                        table.Cell().Box(left: true, minHeight: foot);
                        table.Cell().Box(minHeight: foot);
                        table.Cell().ColumnSpan(2).Box(right: true, minHeight: foot).AlignRight().Text(label);
                        table.Cell().Box(left: true, right: true, minHeight: foot).AlignRight().Text(amount);
                    }
                    Total("หักส่วนลด", Plain(0m));
                    Total("ยอดหลังหักส่วนลด", Plain(r.Subtotal - 0m));
                    Total("หักเงินมัดจำ", "");
                    Total("จำนวนเงินหลังหักมัดจำ", "");
                    Total($"จำนวนภาษีมูลค่าเพิ่ม {vatRate.ToString("0.00", CultureInfo.InvariantCulture)}%", Plain(r.Tax));

                    table.Cell().ColumnSpan(2).Box(left: true, bottom: true, minHeight: foot)
                        .Text(" " + VectorAmountInWords.ExpandPrice(r.Total).ToUpperInvariant());
                    table.Cell().ColumnSpan(2).Box(right: true, bottom: true, minHeight: foot).AlignRight().Text("จำนวนเงินรวมทั้งสิ้น");
                    table.Cell().Box(left: true, right: true, bottom: true, minHeight: foot).AlignRight().Text(Plain(r.Total));

                    table.Cell().ColumnSpan(5).Box(left: true, right: true, minHeight: In(0.15625));
                    table.Cell().ColumnSpan(5).Box(left: true, right: true, minHeight: foot).Text("ได้รับสินค้าตามรายการข้างบนนี้ไว้ถูกต้อง");
                    table.Cell().ColumnSpan(2).Box(left: true, minHeight: foot).Text("และอยู่ในสภาพเรียบร้อยทุกประการ");
                    table.Cell().ColumnSpan(3).Box(right: true, minHeight: foot).Text("ในนาม " + (sellerName ?? ""));
                    table.Cell().ColumnSpan(2).Box(left: true, minHeight: foot).Text("ผู้รับสินค้า __________________ วันที่ " + date);
                    table.Cell().ColumnSpan(3).Box(right: true, minHeight: foot).Text("ผู้รับมอบอำนาจ _________________");
                    table.Cell().ColumnSpan(5).Box(left: true, right: true, bottom: true, minHeight: foot);
                });
            });

            if (voided) page.Foreground().VoidWatermark();
        })).GeneratePdf();
    }

    private static float In(double inches) => RdlLayout.In(inches);

    /// <summary>The buyer's head office / branch (s.86/4) after its tax id; nothing when the payer has no branch on record.</summary>
    private static string BuyerBranch(string? branchNo) =>
        branchNo is null ? ""
        : branchNo.Trim('0').Length == 0 ? " (สำนักงานใหญ่)"
        : $" (สาขาที่ {branchNo})";
}

/// <param name="PayerPhone">MDM party.primary_phone of the payer — the bill's "โทร." line.</param>
/// <param name="HaulierName">The truck's haulier — the bill's "ขนส่งโดย" line. TOS holds it and no contract carries it to Revenue yet: blank.</param>
/// <param name="ChargeNames">By charge code, the name Vector printed for it.</param>
internal sealed record ReceiptPrintFacts(string? PayerPhone, string? HaulierName, IReadOnlyDictionary<string, string> ChargeNames);
