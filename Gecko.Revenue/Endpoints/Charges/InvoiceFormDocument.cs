using System.Globalization;
using Gecko.Data.Documents;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Application;
using Gecko.Revenue.Endpoints.Window;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Tos.Contracts;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Gecko.Revenue.Endpoints.Charges;

/// <summary>
/// Vector's two billing forms (Report.usp_Billing_CreditInvoiceReceipt), printed from Gecko's own invoices and receipts:
///   – TMS.Billing.CreditInvoiceReceipt: the credit invoice (CustomerInvoice.cs "Print");
///   – TMS.Billing.CashInvoiceReceipt: the cash format — for a credit invoice (SourceForm CREDIT, no payment lines) and for a
///     customer cash bill's receipt (SourceForm CASH, with how it was paid).
/// Both are overlays for pre-printed stationery: only what the RDL prints is printed, at the RDL's places (Letter, 0.1in margins).
/// The lines are the proc's — those with a rate — grouped as the RDL grouped them (rate, charge, size), numbered in order. The
/// amount a group prints is its lines' own amounts (the RDL took rate × quantity, and a fudge for SC008-CA). The customer's
/// address is MDM's (party.primary_address*, Vector's Master.Contact); the vessel, voyage, ETA and sub-B/L are TOS's, of the
/// first line's booking. Hidden RDL items (the credit form's withholding-tax, liability and credit-term lines) stay unprinted.
/// </summary>
internal sealed class InvoiceFormDocument(
    RevenueDbContext db, IMasterDataReferences master, BranchCalendar calendar, ITosBookingHeaders tos, ITosBookedBoxes booked)
{
    public sealed record Rendered(string FileName, byte[] Pdf);

    public async Task<Rendered?> InvoiceAsync(Guid invoiceId, bool cashFormat, CancellationToken ct)
    {
        var i = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.InvoiceId == invoiceId, ct);
        if (i is null) return null;
        var lines = await (
                from l in db.InvoiceLines.AsNoTracking()
                where l.InvoiceId == invoiceId
                join c in db.Charges on l.ChargeId equals c.ChargeId into charge
                from c in charge.DefaultIfEmpty()
                orderby l.LineNo
                select new SourceLine(l.ChargeCode, l.ChargeName, l.UnitRate, l.Amount, l.TaxAmount,
                    l.BookingId ?? (c == null ? null : c.BookingId), c == null ? null : c.BookingContainerId, l.OrderNo))
            .ToListAsync(ct);
        var form = await FormAsync(i.BranchId, i.InvoiceNo, i.IssuedAt, i.PayerName ?? i.PayerPartyCode ?? "", i.PayerPartyCode, null,
            i.SubtotalAmount, i.TaxAmount, i.TotalAmount, null, i.Status == "VOIDED", lines, ct);
        return new Rendered($"{i.InvoiceNo.Replace('/', '-')}.pdf", cashFormat ? RenderCash(form) : RenderCredit(form));
    }

    public async Task<Rendered?> ReceiptAsync(Guid receiptId, CancellationToken ct)
    {
        var r = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(x => x.ReceiptId == receiptId, ct);
        if (r is null) return null;
        var lines = await (
                from l in db.ReceiptLines.AsNoTracking()
                where l.ReceiptId == receiptId
                join c in db.Charges on l.ChargeId equals c.ChargeId into charge
                from c in charge.DefaultIfEmpty()
                orderby l.LineNo
                select new SourceLine(l.ChargeCode, l.Description, l.UnitRate, l.Amount, l.TaxAmount,
                    c == null ? null : c.BookingId, c == null ? null : c.BookingContainerId, c == null ? null : c.OrderNo))
            .ToListAsync(ct);
        var payments = await db.ReceiptPayments.AsNoTracking().Where(p => p.ReceiptId == receiptId)
            .OrderBy(p => p.CreatedAt).Select(p => new { p.Channel, p.ReferenceNo }).ToListAsync(ct);
        var payment = new InvoiceFormPayment(
            string.Join(" + ", payments.Select(p => p.Channel).Distinct()),
            payments.FirstOrDefault(p => p.Channel == "CHEQUE")?.ReferenceNo,
            r.TotalAmount - r.WithholdingTaxAmount);
        var form = await FormAsync(r.BranchId, r.ReceiptNo, r.ReceiptAt, r.PayerName, r.PayerPartyCode, r.PayerAddress,
            r.SubtotalAmount, r.TaxAmount, r.TotalAmount, payment, r.Status == "VOIDED", lines, ct, r.OrderNo);
        return new Rendered($"{r.ReceiptNo.Replace('/', '-')}-cash-invoice.pdf", RenderCash(form));
    }

    private sealed record SourceLine(string ChargeCode, string? Name, decimal? UnitRate, decimal Amount, decimal Tax,
        Guid? BookingId, Guid? BookingContainerId, string? OrderNo);

    private async Task<InvoiceForm> FormAsync(Guid branchId, string number, DateTimeOffset at, string customerName, string? payerCode,
        string? keyedAddress, decimal amount, decimal tax, decimal total, InvoiceFormPayment? payment, bool voided,
        IReadOnlyList<SourceLine> all, CancellationToken ct, string? orderNo = null)
    {
        // The proc's rows: InvoiceDetail with SellRate > 0.
        var lines = all.Where(l => l.UnitRate > 0).ToList();
        var boxTypes = await tos.ContainerTypesAsync(lines.Select(l => l.BookingContainerId).OfType<Guid>().Distinct().ToList(), ct);
        var typeCodes = boxTypes.Values.Distinct().ToList();
        var types = typeCodes.Count == 0 ? new Dictionary<string, EquipmentTypeRef>() : await master.EquipmentTypesAsync(typeCodes, ct);
        string? Size(Guid? box) => box is { } id && boxTypes.TryGetValue(id, out var type) ? types.GetValueOrDefault(type)?.SizeCode : null;

        // First(...) of the dataset: the first line's booking.
        var first = lines.FirstOrDefault(l => l.BookingId is not null) ?? all.FirstOrDefault(l => l.BookingId is not null);
        TosBookingHeader? header = null;
        DateTimeOffset? eta = null;
        string? vesselName = null;
        if (first?.BookingId is { } bookingId)
        {
            header = (await tos.HeadersAsync([bookingId], ct)).GetValueOrDefault(bookingId);
            if (first.BookingContainerId is { } box) eta = (await booked.BoxesByIdAsync([box], ct)).GetValueOrDefault(box)?.Eta;
            if (header?.VesselCode is { } vessel) vesselName = (await master.VesselsAsync([vessel], ct)).GetValueOrDefault(vessel)?.VesselName ?? vessel;
        }

        var party = payerCode is null ? null : (await master.PartiesAsync([payerCode], ct)).GetValueOrDefault(payerCode);
        var branch = await calendar.BranchAsync(branchId, ct);
        DateTimeOffset Local(DateTimeOffset instant) => branch is null ? instant : branch.Local(instant);
        // A receipt prints the address the cashier keyed for it; otherwise the payer's own (Address2 + State + PostCode as the RDL joined them).
        var (address1, address2) = !string.IsNullOrWhiteSpace(keyedAddress)
            ? (keyedAddress, "")
            : (party?.Address1 ?? "", string.Join(" ", party?.Address2 ?? "", party?.State ?? "", party?.Postcode ?? ""));

        return new InvoiceForm(number, Local(at), customerName, address1, address2, party?.Phone ?? "",
            header?.BookingTypeCode ?? "", vesselName ?? "", header?.Voyage ?? "", eta is { } e ? Local(e) : null,
            header?.SubBlNo ?? "", orderNo ?? first?.OrderNo ?? header?.OrderNo ?? "",
            amount, tax, total, payment, voided,
            Rows(lines.Select(l => new InvoiceFormLine(l.ChargeCode, l.Name ?? l.ChargeCode, l.UnitRate!.Value, Size(l.BookingContainerId), l.Amount, l.Tax)).ToList()),
            await master.InvoicingCompanyAsync(branchId, ct));
    }

    /// <summary>The RDL's SellRate group (rate, charge, size), in first-appearance order, numbered 1… (RunningValue CountDistinct):
    /// "description rate(count X size')", its lines' amounts and tax.</summary>
    internal static IReadOnlyList<InvoiceFormRow> Rows(IReadOnlyList<InvoiceFormLine> lines) =>
        lines.GroupBy(l => (l.UnitRate, l.ChargeCode, l.Size))
            .Select((g, i) => new InvoiceFormRow(i + 1,
                $"{g.First().Description} {Plain(g.Key.UnitRate)}({g.Count().ToString(CultureInfo.InvariantCulture)} X {g.Key.Size}')",
                g.Sum(l => l.Amount), g.Sum(l => l.Tax)))
            .ToList();

    private static string Plain(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Money(decimal value) => value.ToString("#,0.00;(#,0.00)", CultureInfo.InvariantCulture);

    private static float Cm(double cm) => RdlLayout.In(cm / 2.54);

    private static float In(double inches) => RdlLayout.In(inches);

    private static void AtCm(LayersDescriptor layers, double top, double left, double width, double height, Action<IContainer> content) =>
        layers.At(top / 2.54, left / 2.54, width / 2.54, height / 2.54, content);

    private static void Letter(PageDescriptor page)
    {
        GeckoPdf.Page(page);
        page.Size(PageSizes.Letter);
        page.Margin(In(0.1));
        page.DefaultTextStyle(t => t.FontSize(8).FontFamily(GeckoPdf.LatinFont, GeckoPdf.ThaiFont));
    }

    // ── TMS.Billing.CreditInvoiceReceipt ──

    internal static byte[] RenderCredit(InvoiceForm f)
    {
        GeckoPdf.EnsureInitialised();
        var s = f.Seller;
        return Document.Create(document => document.Page(page =>
        {
            Letter(page);

            page.Header().Height(In(5.02222)).Layers(layers =>
            {
                layers.PrimaryLayer();
                void Letterhead(double top, string text) =>
                    layers.At(top, 0.125, 7.94791, 0.25, c => c.AlignCenter().Text(text).FontSize(10).Bold());
                // The RDL's letterhead was SCT's, hard-coded; it is the branch's invoicing company (MDM), else the RDL's.
                Letterhead(0.08055, s is null ? "SIAM CONTAINER TERMINAL CO.,LTD" : s.LegalNameEn);
                Letterhead(0.38611, s is null ? "102 MOO 2, TEPARAK ROAD, AMPHUR BANGSAOTHONG, SAMUTPRAKRAN TH 10540" : s.Address ?? "");
                Letterhead(0.64305, s is null ? "GST Reg No. 2015-0001/0003" : "GST Reg No. " + (s.TaxId ?? ""));
                Letterhead(1.01111, "CREDIT INVOICE");

                AtCm(layers, 3.54718, 17.64302, 2.68962, 0.6, c => c.Text(f.InvoiceNo));
                AtCm(layers, 4.49093, 17.64302, 2.68962, 0.6, c => c.Text(f.InvoiceDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)));
                AtCm(layers, 5.61861, 4.60908, 14.57351, 0.50271, c => c.Text(f.CustomerName));
                AtCm(layers, 6.23421, 3.65658, 15.60538, 0.50271, c => c.Text(f.Address1).FontSize(7));
                AtCm(layers, 6.8201, 3.65658, 11.78687, 0.47096, c => c.Text(f.Address2).FontSize(7));
                AtCm(layers, 6.8201, 16.46708, 3.77031, 0.47096, c => c.Text(f.Phone));
                AtCm(layers, 8.15657, 3.41316, 8.05625, 0.6, c => c.Text($"BEING CHARGE FOR YOUR {f.BookingType} SHIPMENT AS FOLLOW :"));
                AtCm(layers, 8.93296, 3.41316, 11.46056, 0.6, c => c.Text($"VESSEL & VOY : {f.VesselName}V. {f.Voyage}"));
                AtCm(layers, 8.93296, 15.46052, 0.80667, 0.6, c => c.Text(f.BookingType == "EXPORT" ? "ETD" : "ETA"));
                AtCm(layers, 8.93296, 16.4083, 2.32361, 0.6, c => c.Text(f.Eta?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? ""));
            });

            page.Content().PaddingTop(Cm(0.11289)).PaddingLeft(Cm(1.57475)).Width(Cm(17.68721)).Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    foreach (var width in new[] { 1.00422, 9.10876, 2.54, 2.49423, 2.54 }) cols.ConstantColumn(Cm(width));
                });
                // The RDL's two static rows print once, above the lines (no RepeatOnNewPage).
                table.Cell().ColumnSpan(5).Height(Cm(0.30895));
                table.Cell().Box(minHeight: Cm(0.52917));
                table.Cell().Box(minHeight: Cm(0.52917)).Text("SERVICE CHARGE").Bold();
                table.Cell().Box(minHeight: Cm(0.52917)).Text("Selling Rate").Bold();
                table.Cell().Box(minHeight: Cm(0.52917)).Text("GST Amount").Bold();
                table.Cell().Box(minHeight: Cm(0.52917)).Text("Selling Amount").Bold();
                var line = Cm(0.52062);
                foreach (var row in f.Rows)
                {
                    table.Cell().Box(minHeight: line).AlignCenter().Text(row.Item.ToString(CultureInfo.InvariantCulture));
                    table.Cell().Box(minHeight: line).Text(row.Description);
                    table.Cell().Box(minHeight: line).AlignRight().Text(Money(row.Amount));
                    table.Cell().Box(minHeight: line).AlignRight().Text(Money(row.Tax));
                    table.Cell().Box(minHeight: line).AlignRight().Text(Money(row.Amount + row.Tax));
                }
            });

            page.Footer().Height(In(3.01194)).Layers(layers =>
            {
                layers.PrimaryLayer();
                layers.At(0, 5.8271, 0.91254, 0.21122, c => c.Text("Invoice Amount"));
                layers.At(0.29317, 5.8271, 0.91254, 0.21122, c => c.Text("GST Amount"));
                layers.At(0.58633, 5.8271, 0.90373, 0.21122, c => c.Text("Total Amount"));
                AtCm(layers, 0, 17.5875, 2.31784, 0.5365, c => c.AlignRight().Text(Money(f.Amount)));
                AtCm(layers, 0.74464, 17.5875, 2.31784, 0.5365, c => c.AlignRight().Text(Money(f.Tax)));
                AtCm(layers, 1.48928, 17.60987, 2.29547, 0.5365, c => c.AlignRight().Text(Money(f.Total)));
                AtCm(layers, 3.12496, 2.57897, 16.80658, 0.51329, c => c.Text(VectorAmountInWords.ExpandPrice(f.Total).ToUpperInvariant()).FontSize(7));
            });

            if (f.Voided) page.Foreground().VoidWatermark();
        })).GeneratePdf();
    }

    // ── TMS.Billing.CashInvoiceReceipt ──

    internal static byte[] RenderCash(InvoiceForm f) =>
        Document.Create(document => document.Page(page =>
        {
            GeckoPdf.EnsureInitialised();
            Letter(page);

            page.Header().Height(In(3.77542)).Layers(layers =>
            {
                layers.PrimaryLayer();
                layers.At(0.69625, 2.73584, 0.50348, 0.19792, c => c.Text("สาขาที่ 2").FontSize(7.5f));
                layers.At(0.69625, 5.186, 0.50348, 0.19792, c => c.Text("สาขาที่ 3").FontSize(7.5f));
                AtCm(layers, 5.62907, 3.78038, 2.18251, 0.49417, c => c.Text(f.InvoiceDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)));
                AtCm(layers, 6.15294, 6.15103, 10.15528, 0.49417, c => c.Text(f.CustomerName));
                AtCm(layers, 6.16882, 17.11475, 3.0039, 0.49417, c => c.Text(f.InvoiceNo));
                AtCm(layers, 6.77207, 4.36421, 10.99723, 0.49417, c => c.AlignMiddle().Text(f.Address1));
                AtCm(layers, 6.89907, 17.11475, 3.0039, 0.49417, c => c.Text(f.OrderNo));
                AtCm(layers, 7.31237, 4.36421, 9.39385, 0.49417, c => c.Text(f.Address2));
                AtCm(layers, 7.31237, 14.11204, 2.09902, 0.49417, c => c.Text(f.Phone));
                AtCm(layers, 7.95062, 4.36421, 6.91206, 0.49417, c => c.Text(f.VesselName.Length == 0 ? "" : $"{f.VesselName} V. {f.Voyage}"));
                AtCm(layers, 7.95062, 11.82146, 1.9366, 0.49417, c => c.Text(f.Eta?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? ""));
                AtCm(layers, 7.95062, 17.11475, 3.0039, 0.49417, c => c.Text(f.SubBlNo));
            });

            page.Content().PaddingTop(Cm(0.17639)).PaddingLeft(Cm(1.45659)).Width(Cm(18.1518)).Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    foreach (var width in new[] { 1.0534, 14.62297, 2.47543 }) cols.ConstantColumn(Cm(width));
                });
                // The RDL's Item / Description / Service heading row is hidden: the stationery carries it.
                table.Cell().Box(minHeight: Cm(0.635));
                table.Cell().Box(minHeight: Cm(0.635)).Text("SERVICE CHARGE");
                table.Cell().Box(minHeight: Cm(0.635));
                var line = Cm(0.635);
                foreach (var row in f.Rows)
                {
                    table.Cell().Box(minHeight: line).Text(row.Item.ToString(CultureInfo.InvariantCulture));
                    table.Cell().Box(minHeight: line).Text(row.Description);
                    table.Cell().Box(minHeight: line).AlignRight().Text(Money(row.Amount));
                }
            });

            page.Footer().Height(In(4.63834)).Layers(layers =>
            {
                layers.PrimaryLayer();
                AtCm(layers, 0.11289, 17.10838, 2.5, 0.50475, c => c.AlignRight().Text(Money(f.Amount)));
                AtCm(layers, 0.79403, 17.10839, 2.5, 0.50475, c => c.AlignRight().Text(Money(f.Tax)));
                AtCm(layers, 1.54544, 17.10838, 2.5, 0.50475, c => c.AlignRight().Text(Money(f.Total)));
                AtCm(layers, 2.28314, 17.10838, 2.5, 0.50475, c => c.AlignRight().Text(Money(f.Total)));
                AtCm(layers, 3.07792, 3.45653, 14.46042, 0.51329, c => c.Text(VectorAmountInWords.ExpandPrice(f.Total).ToUpperInvariant()).FontSize(7));
                // How it was paid (SourceForm CASH only). Gecko records no cheque date, so that box stays empty.
                if (f.Payment is { } p)
                {
                    layers.At(1.84992, 1.36084, 2.25454, 0.23622, c => c.Text(p.Channels));
                    layers.At(1.84992, 3.73344, 0.98425, 0.23622, c => c.Text(p.ChequeRef ?? ""));
                    layers.At(1.84992, 6.02662, 0.98425, 0.23622, c => c.AlignRight().Text(Money(p.Nett)));
                }
            });

            if (f.Voided) page.Foreground().VoidWatermark();
        })).GeneratePdf();
}

/// <param name="BookingType">Vector's BookingTypeDescription (IMPORT, EXPORT, …), of the first line's booking.</param>
/// <param name="Payment">Null for a credit invoice (the RDL's SourceForm CREDIT): the payment boxes print empty.</param>
internal sealed record InvoiceForm(
    string InvoiceNo, DateTimeOffset InvoiceDate, string CustomerName, string Address1, string Address2, string Phone,
    string BookingType, string VesselName, string Voyage, DateTimeOffset? Eta, string SubBlNo, string OrderNo,
    decimal Amount, decimal Tax, decimal Total, InvoiceFormPayment? Payment, bool Voided,
    IReadOnlyList<InvoiceFormRow> Rows, InvoicingCompanyRef? Seller);

internal sealed record InvoiceFormLine(string ChargeCode, string Description, decimal UnitRate, string? Size, decimal Amount, decimal Tax);

internal sealed record InvoiceFormRow(int Item, string Description, decimal Amount, decimal Tax);

/// <param name="Channels">The channels paid by, in order ("CHEQUE + CASH" as the RDL wrote it; Gecko also has TRANSFER, CARD…).</param>
/// <param name="Nett">What was received: the total less withholding tax (the RDL's NettAmount).</param>
internal sealed record InvoiceFormPayment(string Channels, string? ChequeRef, decimal Nett);
