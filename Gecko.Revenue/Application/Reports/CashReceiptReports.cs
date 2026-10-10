using System.Globalization;
using Gecko.Data.Documents;
using Gecko.Identity.Contracts;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Tos.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Application.Reports;

/// <summary>
/// Vector's three cash receipt listings (TMS.Accounting.CashReceiptByUser / CashReceiptByLiner / CashReceiptByCompany,
/// procs usp_Accounting_CashReceiptByUser / ByCompany) over Gecko receipts — owner 2026-10-10:
///   * the RDL's columns, headers, sections and total rows; the totals computed correctly: each receipt counted once,
///     a column is the lines' amount (rate × qty), VAT and W/H tax as the receipts carry them, voided receipts kept
///     out of the issued totals (they are listed in their own section, as the RDL does);
///   * the money columns by charge code: the RDL's own (SCT) lists, plus each tenant's codes from
///     billing.report_charge_column (gecko_revenue 31); a code in no column falls in "Other";
///   * a receipt's line with no price is not counted and a receipt with none is not listed (Vector's SellRate > 0).
/// </summary>
internal static class CashReceiptReports
{
    private const string Money = AccountingReports.Money;
    private const string Issued = "ISSUED";
    private const string Voided = "VOIDED";
    public const string ColumnsReport = "CASH_RECEIPT";
    public const string CompanyReport = "CASH_RECEIPT_COMPANY";

    /// <summary>The 15 money columns of the User/Liner listings, in RDL order (columns 8–22).</summary>
    public static readonly string[] Columns =
    [
        "LIFT_ON", "LIFT_OFF", "LOLO", "HANDLING", "RELOCATION", "STUFFING", "FACILITIES",
        "GATE_1", "GATE_2", "WEIGHT_SCALE", "CLEANING", "ELECTRICITY", "CONTAINER_STORAGE", "CARGO_STORAGE", "OTHER",
    ];

    private static Dictionary<string, string> Lists(params (string Key, string[] Codes)[] lists) =>
        lists.SelectMany(l => l.Codes.Select(c => (c, l.Key))).ToDictionary(x => x.c, x => x.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly (string, string[])[] SharedLists =
    [
        ("LIFT_ON", ["SL004-CA", "SL011-CA"]),
        ("LIFT_OFF", ["SL003-CA", "SL001-CA"]),
        ("LOLO", ["SL002-CA", "SL009-CA"]),
        ("HANDLING", ["SH001-CA", "SH002-CA"]),
        ("RELOCATION", ["SR001-CA", "SR002-CA"]),
        ("STUFFING", ["SU001-CA", "SU002-CA", "SS001-CA"]),
        ("FACILITIES", ["SF001-CA", "SF002-CA"]),
        ("GATE_1", ["SA001-CA"]),
        ("GATE_2", ["SA002-CA", "SA003-CA"]),
        ("WEIGHT_SCALE", ["SW001-CA", "SW003-CA"]),
        ("ELECTRICITY", ["SE004-CA", "SE007-CA", "SE002-CA", "SE003-CA", "SM001-CA"]),
        ("CONTAINER_STORAGE", ["SC007-CA", "SC011-CA", "SC006-CA", "SC010-CA"]),
        ("CARGO_STORAGE", ["SC008-CA", "SC012-CA"]),
        ("OTHER", ["SC005-CA", "SC009-CA", "SD001-CA", "SD002-CA", "SD003-CA", "SD004-CA", "SE001-CA", "SE005-CA", "SE006-CA",
                   "SF003-CA", "SF004-CA", "SL005-CA", "SL006-CA", "SL007-CA", "SL008-CA", "SL010-CA", "SO001-CA", "SO002-CA",
                   "SP001-CA", "SS003-CA", "SS004-CA", "SW002-CA"]),
    ];

    /// <summary>TMS.Accounting.CashReceiptByUser's lists (cleaning includes the SC001-CA-C/H/S/W variants).</summary>
    public static readonly IReadOnlyDictionary<string, string> UserLists =
        Lists([.. SharedLists, ("CLEANING", ["SC001-CA", "SC001-CA-C", "SC001-CA-H", "SC001-CA-S", "SC001-CA-W", "SR003-CA"])]);

    /// <summary>Tms.Accounting.CashReceiptByLiner's lists: its cleaning is SC001-CA and SR003-CA only.</summary>
    public static readonly IReadOnlyDictionary<string, string> LinerLists =
        Lists([.. SharedLists, ("CLEANING", ["SC001-CA", "SR003-CA"])]);

    /// <summary>TMS.Accounting.CashReceiptByCompany's summary rows, in RDL order, with their Thai labels.</summary>
    public static readonly (string Key, string Label)[] CompanyRows =
    [
        ("GATE", "ค่าผ่านท่า"), ("PORT_CHARGE", "ค่าภาระผ่านท่า"), ("CLEANING", "ค่าทำความสะอาด"), ("LIFT", "ค่ายกตู้"), ("WEIGHT", "ค่าชั่งน้ำหนัก"),
    ];

    public static readonly IReadOnlyDictionary<string, string> CompanyLists = Lists(
        ("GATE", ["SA001-CA", "SA002-CA"]),
        ("PORT_CHARGE", ["SL003-CA"]),
        ("CLEANING", ["SC001-CA", "SC001-CA-C", "SC001-CA-H", "SC001-CA-S", "SC001-CA-W"]),
        ("LIFT", ["SL001-CA", "SL002-CA"]),
        ("WEIGHT", ["SW001-CA"]));

    // ── Data ────────────────────────────────────────────────────────────────

    private sealed record Line(short LineNo, string ChargeCode, decimal Amount, decimal Tax, string? ContainerNo, Guid ChargeId);

    private sealed record Row(
        Guid ReceiptId, string ReceiptNo, DateTimeOffset ReceiptAt, Guid CashierUserId, Guid? VoidedBy, string PayerName,
        decimal Subtotal, decimal Tax, decimal Total, decimal Wht, bool IsIssued, IReadOnlyList<Line> Lines)
    {
        public string? AgentCode { get; set; }
        public string? SizeType { get; set; }
        public string? TruckPlate { get; set; }
        public string? ContainerNo => Lines.FirstOrDefault(l => l.ContainerNo is not null)?.ContainerNo;
    }

    /// <summary>
    /// The receipts at the depot in [start, end), with their priced lines (ordered) and their first line's agent,
    /// size/type and truck. <paramref name="bookingType"/> keeps a receipt with a line for a booking of that type.
    /// </summary>
    private static async Task<List<Row>> LoadAsync(
        RevenueDbContext db, ITosBookingHeaders tos, ITosTruckVisits? trucks, IMasterDataReferences master,
        Guid branchId, DateTimeOffset start, DateTimeOffset end, string? bookingType, CancellationToken ct)
    {
        var receipts = await db.Receipts.AsNoTracking()
            .Where(r => r.BranchId == branchId && r.ReceiptAt >= start && r.ReceiptAt < end && (r.Status == Issued || r.Status == Voided))
            .Select(r => new
            {
                r.ReceiptId, r.ReceiptNo, r.ReceiptAt, r.CashierUserId, r.VoidedBy, r.PayerName,
                r.SubtotalAmount, r.TaxAmount, r.TotalAmount, r.WithholdingTaxAmount, r.Status,
            }).ToListAsync(ct);
        var ids = receipts.Select(r => r.ReceiptId).ToList();

        var lines = (await db.ReceiptLines.AsNoTracking()
                .Where(l => ids.Contains(l.ReceiptId) && l.UnitRate > 0)
                .Select(l => new { l.ReceiptId, l.LineNo, l.ChargeCode, l.Amount, l.TaxAmount, l.ContainerNo, l.ChargeId })
                .ToListAsync(ct))
            .ToLookup(l => l.ReceiptId, l => new Line(l.LineNo, l.ChargeCode, l.Amount, l.TaxAmount, l.ContainerNo, l.ChargeId));

        var rows = receipts
            .Where(r => lines[r.ReceiptId].Any())
            .Select(r => new Row(r.ReceiptId, r.ReceiptNo, r.ReceiptAt, r.CashierUserId, r.VoidedBy, r.PayerName,
                r.SubtotalAmount, r.TaxAmount, r.TotalAmount, r.WithholdingTaxAmount, r.Status == Issued,
                lines[r.ReceiptId].OrderBy(l => l.LineNo).ToList()))
            .ToList();

        var chargeIds = rows.SelectMany(r => r.Lines.Select(l => l.ChargeId)).Distinct().ToList();
        var charges = chargeIds.Count == 0 ? []
            : await db.Charges.AsNoTracking().Where(c => chargeIds.Contains(c.ChargeId))
                .Select(c => new { c.ChargeId, c.BookingId, c.BookingContainerId, c.TruckVisitId })
                .ToDictionaryAsync(c => c.ChargeId, ct);

        var headers = await tos.HeadersAsync(charges.Values.Select(c => c.BookingId).OfType<Guid>().Distinct().ToList(), ct);
        var boxTypes = await tos.ContainerTypesAsync(charges.Values.Select(c => c.BookingContainerId).OfType<Guid>().Distinct().ToList(), ct);
        var typeCodes = boxTypes.Values.Distinct().ToList();
        var types = typeCodes.Count == 0 ? new Dictionary<string, EquipmentTypeRef>() : await master.EquipmentTypesAsync(typeCodes, ct);
        var visits = trucks is null ? new Dictionary<Guid, TosTruckVisit>()
            : await trucks.VisitsAsync(charges.Values.Select(c => c.TruckVisitId).OfType<Guid>().Distinct().ToList(), ct);

        foreach (var row in rows)
        {
            var linked = row.Lines.Select(l => charges.GetValueOrDefault(l.ChargeId)).Where(c => c is not null).ToList();
            var first = linked.FirstOrDefault(c => c!.BookingId is not null);
            if (first?.BookingId is { } bookingId && headers.TryGetValue(bookingId, out var h)) row.AgentCode = h.LineCode;
            if (linked.FirstOrDefault(c => c!.BookingContainerId is not null)?.BookingContainerId is { } box
                && boxTypes.TryGetValue(box, out var code))
                row.SizeType = SizeType(types, code);
            if (linked.FirstOrDefault(c => c!.TruckVisitId is not null)?.TruckVisitId is { } visit && visits.TryGetValue(visit, out var v))
                row.TruckPlate = v.TruckPlate;
        }

        if (bookingType is not null)
        {
            var keep = rows.Where(r => r.Lines.Any(l =>
                    charges.GetValueOrDefault(l.ChargeId)?.BookingId is { } b && headers.TryGetValue(b, out var h) && h.BookingTypeCode == bookingType))
                .Select(r => r.ReceiptId).ToHashSet();
            rows = rows.Where(r => keep.Contains(r.ReceiptId)).ToList();
        }
        return rows;
    }

    /// <summary>Vector's Size &amp; " " &amp; Type: "20 GP" from Gecko's "20GP".</summary>
    private static string SizeType(IReadOnlyDictionary<string, EquipmentTypeRef> types, string code)
    {
        var size = types.GetValueOrDefault(code)?.SizeCode ?? (code.Length > 2 && char.IsDigit(code[0]) && char.IsDigit(code[1]) ? code[..2] : null);
        return size is not null && code.StartsWith(size, StringComparison.Ordinal) && code.Length > size.Length
            ? $"{size} {code[size.Length..]}"
            : code;
    }

    /// <summary>The tenant's own codes for a report (gecko_revenue 31), read under its RLS.</summary>
    private static async Task<Dictionary<string, string>> TenantMapAsync(RevenueDbContext db, string reportKey, CancellationToken ct) =>
        await db.ReportChargeColumns.AsNoTracking().Where(m => m.ReportKey == reportKey)
            .ToDictionaryAsync(m => m.ChargeCode, m => m.ColumnKey, StringComparer.OrdinalIgnoreCase, ct);

    // ── User and Liner ──────────────────────────────────────────────────────

    /// <summary>The 26 columns' money: the 15 buckets by charge code, then Total Charge, VAT, VAT Include, W/H Tax.</summary>
    private static decimal[] Money15(Row r, Func<string, string> column)
    {
        var sums = new decimal[Columns.Length];
        foreach (var l in r.Lines) sums[Array.IndexOf(Columns, column(l.ChargeCode))] += l.Amount;
        return sums;
    }

    private static object?[] Sums(IReadOnlyList<Row> rows, Func<string, string> column, string label)
    {
        var buckets = new decimal[Columns.Length];
        foreach (var r in rows)
        {
            var m = Money15(r, column);
            for (var i = 0; i < buckets.Length; i++) buckets[i] += m[i];
        }
        return [null, null, null, null, null, null, label, .. buckets.Cast<object?>(),
            rows.Sum(r => r.Subtotal), rows.Sum(r => r.Tax), rows.Sum(r => r.Total), rows.Sum(r => r.Wht)];
    }

    private static object?[] ReceiptCells(int no, object date, Row r, Func<string, string> column) =>
        [no, date, r.ReceiptNo, r.AgentCode, r.PayerName, r.ContainerNo, r.SizeType,
         .. Money15(r, column).Cast<object?>(), r.Subtotal, r.Tax, r.Total, r.Wht];

    private static IReadOnlyList<TabularColumn> ListingColumns(string dateFormat) =>
    [
        new(1.0, null, CellAlign.Center), new(dateFormat.Length > 10 ? 2.9 : 2.0, dateFormat, CellAlign.Center), new(3.0), new(1.8),
        new(5.0), new(2.6), new(1.5, null, CellAlign.Center),
        .. Enumerable.Range(0, 15).Select(_ => new TabularColumn(2.1, Money, CellAlign.Right)),
        new(2.4, Money, CellAlign.Right), new(2.1, Money, CellAlign.Right), new(2.4, Money, CellAlign.Right), new(2.0, Money, CellAlign.Right),
    ];

    private static IReadOnlyList<IReadOnlyList<HeaderCell>> ListingHeader(bool liner) =>
    [
        [
            new("No.", RowSpan: 2), new("Invoice Date", RowSpan: 2), new("Invoice No", RowSpan: 2), new("Agent Code", RowSpan: 2),
            new("Customer Name", RowSpan: 2), new(liner ? "Container No" : "Container No.", RowSpan: 2), new("Size/Type", RowSpan: 2),
            new(liner ? "Handling " : "Handling", ColSpan: 7),
            new("Gate Charge", RowSpan: 2), new("Gate Charge", RowSpan: 2), new(liner ? "weight scale" : "Weight Scale", RowSpan: 2),
            new(liner ? "cleaning" : "Cleaning", RowSpan: 2), new("Electricity Charge", RowSpan: 2),
            new(liner ? "Container storage" : "Container Storage", RowSpan: 2), new(liner ? "Cargo storage" : "Cargo Storage", RowSpan: 2),
            new(liner ? "other" : "Other", RowSpan: 2), new(liner ? "Total charge" : "Total Charge", RowSpan: 2),
            new("VAT", RowSpan: 2), new("VAT Include", RowSpan: 2), new("W/H Tax", RowSpan: 2),
        ],
        [
            new("Lift On"), new("Lift Off"), new(liner ? "LOLO " : "LOLO"), new(liner ? "handling" : "Handling"), new("Relocation"),
            new("Stuff/unStuff"), new("Facilities"),
        ],
    ];

    private static List<ReportText> ListingHeading(AccountingReportContext c, DateTime from, DateTime to) =>
    [
        new(c.BranchName, 10, Bold: true),
        new("CASH RECEIPT LISTING REPORT  by USER", 10, Bold: true),
        new($"Frome Date: {from.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)} To Date: {to.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}", 9, Bold: true),
    ];

    private static List<ReportText> PrintedBlock(AccountingReportContext c) =>
    [
        new($"Printed By: {c.PrintedBy}"),
        new($"Printed On: {c.PrintedOn.ToString("dd/MM/yyyy HH.mm", CultureInfo.InvariantCulture)}"),
    ];

    /// <summary>
    /// TMS.Accounting.CashReceiptByUser: a section per cashier ("USER ID :"), a line per receipt, the cashier's Total,
    /// a Grand Total; then "CANCELLED RECEIPTS" the same way, sectioned by who voided them (the RDL's ModifiedBy).
    /// <paramref name="cashierUserId"/>: the User ID filter (owner 2026-10-10: it filters).
    /// </summary>
    public static async Task<TabularReport> ByUserAsync(
        RevenueDbContext db, ITosBookingHeaders tos, IMasterDataReferences master, IUserDirectory users,
        AccountingReportContext c, DateTime from, DateTime to, string? bookingType, Guid? cashierUserId, CancellationToken ct)
    {
        var rows = await LoadAsync(db, tos, null, master, c.Branch.BranchId, c.Start, c.End, bookingType, ct);
        if (cashierUserId is { } only) rows = rows.Where(r => r.CashierUserId == only).ToList();
        var tenant = await TenantMapAsync(db, ColumnsReport, ct);
        string Column(string code) => tenant.GetValueOrDefault(code) ?? UserLists.GetValueOrDefault(code) ?? "OTHER";
        var names = await users.DisplayNamesAsync(rows.Select(r => r.CashierUserId).Concat(rows.Select(r => r.VoidedBy).OfType<Guid>()).Distinct(), ct);
        string Name(Guid? id) => id is { } g ? names.GetValueOrDefault(g) ?? g.ToString() : "";

        var output = new List<TabularRow>();
        void Section(IReadOnlyList<Row> set, Func<Row, Guid?> by, string banner)
        {
            foreach (var group in set.GroupBy(by).OrderBy(g => Name(g.Key), StringComparer.Ordinal))
            {
                output.Add(TabularRow.Banner($"{banner}{Name(group.Key)}"));
                var no = 0;
                foreach (var r in group.OrderBy(r => r.ReceiptNo, StringComparer.Ordinal))
                    output.Add(new TabularRow(ReceiptCells(++no, c.Branch.LocalDate(r.ReceiptAt), r, Column)));
                output.Add(new TabularRow(Sums(group.ToList(), Column, "Total"), RowKind.Subtotal));
            }
            output.Add(new TabularRow(Sums(set, Column, "Grand Total"), RowKind.Total));
        }

        Section(rows.Where(r => r.IsIssued).ToList(), r => r.CashierUserId, "USER ID : ");
        output.Add(TabularRow.Banner("CANCELLED RECEIPTS"));
        Section(rows.Where(r => !r.IsIssued).ToList(), r => r.VoidedBy, "USER ID: ");

        return new TabularReport(
            FileName: $"CashReceiptByUser_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A3Landscape, MarginCm: 0.5,
            Heading: ListingHeading(c, from, to), HeadingRight: PrintedBlock(c),
            Preamble: [], PreambleRight: [],
            Columns: ListingColumns("dd/MM/yyyy"), HeaderRows: ListingHeader(liner: false), Rows: output);
    }

    /// <summary>
    /// Tms.Accounting.CashReceiptByLiner: the same listing sectioned by shipping line ("AGENT CODE :", with the section's
    /// first receipt number beside it, as the RDL prints it), lines in order of their first receipt, a Total per line and
    /// no grand total; then "CANCELLED RECEIPTS". <paramref name="agentCode"/>, when given, keeps that line only.
    /// </summary>
    public static async Task<TabularReport> ByLinerAsync(
        RevenueDbContext db, ITosBookingHeaders tos, IMasterDataReferences master,
        AccountingReportContext c, DateTime from, DateTime to, string? agentCode, CancellationToken ct)
    {
        var rows = await LoadAsync(db, tos, null, master, c.Branch.BranchId, c.Start, c.End, null, ct);
        if (agentCode is not null) rows = rows.Where(r => string.Equals(r.AgentCode, agentCode, StringComparison.OrdinalIgnoreCase)).ToList();
        var tenant = await TenantMapAsync(db, ColumnsReport, ct);
        string Column(string code) => tenant.GetValueOrDefault(code) ?? LinerLists.GetValueOrDefault(code) ?? "OTHER";

        var output = new List<TabularRow>();
        void Section(IReadOnlyList<Row> set, string banner)
        {
            var groups = set.OrderBy(r => r.ReceiptNo, StringComparer.Ordinal).GroupBy(r => r.AgentCode ?? "");
            foreach (var group in groups)
            {
                var ordered = group.ToList();
                output.Add(TabularRow.Banner($"{banner}{group.Key}    {ordered[0].ReceiptNo}"));
                var no = 0;
                foreach (var r in ordered)
                    output.Add(new TabularRow(ReceiptCells(++no, c.Branch.Local(r.ReceiptAt).DateTime, r, Column)));
                output.Add(new TabularRow(Sums(ordered, Column, "Total"), RowKind.Subtotal));
            }
        }

        Section(rows.Where(r => r.IsIssued).ToList(), "AGENT CODE : ");
        output.Add(TabularRow.Banner("CANCELLED RECEIPTS"));
        Section(rows.Where(r => !r.IsIssued).ToList(), "AGENT CODE: ");

        return new TabularReport(
            FileName: $"CashReceiptByLiner_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A3Landscape, MarginCm: 0.5,
            Heading: ListingHeading(c, from, to), HeadingRight: PrintedBlock(c),
            Preamble: [], PreambleRight: [],
            Columns: ListingColumns("dd/MM/yyyy HH:mm"), HeaderRows: ListingHeader(liner: true), Rows: output);
    }

    // ── Company ─────────────────────────────────────────────────────────────

    /// <summary>
    /// TMS.Accounting.CashReceiptByCompany: every receipt of the depot days once, voided ones flagged "C"; the grand total
    /// (issued receipts); then "Summary by Charge Code for all above Invoices" — the issued lines' money before VAT, VAT
    /// and net, by the RDL's five charge rows and their total.
    /// </summary>
    public static async Task<TabularReport> ByCompanyAsync(
        RevenueDbContext db, ITosBookingHeaders tos, ITosTruckVisits trucks, IMasterDataReferences master,
        AccountingReportContext c, CancellationToken ct)
    {
        var rows = (await LoadAsync(db, tos, trucks, master, c.Branch.BranchId, c.Start, c.End, null, ct))
            .OrderBy(r => r.ReceiptNo, StringComparer.Ordinal).ToList();
        var tenant = await TenantMapAsync(db, CompanyReport, ct);
        string? Row(string code) => tenant.GetValueOrDefault(code) ?? CompanyLists.GetValueOrDefault(code);

        var output = new List<TabularRow>();
        var no = 0;
        foreach (var r in rows)
            output.Add(new TabularRow([r.IsIssued ? "" : "C", ++no, r.ReceiptNo, c.Branch.LocalDate(r.ReceiptAt), r.PayerName,
                r.TruckPlate, r.ContainerNo, r.Subtotal, r.Tax, r.Total]));
        var issued = rows.Where(r => r.IsIssued).ToList();
        output.Add(new TabularRow([null, null, null, null, null, null, null,
            issued.Sum(r => r.Subtotal), issued.Sum(r => r.Tax), issued.Sum(r => r.Total)], RowKind.Total));

        var issuedLines = issued.SelectMany(r => r.Lines).ToList();
        var summary = new List<TabularRow>();
        decimal net = 0, vat = 0;
        foreach (var (key, label) in CompanyRows)
        {
            var mine = issuedLines.Where(l => Row(l.ChargeCode) == key).ToList();
            var (amount, tax) = (mine.Sum(l => l.Amount), mine.Sum(l => l.Tax));
            (net, vat) = (net + amount, vat + tax);
            summary.Add(new TabularRow([label, amount, tax, amount + tax]));
        }
        summary.Add(new TabularRow(["รวม", net, vat, net + vat], RowKind.Total));

        return new TabularReport(
            FileName: $"CashReceiptByCompany_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape, MarginCm: 0.5,
            Heading:
            [
                new(c.BranchName, 10, Bold: true),
                new("CASHRECEIPT DETAIL LIST By Company", 10, Bold: true),
                new($"From Date: {c.From.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} To: {c.To.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}", 9),
            ],
            HeadingRight:
            [
                new($"Printed By: {c.PrintedBy}"),
                new($"Printed On: {c.PrintedOn.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}"),
            ],
            Preamble: [], PreambleRight: [],
            Columns:
            [
                new(1.4, null, CellAlign.Center), new(1.3, null, CellAlign.Center), new(3.2), new(2.0, "dd/MM/yyyy", CellAlign.Center),
                new(6.5), new(2.4), new(2.6), new(2.6, Money, CellAlign.Right), new(2.4, Money, CellAlign.Right), new(2.6, Money, CellAlign.Right),
            ],
            HeaderRows:
            [
                [new("Invoice Status"), new("ลำดับที่"), new("ใบกำกับภาษี"), new("วัน / เดือน / ปี"), new("ชื่อลูกค้า"), new("ทะเบียนรถ"),
                 new("เบอร์ตู้"), new("Amount"), new("Vat 7%"), new("NET")],
            ],
            Rows: output,
            After:
            [
                new TabularBlock("Summary by Charge Code for all above Invoices",
                    [new(4.0), new(3.0, Money, CellAlign.Right), new(3.0, Money, CellAlign.Right), new(3.0, Money, CellAlign.Right)],
                    [[new(""), new("รวมเงินก่อน VAT"), new("ภาษีมูลค่าเพิ่ม 7 %"), new("รวมเงินสุทธิ")]],
                    summary),
            ]);
    }
}
