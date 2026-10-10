using System.Globalization;
using Gecko.Data.Documents;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Tos.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Application.Reports;

/// <summary>
/// Vector's upload sheets laid out in a shipping line's own import format (TMS.Accounting.NYKReport): the charges a
/// depot billed the line, one row per box, move and charge, with the line's tariff item for each charge code.
/// </summary>
internal static class LineReports
{
    public const string NykKey = "NYK";

    /// <summary>TMS.Accounting.NYKReport's Tariff Item per charge code (the RDL's Switch); a tenant's rows map its own codes.</summary>
    public static readonly IReadOnlyDictionary<string, string> NykTariffItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SL001-CR"] = "CYCDLIFTE", ["SL002-CR"] = "CYCDLIFTF", ["SC006-CR"] = "CYCDSTOE", ["SC010-CR"] = "CYCDSTOF",
        ["SE001-CR"] = "CYCDLIFTE", ["SE003-CR"] = "MRCNPTI", ["SE002-CR"] = "MRCNPRECL", ["SE004-CR"] = "CGOMIT",
    };

    /// <param name="BookingType">Gecko's booking type code; null = every type.</param>
    public sealed record NykFilter(string LineCode, string? VesselCode, string? Voyage, string? BookingType, string? MovementCode,
        string? CarrierRef, string? OrderType);

    /// <summary>
    /// TMS.Accounting.NYKReport (Report.usp_Accounting_NYK) — &lt;&lt;NYK Report&gt;&gt;: the line's charges on its boxes moved
    /// in the window, a row per box, move and charge in NYK's vendor-invoice format — equipment type and no, F/E, QTY 1,
    /// THB, cost (the charge's amount before VAT), activity date (the move's; a charge on no move — pre-cool — the box's
    /// empty gate-out), vessel, voyage, tariff item, Office BKK — the rest blank as in the RDL; then the cost total.
    /// Owner 2026-10-10 defaults: the line is a parameter (default NYK), matched on the booking's shipping line;
    /// cancelled charges left out; Facility and Vendor print blank (the RDL hard-coded SCT's BKK40/SCT and EKA's
    /// BKK34/EKA by Vector branch; Gecko has no per-branch line code yet); rows by container, then activity date.
    /// </summary>
    public static async Task<TabularReport> NykAsync(
        RevenueDbContext db, ITosGateMoves gate, ITosBookedBoxes booked, ITosBookingHeaders tos, AccountingReportContext c,
        NykFilter f, CancellationToken ct)
    {
        var moves = (await gate.MovesAsync(c.Branch.BranchId, c.Start, c.End,
                new TosGateMoveFilter(LineCode: f.LineCode, BookingTypeCode: f.BookingType, OrderTypeCode: f.OrderType), ct))
            .ToList();
        var boxes = await booked.BoxesByIdAsync(moves.Select(m => m.BookingContainerId).Distinct().ToList(), ct);
        var headers = await tos.HeadersAsync(boxes.Values.Select(b => b.BookingId).Distinct().ToList(), ct);
        bool Wanted(TosBookedBox b) => headers.TryGetValue(b.BookingId, out var h) && h.Status != "CANCELLED"
            && (f.VesselCode is null || string.Equals(b.VesselCode, f.VesselCode, StringComparison.OrdinalIgnoreCase))
            && (f.Voyage is null || string.Equals(b.Voyage, f.Voyage, StringComparison.OrdinalIgnoreCase))
            && (f.CarrierRef is null || string.Equals(h.CarrierRef, f.CarrierRef, StringComparison.OrdinalIgnoreCase));
        moves = moves.Where(m => boxes.TryGetValue(m.BookingContainerId, out var b) && Wanted(b)).ToList();

        var tenant = await OperationChargeReports.TenantMapAsync(db, NykKey, ct);
        string? Tariff(string code) => tenant.GetValueOrDefault(code) ?? NykTariffItems.GetValueOrDefault(code);

        // Part 1: the charges each move earned (only moves of the asked movement, when one is asked).
        var charged = f.MovementCode is { } movement
            ? moves.Where(m => string.Equals(m.MovementCode, movement, StringComparison.OrdinalIgnoreCase)).ToList()
            : moves;
        var byMove = await OperationChargeReports.ChargesOfAsync(db, charged, ct);
        var lines = charged
            .SelectMany(m => byMove[m.GateTransactionId]
                .Where(x => Tariff(x.Code) is not null && x.Amount != 0)
                .Select(x => (Box: boxes[m.BookingContainerId], Status: m.FullEmpty == "FULL" ? "F" : "E",
                    Type: m.EquipmentTypeCode ?? boxes[m.BookingContainerId].EquipmentTypeCode, x.Amount,
                    Day: (DateOnly?)c.Branch.LocalDate(m.TransactionAt), Tariff: Tariff(x.Code)!)))
            .ToList();

        // Part 2: a charge on the box but on no move (Vector's pre-cool, SE002-CR): dated by the box's empty gate-out.
        var ids = moves.Select(m => m.BookingContainerId).Distinct().ToList();
        foreach (var slice in ids.Chunk(2000))
        {
            var loose = await db.Charges.AsNoTracking()
                .Where(x => x.BookingContainerId != null && slice.Contains(x.BookingContainerId.Value) && x.Status != "CANCELLED"
                            && x.GateTransactionId == null && x.EarnedGateTransactionId == null
                            && (x.MovementCode == null || x.MovementCode == "") && x.Amount != 0)
                .Select(x => new { Box = x.BookingContainerId!.Value, x.ChargeCode, x.Amount })
                .ToListAsync(ct);
            lines.AddRange(loose.Where(x => Tariff(x.ChargeCode) is not null).Select(x =>
            {
                var box = boxes[x.Box];
                return (Box: box, Status: "E", Type: box.EquipmentTypeCode, x.Amount,
                    Day: box.EmptyOut is { } at ? (DateOnly?)c.Branch.LocalDate(at) : null, Tariff: Tariff(x.ChargeCode)!);
            }));
        }
        lines = lines.OrderBy(x => x.Box.ContainerNo, StringComparer.Ordinal).ThenBy(x => x.Day).ToList();

        var rows = lines.Select((x, n) => new TabularRow([
            n + 1, x.Type, x.Box.ContainerNo, x.Status, "1", "THB", x.Amount, x.Day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            x.Box.VesselCode, x.Box.Voyage, null, null, null, x.Tariff, null, null, null, null, null, null, null, "BKK", null, null])).ToList();
        rows.Add(new TabularRow([
            null, null, null, null, null, null, lines.Sum(x => x.Amount), null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, null], RowKind.Total));

        string D(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        TabularColumn T(double width) => new(width);
        return new TabularReport(
            FileName: $"NYKReport_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A3Landscape,
            MarginCm: 1.0,
            Heading:
            [
                new("<<NYK Report>>", 10, Bold: true),
                new($"Vessel:{f.VesselCode} Voy:{f.Voyage}"),
                new($"BookingType:{f.BookingType} MovementCode:{f.MovementCode} "),
                new($"FromeDate:{D(c.From)} ToDate:{D(c.To)}"),
            ],
            HeadingRight:
            [
                new($"Printed By : {c.PrintedBy}"),
                new($"Printed On : {c.PrintedOn.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}"),
            ],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(1.0, null, CellAlign.Center), T(1.8), T(2.6), new(1.2, null, CellAlign.Center), new(1.0, null, CellAlign.Center), T(1.4),
                new(2.2, "#,0.00;(#,0.00)", CellAlign.Right), new(2.2, null, CellAlign.Center), T(1.8), T(1.8), T(1.6), T(1.4), T(1.6), T(2.4),
                T(1.6), T(2.0), T(1.8), T(2.0), T(1.8), T(2.0), T(2.0), T(1.2), T(2.4), T(1.4),
            ],
            HeaderRows:
            [
                [new("SEQ"), new("Equipment Type"), new("Equipment #"), new("Status"), new("QTY"), new("Currency"), new("Cost Amount"),
                 new("Activity Date"), new("Vessel"), new("Voyage"), new("Service"), new("Port"), new("Facility"), new("Tariff Item"),
                 new("Vendor"), new("Invoice Total"), new("Invoice Vat"), new("Withholding Tax"), new("Invoice #"), new("Receive Date"),
                 new("Invoice Date"), new("Office"), new("External Reference"), new("Error")],
            ],
            Rows: rows);
    }
}
