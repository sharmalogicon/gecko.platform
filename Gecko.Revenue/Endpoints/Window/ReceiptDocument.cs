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
            receipt.VoidedAt is { } voided && branch is not null ? branch.Local(voided) : receipt.VoidedAt, receipt.VoidReason);
    }

    public async Task<Rendered?> RenderAsync(Guid receiptId, CancellationToken ct)
    {
        var r = await ReadAsync(receiptId, ct);
        return r is null ? null : new Rendered($"{r.ReceiptNo.Replace('/', '-')}.pdf", Render(r));
    }

    private const string NotSet = "(not set)";

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    private static string TaxBranch(string? branchNo, bool? headOffice) =>
        branchNo is null ? NotSet
        : headOffice == true ? $"สำนักงานใหญ่ / Head office ({branchNo})"
        : $"สาขาที่ {branchNo} / Branch {branchNo}";

    private static string Unit(string? code) => code switch
    {
        null => "",
        "PER_CONTAINER" => "box",
        "PER_DAY" => "day",
        "PER_HOUR" => "hour",
        "PER_TEU" => "TEU",
        _ => code.StartsWith("PER_", StringComparison.Ordinal) ? code[4..].ToLowerInvariant() : code.ToLowerInvariant(),
    };

    internal static byte[] Render(ReceiptResponse r)
    {
        GeckoPdf.EnsureInitialised();
        var voided = r.Status == "VOIDED";
        var s = r.Seller;
        var grey = Colors.Grey.Darken2;

        return Document.Create(document => document.Page(page =>
        {
            GeckoPdf.Page(page);

            page.Header().Column(head =>
            {
                head.Item().Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        if (s is null)
                        {
                            c.Item().Text($"Seller {NotSet} — branch {r.BranchCode ?? r.BranchId.ToString()} has no invoicing company in master data")
                                .FontColor(Colors.Red.Darken2);
                        }
                        else
                        {
                            if (s.NameLocal is not null) c.Item().Text(s.NameLocal).FontSize(12).Bold();
                            c.Item().Text(s.NameEn).FontSize(s.NameLocal is null ? 12 : 10).SemiBold();
                            c.Item().Text(s.Address ?? $"Address {NotSet}");
                            c.Item().Text(t =>
                            {
                                t.Span("เลขประจำตัวผู้เสียภาษี / Tax ID  ").FontSize(7).FontColor(Colors.Grey.Darken1);
                                t.Span(s.TaxId ?? NotSet).SemiBold();
                                t.Span("    ").FontSize(7);
                                t.Span(TaxBranch(s.TaxBranchNo, s.IsHeadOffice));
                            });
                            if (s.Phone is not null || s.Email is not null)
                                c.Item().Text(string.Join("   ", new[] { s.Phone is null ? null : $"Tel {s.Phone}", s.Email }.OfType<string>()))
                                    .FontColor(grey);
                        }
                    });
                    row.ConstantItem(210).AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text("ใบเสร็จรับเงิน/ใบกำกับภาษี").FontSize(13).Bold();
                        c.Item().AlignRight().Text("RECEIPT / TAX INVOICE").FontSize(10).SemiBold();
                        c.Item().AlignRight().PaddingTop(4).Text("ต้นฉบับ / Original").FontSize(8).FontColor(grey);
                    });
                });
                head.Item().PaddingTop(8).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
            });

            page.Content().PaddingTop(10).Column(body =>
            {
                body.Spacing(10);

                body.Item().Row(parts =>
                {
                    parts.RelativeItem(3).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(6).Column(c =>
                    {
                        c.Spacing(2);
                        c.Item().Text("ผู้ซื้อ / BUYER").FontSize(8).Bold().FontColor(grey);
                        c.Item().Text(r.PayerName).FontSize(11).SemiBold();
                        c.Field("Tax ID", r.PayerTaxId);
                        c.Field("Branch", r.PayerBranchNo is null ? null
                            : r.PayerBranchNo.Trim('0').Length == 0 ? $"สำนักงานใหญ่ / Head office ({r.PayerBranchNo})" : r.PayerBranchNo);
                        c.Field("Address", r.PayerAddress);
                    });
                    parts.ConstantItem(12);
                    parts.RelativeItem(2).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(6).Column(c =>
                    {
                        c.Spacing(2);
                        c.Item().Text(r.ReceiptNo).FontSize(13).Bold();
                        c.Field("Date / วันที่", r.ReceiptAt.ToString("dd MMM yyyy  HH:mm", CultureInfo.InvariantCulture));
                        c.Field("Order", r.OrderNo);
                        c.Field("Depot", r.BranchCode);
                        if (voided) c.Item().Text("VOIDED").Bold().FontColor(Colors.Red.Darken2);
                    });
                });

                body.Item().Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        cols.ConstantColumn(22);
                        cols.RelativeColumn(5);
                        cols.RelativeColumn(1.4f);
                        cols.ConstantColumn(62);
                        cols.ConstantColumn(62);
                        cols.ConstantColumn(72);
                    });
                    table.Header(h =>
                    {
                        void Head(string text, bool right = false)
                        {
                            var cell = h.Cell().Background(Colors.Grey.Lighten3).Padding(3);
                            (right ? cell.AlignRight() : cell).Text(text).FontSize(7).Bold();
                        }
                        Head("#"); Head("Description / รายการ"); Head("Container"); Head("Qty", true); Head("Unit price", true); Head("Amount", true);
                    });
                    foreach (var l in r.Lines)
                    {
                        IContainer Cell() => table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3).PaddingHorizontal(3);
                        Cell().Text(l.LineNo.ToString(CultureInfo.InvariantCulture));
                        Cell().Column(c =>
                        {
                            c.Item().Text(l.Description);
                            var extra = new List<string> { l.ChargeCode };
                            if (l.MovementCode is not null) extra.Add(l.MovementCode);
                            if (l.ServiceFrom is { } from && l.ServiceTo is { } to)
                                extra.Add($"{from.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} – {to.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}");
                            c.Item().Text(string.Join(" · ", extra)).FontSize(7).FontColor(Colors.Grey.Darken1);
                        });
                        Cell().Text(l.ContainerNo ?? "—");
                        Cell().AlignRight().Text($"{l.Quantity.ToString("0.##", CultureInfo.InvariantCulture)} {Unit(l.BillingUnitCode)}".TrimEnd());
                        Cell().AlignRight().Text(l.UnitRate is { } rate ? Money(rate) : "—");
                        Cell().AlignRight().Text(Money(l.Amount));
                    }
                });

                body.Item().Row(totals =>
                {
                    totals.RelativeItem().Column(c =>
                    {
                        c.Spacing(2);
                        c.Item().Text("การชำระเงิน / PAYMENT").FontSize(8).Bold().FontColor(grey);
                        foreach (var p in r.Payments)
                        {
                            var detail = p.Channel == "CASH"
                                ? p.Tendered is { } t ? $"received {Money(t)}, change {Money(p.Change ?? 0)}" : null
                                : string.Join(" ", new[] { p.BankName, p.ReferenceNo is null ? null : $"ref {p.ReferenceNo}" }.OfType<string>());
                            c.Item().Text(t =>
                            {
                                t.Span($"{p.Channel}  {Money(p.Amount)}").SemiBold();
                                if (!string.IsNullOrWhiteSpace(detail)) t.Span($"   {detail}").FontColor(Colors.Grey.Darken1);
                            });
                        }
                        if (r.Change > 0) c.Field("Change / เงินทอน", Money(r.Change));
                        if (r.Coupons.Count > 0)
                        {
                            c.Item().PaddingTop(4).Text("GATE COUPONS").FontSize(8).Bold().FontColor(grey);
                            foreach (var coupon in r.Coupons)
                                c.Item().Text($"{coupon.CouponRef}  {coupon.ContainerNo ?? ""} {coupon.MovementCode}".TrimEnd());
                        }
                    });
                    totals.ConstantItem(16);
                    totals.ConstantItem(220).Column(c =>
                    {
                        void Line(string label, decimal amount, bool strong = false) => c.Item().Row(x =>
                        {
                            var left = x.RelativeItem().Text(label);
                            var right = x.ConstantItem(90).AlignRight().Text($"{Money(amount)} {r.CurrencyCode}");
                            if (strong) { left.Bold(); right.Bold().FontSize(11); }
                        });
                        Line("มูลค่าสินค้า/บริการ / Subtotal", r.Subtotal);
                        var rates = r.Lines.Where(l => l.TaxAmount != 0).Select(l => l.TaxRate).Distinct().ToList();
                        var vatLabel = rates.Count == 1 ? $"VAT {rates[0].ToString("0.##", CultureInfo.InvariantCulture)}%" : "VAT";
                        Line($"ภาษีมูลค่าเพิ่ม / {vatLabel}", r.Tax);
                        c.Item().PaddingVertical(2).LineHorizontal(1);
                        Line("รวมทั้งสิ้น / Total", r.Total, strong: true);
                    });
                });

                if (voided)
                    body.Item().Text($"VOIDED {r.VoidedAt?.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture)} — {r.VoidReason}").Italic()
                        .FontColor(Colors.Red.Darken2);

                body.Item().PaddingTop(30).Row(sign =>
                {
                    foreach (var who in new[] { "ผู้รับเงิน / Collected by", "ผู้จ่ายเงิน / Paid by" })
                    {
                        sign.RelativeItem().Column(c =>
                        {
                            c.Item().PaddingTop(24).BorderTop(1).BorderColor(Colors.Grey.Darken1);
                            c.Item().AlignCenter().Text(who).FontSize(8);
                        });
                        sign.ConstantItem(40);
                    }
                });
            });

            if (voided) page.Foreground().VoidWatermark();

            page.Footer().Row(f =>
            {
                f.RelativeItem().Text($"Cashier {r.CashierUserId.ToString("N")[..8]} · shift {r.ShiftId.ToString("N")[..8]} · GECKO")
                    .FontSize(7).FontColor(Colors.Grey.Darken1);
                f.RelativeItem().AlignRight().Text(t => { t.Span("Page ").FontSize(7); t.CurrentPageNumber().FontSize(7); });
            });
        })).GeneratePdf();
    }
}
