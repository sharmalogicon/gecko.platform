using System.Globalization;
using Gecko.Data.Documents;
using Gecko.Revenue.Application;
using Gecko.Tos.Contracts;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Gecko.Revenue.Endpoints.Window;

/// <summary>
/// The coupon slips of a receipt (owner D2, GATE_IN_COMPLETION_PLAN A10; Vector CouponInvoice): one small page per
/// box the receipt paid for, which the driver shows at the barrier — coupon ref, container, movement, what was paid
/// for it and until when it is good. The money is the receipt's (gecko_revenue); how long the coupon is good for and
/// whether it was used or withdrawn is the barrier's (TOS, <see cref="ITosGateCoupons"/>). A voided receipt's
/// slips still print, marked VOID.
/// </summary>
internal sealed class CouponSlipDocument(ReceiptDocument receipts, ITosGateCoupons tos, BranchCalendar calendar)
{
    public sealed record Rendered(string FileName, byte[] Pdf);

    public async Task<Rendered?> RenderAsync(Guid receiptId, CancellationToken ct)
    {
        var r = await receipts.ReadAsync(receiptId, ct);
        if (r is null) return null;
        var held = await tos.CouponsAsync(r.Coupons.Select(c => c.CouponRef).ToList(), ct);
        var branch = await calendar.BranchAsync(r.BranchId, ct);
        string Local(DateTimeOffset at) => (branch is null ? at : branch.Local(at)).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);
        static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);
        var voided = r.Status == "VOIDED";

        GeckoPdf.EnsureInitialised();
        var pdf = Document.Create(document =>
        {
            foreach (var coupon in r.Coupons)
            {
                var paid = r.Lines.Where(l => l.ContainerNo == coupon.ContainerNo && l.MovementCode == coupon.MovementCode).ToList();
                var gate = held.GetValueOrDefault(coupon.CouponRef);
                document.Page(page =>
                {
                    GeckoPdf.Page(page);   // the Latin + Thai fonts; then a slip-sized page
                    page.Size(PageSizes.A6);
                    page.Margin(16);

                    page.Content().Column(c =>
                    {
                        c.Spacing(4);
                        c.Item().Text(r.Seller?.NameLocal ?? r.Seller?.NameEn ?? r.BranchCode ?? "").SemiBold();
                        c.Item().Text("GATE COUPON · คูปองผ่านประตู").FontSize(11).Bold();
                        c.Item().PaddingVertical(4).Border(1).BorderColor(Colors.Grey.Darken1).Padding(6).Column(box =>
                        {
                            box.Item().AlignCenter().Text(coupon.CouponRef).FontSize(16).Bold();
                            box.Item().AlignCenter().Text(coupon.ContainerNo ?? "—").FontSize(12).SemiBold();
                            box.Item().AlignCenter().Text(coupon.MovementCode);
                        });
                        c.Field("Receipt", r.ReceiptNo);
                        c.Field("Order", gate?.OrderNo ?? (string.IsNullOrEmpty(r.OrderNo) ? null : r.OrderNo));
                        c.Field("Paid by", r.PayerName);
                        c.Field("Amount", $"{Money(paid.Sum(l => l.Amount + l.TaxAmount))} {r.CurrencyCode} (VAT incl.)");
                        foreach (var line in paid)
                            c.Item().PaddingLeft(8).Text($"{line.ChargeCode}  {Money(line.Amount + line.TaxAmount)}").FontColor(Colors.Grey.Darken2);
                        c.Field("Issued", Local(r.ReceiptAt));
                        c.Field("Valid until", gate is null ? "(the barrier has not received it yet)" : Local(gate.ValidUntil));
                        if (gate?.UsedAt is { } used)
                            c.Item().Text($"USED {Local(used)} — EIR {gate.UsedByEirNo}").Bold().FontColor(Colors.Blue.Darken2);
                        if (gate?.RevokedAt is { } revoked)
                            c.Item().Text($"WITHDRAWN {Local(revoked)}").Bold().FontColor(Colors.Red.Darken2);
                    });

                    if (voided) page.Foreground().VoidWatermark();
                });
            }
            if (r.Coupons.Count == 0)
                document.Page(page =>
                {
                    GeckoPdf.Page(page);
                    page.Size(PageSizes.A6);
                    page.Margin(16);
                    page.Content().Text($"Receipt {r.ReceiptNo} issued no gate coupon.").FontSize(9);
                });
        }).GeneratePdf();

        return new Rendered($"{r.ReceiptNo.Replace('/', '-')}-coupons.pdf", pdf);
    }
}
