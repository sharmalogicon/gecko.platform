using System.Globalization;
using Gecko.Data.Documents;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Tos.Contracts;

namespace Gecko.Revenue.Application.Reports;

/// <summary>
/// Vector's per-invoice lift charge lists (TMS.Accounting.InboundSCT / InboundSCT1): the lift charges billed in the window
/// — on a receipt (cash) or an invoice (credit), by its date — on bookings of the RDL's order types. Owner 2026-10-10
/// defaults: the RDL's codes plus the tenant's (billing.report_charge_column), amounts the charges' own (the RDLs summed
/// unit prices), boxes counted once, voided receipts and cancelled charges out, the agent is the booking's shipping line.
/// </summary>
internal static class LiftChargeReports
{
    private const string Money = "#,0.00;(#,0.00);\"-\"";

    /// <summary>A billed lift line with its booking, box type and the customer named on the booking.</summary>
    public sealed record Line(OperationChargeReports.BilledCharge Billed, TosBookingHeader Booking, string? Size, string? CustomerName, string? VesselName);

    /// <param name="Customer">The payer the line was billed to (Vector's InvoiceHeader.CustomerCode).</param>
    public sealed record Filter(string? LineCode, string? Customer, string? CarrierRef, string? VesselCode, string? Voyage, string? BookingType = null);

    private static async Task<List<Line>> LinesAsync(
        RevenueDbContext db, ITosBookingHeaders tos, IMasterDataReferences master, AccountingReportContext c,
        string reportKey, IReadOnlyDictionary<string, string> codes, string column, IReadOnlySet<string> orderTypes, Filter f, CancellationToken ct)
    {
        var tenant = await OperationChargeReports.TenantMapAsync(db, reportKey, ct);
        var billed = (await OperationChargeReports.BilledAsync(db, c, ct))
            .Where(b => (tenant.GetValueOrDefault(b.ChargeCode) ?? codes.GetValueOrDefault(b.ChargeCode)) == column)
            .Where(b => f.Customer is null || string.Equals(b.Payer, f.Customer, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var headers = await tos.HeadersAsync(billed.Select(b => b.BookingId).OfType<Guid>().Distinct().ToList(), ct);
        bool Wanted(Guid? id) => id is { } bookingId && headers.TryGetValue(bookingId, out var h) && h.Status != "CANCELLED"
            && orderTypes.Contains(h.OrderTypeCode)
            && (f.LineCode is null || string.Equals(h.LineCode, f.LineCode, StringComparison.OrdinalIgnoreCase))
            && (f.CarrierRef is null || string.Equals(h.CarrierRef, f.CarrierRef, StringComparison.OrdinalIgnoreCase))
            && (f.VesselCode is null || string.Equals(h.VesselCode, f.VesselCode, StringComparison.OrdinalIgnoreCase))
            && (f.Voyage is null || string.Equals(h.Voyage, f.Voyage, StringComparison.OrdinalIgnoreCase))
            && (f.BookingType is null || string.Equals(h.BookingTypeCode, f.BookingType, StringComparison.OrdinalIgnoreCase));
        billed = billed.Where(b => Wanted(b.BookingId)).ToList();

        var boxTypes = await tos.ContainerTypesAsync(billed.Select(b => b.Box).Distinct().ToList(), ct);
        var typeCodes = boxTypes.Values.Distinct().ToList();
        var types = typeCodes.Count == 0 ? new Dictionary<string, EquipmentTypeRef>() : await master.EquipmentTypesAsync(typeCodes, ct);
        var used = billed.Select(b => headers[b.BookingId!.Value]).ToList();
        var customerCodes = used.Select(h => h.CustomerCode).OfType<string>().Distinct().ToList();
        var parties = customerCodes.Count == 0 ? new Dictionary<string, PartyRef>() : await master.PartiesAsync(customerCodes, ct);
        var vesselCodes = used.Select(h => h.VesselCode).OfType<string>().Distinct().ToList();
        var vessels = vesselCodes.Count == 0 ? new Dictionary<string, VesselRef>() : await master.VesselsAsync(vesselCodes, ct);

        return billed.Select(b =>
        {
            var h = headers[b.BookingId!.Value];
            return new Line(b, h, OperationChargeReports.SizeType(types, boxTypes.GetValueOrDefault(b.Box)).Size,
                h.CustomerCode is { } cc ? parties.GetValueOrDefault(cc)?.Name ?? cc : null,
                h.VesselCode is { } v ? vessels.GetValueOrDefault(v)?.VesselName ?? v : null);
        }).ToList();
    }

    public const string InboundSctKey = "INBOUND_SCT";

    /// <summary>TMS.Accounting.InboundSCT's codes: SL004 LIFT ON CHARGE (credit and cash), SL011-CA LIFT ON (VAS).</summary>
    public static readonly IReadOnlyDictionary<string, string> InboundSctCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SL004-CR"] = "LIFT_ON", ["SL004-CA"] = "LIFT_ON", ["SL011-CA"] = "LIFT_ON",
    };

    private static readonly IReadOnlySet<string> ImportOrderTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IMP CY/CY", "IMP CFS", "IMP CYD" };

    /// <summary>
    /// TMS.Accounting.InboundSCT (Report.usp_Accounting_InboundSCT) — import lift-on per cash invoice: a row per receipt or
    /// invoice of lift-on on IMP CY/CY, IMP CFS and IMP CYD bookings — date, the booking's customer, vessel, voyage, the
    /// invoice no, ICD NO. (blank), B/L, the lift-on rate, boxes by 20'/40' and the amount; the second AMOUNT (SL003-CR,
    /// which the RDL's query never returned) prints 0 and the third blank, as the RDL. No totals, as the RDL.
    /// Owner 2026-10-10 defaults: AMOUNT is the charges' amount (the RDL summed the unit rate); 20'/40' count boxes once;
    /// any movement (the RDL took FULL OUT, or FULL IN for CYD, or none); an invoice billed to a payer other than the
    /// booking's customer is kept (the RDL dropped it when no customer was asked for).
    /// </summary>
    public static async Task<TabularReport> InboundSctAsync(
        RevenueDbContext db, ITosBookingHeaders tos, IMasterDataReferences master, AccountingReportContext c, Filter f, CancellationToken ct)
    {
        var lines = await LinesAsync(db, tos, master, c, InboundSctKey, InboundSctCodes, "LIFT_ON", ImportOrderTypes, f, ct);
        var invoices = lines.GroupBy(x => x.Billed.BilledNo)
            .OrderBy(g => g.First().Booking.OrderNo, StringComparer.Ordinal)
            .ThenBy(g => g.First().Booking.CarrierRef, StringComparer.Ordinal).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var rows = invoices.Select((g, n) =>
        {
            var first = g.First();
            int Boxes(string size) => g.Where(x => x.Size == size).Select(x => x.Billed.Box).Distinct().Count();
            return new TabularRow([
                n + 1, c.Branch.LocalDate(g.Min(x => x.Billed.BilledAt)), first.CustomerName, first.VesselName, first.Booking.Voyage, g.Key,
                null, first.Booking.CarrierRef, g.Max(x => x.Billed.UnitRate), Boxes("20"), Boxes("40"), g.Sum(x => x.Billed.Amount), 0m, null]);
        }).ToList();

        var dates = $"START DATE {c.From.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}    FINISH DATE {c.To.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";
        return new TabularReport(
            FileName: $"InboundSCT_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 1.0,
            Heading: [new(c.BranchName, 10, Bold: true), new($"Agent: {lines.FirstOrDefault()?.Booking.LineCode}    {dates}")],
            HeadingRight: [],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(1.0, null, CellAlign.Center), new(1.6, "dd/MM/yy", CellAlign.Center), new(4.2), new(3.0), new(1.6), new(2.6), new(1.6),
                new(2.8), new(1.8, Money, CellAlign.Center), new(1.0, null, CellAlign.Center), new(1.0, null, CellAlign.Center),
                new(2.0, "#,0.00;(#,0.00)", CellAlign.Right), new(1.6, "0", CellAlign.Right), new(1.6),
            ],
            HeaderRows:
            [
                [new("ITEM"), new("DATE"), new("CUSTOMER"), new("VESSEL"), new("VOYAGE"), new("CASH INVOICE NO"), new("ICD NO."), new("BL"),
                 new("LIFT ON"), new("20'"), new("40'"), new("AMOUNT"), new("AMOUNT"), new("AMOUNT")],
            ],
            Rows: rows);
    }

    public const string InboundSct1Key = "INBOUND_SCT1";

    /// <summary>TMS.Accounting.InboundSCT1's codes: SL003 LIFT OFF CHARGE FOR CARGO, credit and cash.</summary>
    public static readonly IReadOnlyDictionary<string, string> InboundSct1Codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SL003-CR"] = "LIFT_OFF", ["SL003-CA"] = "LIFT_OFF",
    };

    private static readonly IReadOnlySet<string> ExportCyOrderTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EXP CY/CY-IN", "EXP CY/CY" };

    /// <summary>The RDL's REUND: the share of the lift-off refunded to the line.</summary>
    private const decimal Sct1Refund = 0.9m;

    /// <summary>
    /// TMS.Accounting.InboundSCT1 (Report.usp_Accounting_InboundSCT1) — รายงานค่าภาระผ่านท่า: a row per receipt or invoice ×
    /// container of lift-off for cargo on EXP CY/CY and EXP CY/CY-IN bookings — invoice date and no, container, the
    /// booking's customer, LIFT OFF and the 90 % REUND — then the grand total.
    /// Owner 2026-10-10 defaults: LIFT OFF is the charges' amount (the RDL summed the unit rate); the total's REUND is 90 %
    /// of the total (the RDL used the line amount for cash lines there and the rate in the rows).
    /// </summary>
    public static async Task<TabularReport> InboundSct1Async(
        RevenueDbContext db, ITosBookingHeaders tos, IMasterDataReferences master, AccountingReportContext c, Filter f, CancellationToken ct)
    {
        var lines = await LinesAsync(db, tos, master, c, InboundSct1Key, InboundSct1Codes, "LIFT_OFF", ExportCyOrderTypes, f, ct);
        var invoices = lines.GroupBy(x => x.Billed.BilledNo).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();

        var rows = new List<TabularRow>();
        foreach (var (g, n) in invoices.Select((g, n) => (g, n)))
            foreach (var box in g.GroupBy(x => x.Billed.Box).OrderBy(b => b.First().Billed.ContainerNo ?? "", StringComparer.Ordinal))
            {
                var amount = box.Sum(x => x.Billed.Amount);
                rows.Add(new TabularRow([n + 1, c.Branch.LocalDate(g.Min(x => x.Billed.BilledAt)), g.Key, box.First().Billed.ContainerNo,
                    box.First().CustomerName, amount, amount * Sct1Refund]));
            }
        var total = lines.Sum(x => x.Billed.Amount);
        rows.Add(new TabularRow([null, null, null, null, null, total, total * Sct1Refund], RowKind.Total));

        string D(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        return new TabularReport(
            FileName: $"InboundSCT1_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 1.5,
            Heading: [new(c.BranchName, 10, Bold: true), new("รายงานค่าภาระผ่านท่า", 10, Bold: true), new($"{D(c.From)} - {D(c.To)}")],
            HeadingRight: [new($"Print Date: {c.PrintedOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}")],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(1.2, null, CellAlign.Center), new(2.2, "dd/MM/yy", CellAlign.Center), new(3.0), new(3.0), new(6.0),
                new(2.6, Money, CellAlign.Right), new(2.6, Money, CellAlign.Right),
            ],
            HeaderRows:
            [
                [new("ITEM"), new("INVOICE DATE"), new("CASH INVOICE NO"), new("CONTAINER No"), new("CUSTOMER NAME"), new("LIFT OFF"), new("REUND")],
            ],
            Rows: rows);
    }

    public const string LiftOnRefundKey = "LIFT_ON_REFUND";

    /// <summary>TMS.Accounting.LiftOnChargeRefundSummary's code: SL004-CA, the cash LIFT ON CHARGE.</summary>
    public static readonly IReadOnlyDictionary<string, string> LiftOnRefundCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SL004-CA"] = "LIFT_ON",
    };

    private static readonly IReadOnlySet<string> RefundOrderTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IMP CY/CY", "IMP CYD" };

    /// <summary>The RDL's refund to T.S LINE.</summary>
    private const decimal LiftOnRefund = 0.7m;

    /// <summary>
    /// TMS.Accounting.LiftOnChargeRefundSummary (Report.usp_Accounting_LiftOnChargeRefundSummary) — SUMMARY OF LIFT ON
    /// CHARGE REFUND TO T.S LINE CO.,LTD.: a row per import lift-on line paid in the window — vessel, voyage, ETA, B/L,
    /// receipt no and date, the box counted in its band (FCL = IMP CY/CY, DD = IMP CYD, by 20'/40'/45') and 70 % of its
    /// amount refunded; then the total row. The addressee, ATTN line, band captions and 70 % are the RDL's.
    /// Owner 2026-10-10 defaults: every band refunds (the RDL's typo left CYD 20'/40' out), only lift-on lines print (the
    /// RDL printed every line of the invoice as dashes), voided receipts are out, the refund is 70 % of the line's amount.
    /// </summary>
    public static async Task<TabularReport> LiftOnRefundAsync(
        RevenueDbContext db, ITosBookingHeaders tos, ITosBookedBoxes booked, IMasterDataReferences master, AccountingReportContext c,
        Filter f, CancellationToken ct)
    {
        var lines = (await LinesAsync(db, tos, master, c, LiftOnRefundKey, LiftOnRefundCodes, "LIFT_ON", RefundOrderTypes,
                f with { BookingType = "IMPORT" }, ct))
            .OrderBy(x => x.Booking.OrderNo, StringComparer.Ordinal).ThenBy(x => x.Booking.CarrierRef, StringComparer.Ordinal)
            .ThenBy(x => x.Billed.BilledNo, StringComparer.Ordinal)
            .ToList();
        var boxes = await booked.BoxesByIdAsync(lines.Select(x => x.Billed.Box).Distinct().ToList(), ct);

        string[] sizes = ["20", "40", "45"];
        object?[] Bands(Line x) =>
        [
            .. sizes.Select(size => (object?)(x.Booking.OrderTypeCode == "IMP CY/CY" && x.Size == size ? 1 : 0)),
            .. sizes.Select(size => (object?)(x.Booking.OrderTypeCode == "IMP CYD" && x.Size == size ? 1 : 0)),
        ];
        bool Banded(Line x) => x.Size is "20" or "40" or "45";
        var rows = lines.Select((x, n) => new TabularRow([
            n + 1, x.Booking.VesselCode, x.Booking.Voyage,
            boxes.GetValueOrDefault(x.Billed.Box)?.Eta is { } eta ? c.Branch.LocalDate(eta) : null,
            x.Booking.CarrierRef, x.Billed.BilledNo, c.Branch.LocalDate(x.Billed.BilledAt), .. Bands(x),
            Banded(x) ? x.Billed.Amount * LiftOnRefund : 0m])).ToList();
        rows.Add(new TabularRow([
            null, null, null, null, null, null, null,
            .. Enumerable.Range(0, 6).Select(i => (object?)lines.Sum(x => (int)Bands(x)[i]!)),
            lines.Where(Banded).Sum(x => x.Billed.Amount) * LiftOnRefund], RowKind.Total));

        const string Count = "0;(0);\"-\"";
        TabularColumn N() => new(1.7, Count, CellAlign.Center);
        return new TabularReport(
            FileName: $"LiftOnChargeRefundSummary_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 0.5,
            Heading:
            [
                new(c.BranchName, 10, Bold: true),
                new("ATTN :\u00a0 K .\u00a0 SUNAN\u00a0 \u00a0 \u00a0 JINUNARUG"),
                new("SUMMARY  OF  LIFT  ON  CHARGE  REFUND  TO  T.S LINE  CO.,LTD.", 9, Bold: true),
            ],
            HeadingRight: [],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(1.0, null, CellAlign.Center), new(2.0), new(1.6), new(1.6, "dd/MM/yy", CellAlign.Center), new(3.0), new(2.6),
                new(1.6, "dd/MM/yy", CellAlign.Center), N(), N(), N(), N(), N(), N(), new(2.4, Money, CellAlign.Right),
            ],
            HeaderRows:
            [
                [new("ITEM"), new("VESSEL"), new("VOYAGE"), new("ETA"), new("B/L NO."), new("RECEIPT", ColSpan: 2),
                 new("FCL (PER CONT.) ลากตู้", ColSpan: 3), new("DD (PER CONT.) ลากตู้", ColSpan: 3), new("REFUND 70%")],
                [new(""), new(""), new(""), new(""), new(""), new("NO."), new("DATE"), new("1,550.-/20'"), new("2,650.-/40'"), new("3,100.-/45'"),
                 new("1,850.-/20'"), new("3,150.-/40'"), new("3,700.-/45'"), new("TOTAL")],
            ],
            Rows: rows);
    }
}
