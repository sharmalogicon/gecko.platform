using System.Globalization;
using Gecko.Data.Documents;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Tos.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Application.Reports;

/// <summary>What every accounting report is run for: the depot, its days, who prints it and when.</summary>
/// <param name="BranchName">Vector's BranchName parameter: the top line of the page header.</param>
/// <param name="Start">The first instant of <paramref name="From"/> at the depot; <paramref name="End"/> the first after <paramref name="To"/>.</param>
internal sealed record AccountingReportContext(
    BranchClockInfo Branch, string BranchName, DateOnly From, DateOnly To, DateTimeOffset Start, DateTimeOffset End,
    string PrintedBy, DateTimeOffset PrintedOn, AccountingReports.Letterhead Letterhead);

/// <summary>
/// Vector's TMS.Accounting.* RDLs rebuilt over Gecko receipts (owner 2026-10-10, ACCOUNTING_REPORTS_GAP_ANALYSIS.md):
/// the RDL's parameters, headers, columns, formats and totals — the totals computed correctly, receipts issued by
/// Gecko only. A receipt is a cash tax invoice (cashier.receipt): ISSUED, or VOIDED (Vector's Status = 0, "deleted").
/// </summary>
internal static class AccountingReports
{
    public const string Money = "#,0.00;(#,0.00)";
    private const string Issued = "ISSUED";
    private const string Voided = "VOIDED";

    /// <summary>The company block printed under a report's heading: legal name, address, tax id, tax branch.</summary>
    public sealed record Letterhead(string Name, string Address, string TaxId, string TaxBranch);

    /// <summary>
    /// What the RDLs hard-code for Vector's BranchID 240, the branch KORAKIT runs as (TMS.Accounting.SalesTax body
    /// textboxes). Owner 2026-10-10: the report header is taken from the RDL. Printed while the depot's company in
    /// master data has no tax id; once it has one, master data is printed instead. Never used on receipts.
    /// </summary>
    public static readonly Letterhead Vector240 = new(
        "บริษัท สยามคอนเทนเนอร์ทรานสปอร์ตแอนด์เทอร์มินอล จำกัด",
        "102 หมู่ 2 ถ.เทพารักษ์ ต.บางเสาธง กิ่งอำเภอบางเสาธง จ.สมุทรปราการ 10250",
        "0105531101642",
        "สาขา 2");

    public static Letterhead LetterheadOf(InvoicingCompanyRef? seller) =>
        seller is null || string.IsNullOrWhiteSpace(seller.TaxId)
            ? Vector240
            : new(seller.LegalNameLocal ?? seller.LegalNameEn, seller.Address ?? "", seller.TaxId, TaxBranch(seller));

    /// <summary>The seller block's tax branch, as Thai tax documents print it.</summary>
    public static string TaxBranch(InvoicingCompanyRef? seller) => seller switch
    {
        { IsHeadOffice: true } => "สำนักงานใหญ่",
        { TaxBranchNo: { } no } when int.TryParse(no, NumberStyles.None, CultureInfo.InvariantCulture, out var n) => $"สาขา {n}",
        _ => "",
    };

    /// <summary>
    /// TMS.Accounting.SalesTax (Report.usp_Accounting_SalesTax) — รายงานภาษีขาย: one line per cash receipt in
    /// receipt-number order, its value before VAT and its VAT; a voided receipt is listed with 0.00 and "DELETED".
    /// <paramref name="bookingType"/> (IMPORT/EXPORT/REPO/INTERNAL) keeps the receipts with a line for a booking of
    /// that type; a receipt spanning bookings of two types is listed once (Vector's DISTINCT would list it twice).
    /// </summary>
    public static async Task<TabularReport> SalesTaxAsync(
        RevenueDbContext db, ITosBookingHeaders tos, AccountingReportContext c, string? bookingType, CancellationToken ct)
    {
        var receipts = await db.Receipts.AsNoTracking()
            .Where(r => r.BranchId == c.Branch.BranchId && r.ReceiptAt >= c.Start && r.ReceiptAt < c.End
                        && (r.Status == Issued || r.Status == Voided))
            .Select(r => new { r.ReceiptId, r.ReceiptNo, r.ReceiptAt, r.PayerName, r.SubtotalAmount, r.TaxAmount, r.Status })
            .ToListAsync(ct);

        if (bookingType is not null)
        {
            var ids = receipts.Select(r => r.ReceiptId).ToList();
            var lines = await (from l in db.ReceiptLines.AsNoTracking()
                               join ch in db.Charges.AsNoTracking() on l.ChargeId equals ch.ChargeId
                               where ids.Contains(l.ReceiptId) && ch.BookingId != null
                               select new { l.ReceiptId, BookingId = ch.BookingId!.Value }).Distinct().ToListAsync(ct);
            var headers = await tos.HeadersAsync(lines.Select(l => l.BookingId).Distinct().ToList(), ct);
            var keep = lines.Where(l => headers.TryGetValue(l.BookingId, out var h) && h.BookingTypeCode == bookingType)
                .Select(l => l.ReceiptId).ToHashSet();
            receipts = receipts.Where(r => keep.Contains(r.ReceiptId)).ToList();
        }

        var rows = new List<TabularRow>();
        foreach (var r in receipts.OrderBy(r => r.ReceiptNo, StringComparer.Ordinal))
        {
            var live = r.Status == Issued;
            rows.Add(new TabularRow([
                c.Branch.LocalDate(r.ReceiptAt), r.ReceiptNo, r.PayerName,
                live ? r.SubtotalAmount : 0m, live ? r.TaxAmount : 0m, live ? "" : "DELETED"]));
        }
        var issued = receipts.Where(r => r.Status == Issued).ToList();
        rows.Add(new TabularRow([null, null, "รวมทั้งสิ้น", issued.Sum(r => r.SubtotalAmount), issued.Sum(r => r.TaxAmount), null], RowKind.Total));

        return new TabularReport(
            FileName: $"SalesTax_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 1.0,
            Heading:
            [
                new(c.BranchName, 10, Bold: true),
                new("รายงานภาษีขาย", 12, Bold: true),
                new($"ประจำเดือน : {c.From.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)}", 10, Bold: true),
            ],
            HeadingRight:
            [
                new($"Printed By: {c.PrintedBy}"),
                new($"Printed On: {c.PrintedOn.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}"),
            ],
            Preamble: [new(c.Letterhead.Name), new(c.Letterhead.Address)],
            PreambleRight: [new($"เลขประจำตัวผู้เสียภาษีอากร : {c.Letterhead.TaxId}"), new(c.Letterhead.TaxBranch)],
            Columns:
            [
                new(2.2, "dd/MM/yy", CellAlign.Center),
                new(3.8),
                new(10.5),
                new(3.5, Money, CellAlign.Right),
                new(3.5, Money, CellAlign.Right),
                new(2.5, null, CellAlign.Center),
            ],
            HeaderRows:
            [
                [new("วัน เดือน ปี", RowSpan: 2), new("ใบกำกับภาษี"), new("ชื่อสินค้า / ผู้รับบริการ", RowSpan: 2), new("มูลค่าสินค้า"), new("จำนวนเงิน"), new("หมายเหตุ", RowSpan: 2)],
                [new("เล่ม/เลขที่"), new("บริการ (บาท)"), new("ภาษีมูลค่าเพิ่ม(บาท)")],
            ],
            Rows: rows);
    }

    /// <summary>
    /// TMS.Accounting.WithholdingTax (Report.usp_Accounting_WitholdingTax) — บัญชีแสดงภาษีถูกหัก ณ ที่จ่าย: the issued
    /// cash receipts on which the customer withheld tax, one line each in receipt-number order — the base (value
    /// before VAT) and the tax withheld — and the grand total under the table. Voided receipts are left out, as Vector does.
    /// </summary>
    public static async Task<TabularReport> WithholdingTaxAsync(RevenueDbContext db, AccountingReportContext c, CancellationToken ct)
    {
        var receipts = await db.Receipts.AsNoTracking()
            .Where(r => r.BranchId == c.Branch.BranchId && r.ReceiptAt >= c.Start && r.ReceiptAt < c.End
                        && r.Status == Issued && r.WithholdingTaxAmount > 0)
            .Select(r => new { r.ReceiptNo, r.ReceiptAt, r.PayerName, r.SubtotalAmount, r.WithholdingTaxAmount })
            .ToListAsync(ct);

        var rows = receipts.OrderBy(r => r.ReceiptNo, StringComparer.Ordinal)
            .Select(r => new TabularRow([
                c.Branch.LocalDate(r.ReceiptAt), r.ReceiptNo, r.PayerName, "ค่าบริการ", r.SubtotalAmount, r.WithholdingTaxAmount, null]))
            .ToList();
        rows.Add(new TabularRow([null, null, null, "รวมทั้งสิ้น", receipts.Sum(r => r.SubtotalAmount), receipts.Sum(r => r.WithholdingTaxAmount), null], RowKind.Total));

        return new TabularReport(
            FileName: $"WithholdingTax_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 1.0,
            Heading:
            [
                new(c.BranchName, 10, Bold: true),
                new(c.Letterhead.Name, 10, Bold: true),
                new("บัญชีแสดงภาษีถูกหัก ณ ที่จ่าย สรุปยอดแต่ละลูกหนี้", 12, Bold: true),
                new($"ประจำเดือน: {c.From.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}", 12, Bold: true),
            ],
            HeadingRight:
            [
                new($"Printed By {c.PrintedBy}"),
                new($"Printed On {c.PrintedOn.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}"),
            ],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(2.6, "dd MMM yyyy", CellAlign.Center),
                new(3.6),
                new(8.5),
                new(3.0, null, CellAlign.Center),
                new(3.6, Money, CellAlign.Right),
                new(3.6, Money, CellAlign.Right),
                new(2.4),
            ],
            HeaderRows:
            [
                [new("วัน เดือน ปี"), new("เลขที่ใบเสร็จ"), new("บริษัท"), new("เงินได้ที่จ่าย"), new("จำนวนเงินหัก ณ ที่จ่าย"), new("ภาษีถูกหัก ณ ที่จ่าย"), new("หมายเหตุ")],
            ],
            Rows: rows);
    }

    /// <summary>
    /// TMS.Accounting.CreditInvoiceListing (Report.usp_Accounting_CreditInvoiceListing) — รายงานการออกใบแจ้งหนี้: the issued
    /// credit invoices of the depot days in two tables, EXS (EXPORT and REPO bookings) then IMS (IMPORT), each with its
    /// unlabelled total, and the "Total" of both under them. As in the RDL an invoice for an INTERNAL booking is in neither.
    /// An invoice is listed once (Vector's DISTINCT repeated one spanning booking types or lines), under the booking type
    /// of its first line on a booking. Filters: <paramref name="agentCode"/> (a line on a booking of that shipping line),
    /// <paramref name="customerCode"/> (the payer), <paramref name="bookingType"/>.
    /// </summary>
    public static async Task<TabularReport> CreditInvoiceListingAsync(
        RevenueDbContext db, ITosBookingHeaders tos, AccountingReportContext c,
        string? agentCode, string? customerCode, string? bookingType, CancellationToken ct)
    {
        var invoices = await db.Invoices.AsNoTracking()
            .Where(i => i.BranchId == c.Branch.BranchId && i.IssuedAt >= c.Start && i.IssuedAt < c.End
                        && i.InvoiceType == "CREDIT" && i.Status == Issued
                        && (customerCode == null || i.PayerPartyCode == customerCode))
            .Select(i => new { i.InvoiceId, i.InvoiceNo, i.PayerName, i.IssuedAt, i.SubtotalAmount, i.TaxAmount, i.TotalAmount })
            .ToListAsync(ct);
        var ids = invoices.Select(i => i.InvoiceId).ToList();
        var lines = (await db.InvoiceLines.AsNoTracking()
                .Where(l => ids.Contains(l.InvoiceId) && l.BookingId != null)
                .Select(l => new { l.InvoiceId, l.LineNo, BookingId = l.BookingId!.Value }).ToListAsync(ct))
            .ToLookup(l => l.InvoiceId);
        var headers = await tos.HeadersAsync(lines.SelectMany(g => g.Select(l => l.BookingId)).Distinct().ToList(), ct);

        var rows = invoices
            .Select(i =>
            {
                var booked = lines[i.InvoiceId].OrderBy(l => l.LineNo).Select(l => headers.GetValueOrDefault(l.BookingId)).OfType<TosBookingHeader>().ToList();
                return (Invoice: i, Type: booked.FirstOrDefault()?.BookingTypeCode, Bookings: booked);
            })
            .Where(x => agentCode is null || x.Bookings.Any(b => string.Equals(b.LineCode, agentCode, StringComparison.OrdinalIgnoreCase)))
            .Where(x => bookingType is null || x.Bookings.Any(b => b.BookingTypeCode == bookingType))
            .OrderBy(x => x.Invoice.InvoiceNo, StringComparer.Ordinal)
            .ToList();

        TabularColumn[] columns =
        [
            new(1.2, null, CellAlign.Center), new(3.6), new(8.0), new(2.2, null, CellAlign.Center), new(2.4), new(2.4),
            new(3.0, Money, CellAlign.Right), new(2.6, Money, CellAlign.Right), new(3.0, Money, CellAlign.Right), new(1.6),
        ];
        IReadOnlyList<IReadOnlyList<HeaderCell>> Header(string first) =>
            [[new(first), new("Invoice No"), new("Customer Name"), new("Date"), new("Reimbursement"), new("Transport"),
              new("Service"), new("VAT 7 %"), new("TOTAL"), new("COM")]];

        (List<TabularRow> Rows, decimal Service, decimal Vat) Section(params string[] types)
        {
            var mine = rows.Where(x => x.Type is not null && types.Contains(x.Type)).ToList();
            var output = mine.Select((x, n) => new TabularRow([
                n + 1, x.Invoice.InvoiceNo, x.Invoice.PayerName,
                c.Branch.LocalDate(x.Invoice.IssuedAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                null, null, x.Invoice.SubtotalAmount, x.Invoice.TaxAmount, x.Invoice.TotalAmount, null])).ToList();
            var (service, vat) = (mine.Sum(x => x.Invoice.SubtotalAmount), mine.Sum(x => x.Invoice.TaxAmount));
            // The RDL's section TOTAL is Σ Service + Σ VAT.
            output.Add(new TabularRow([null, null, null, null, null, null, service, vat, service + vat, null], RowKind.Subtotal));
            return (output, service, vat);
        }
        var exs = Section("EXPORT", "REPO");
        var ims = Section("IMPORT");
        var (allService, allVat) = (exs.Service + ims.Service, exs.Vat + ims.Vat);

        return new TabularReport(
            FileName: $"CreditInvoiceListing_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 1.0,
            Heading:
            [
                new(c.Letterhead.Name, 10, Bold: true),
                new("รายงานการออกใบแจ้งหนี้ /INVOICE ", 10, Bold: true),
                new($"From : {c.From.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}  To : {c.To.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}", 10, Bold: true),
                new($"AgentCode: {agentCode} BookingType: {bookingType}", 10, Bold: true),
            ],
            HeadingRight:
            [
                new($"Printed By : {c.PrintedBy}"),
                new($"Printed On : {c.PrintedOn.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}"),
            ],
            Preamble: [],
            PreambleRight: [],
            Columns: columns,
            HeaderRows: Header("EXS"),
            Rows: exs.Rows,
            After:
            [
                new TabularBlock(null, columns, Header("IMS"), ims.Rows),
                new TabularBlock(null, columns, [],
                    [new TabularRow(["Total", null, null, null, null, null, allService, allVat, allService + allVat, null], RowKind.Total)]),
            ]);
    }
}
