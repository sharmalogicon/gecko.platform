using System.Globalization;
using Gecko.Data.Documents;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Tos.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Application.Reports;

/// <summary>
/// Vector's accounting reports that price gate moves (Operation.ContainerMovement joined to the booking statement):
/// the moves come from TOS (<see cref="ITosGateMoves"/>), the charges from billing.charge. Owner 2026-10-10: the RDL's
/// columns and totals, money columns by charge code (the RDL's codes, plus the tenant's in billing.report_charge_column),
/// totals corrected — the charge's amount, cancelled charges left out, no whole-baht rounding.
/// </summary>
internal static class OperationChargeReports
{
    /// <summary>The RDL's #,0.00;(#,0.00);'-' — a zero prints "-"; written with double quotes, which Excel reads too.</summary>
    private const string Money = "#,0.00;(#,0.00);\"-\"";

    /// <summary>
    /// The charges each move earned, as Vector joined a movement to its statement lines: a charge raised for the
    /// move (gate_transaction_id) or earned by it (earned_gate_transaction_id), else one on the same box for the same
    /// movement. A charge counts for one move only. Cancelled charges are left out.
    /// </summary>
    public static async Task<ILookup<Guid, (string Code, decimal Amount)>> ChargesOfAsync(
        RevenueDbContext db, IReadOnlyList<TosGateMove> moves, CancellationToken ct)
    {
        var ids = moves.Select(m => m.GateTransactionId).ToList();
        var boxes = moves.Select(m => m.BookingContainerId).Distinct().ToList();
        var charges = await db.Charges.AsNoTracking()
            .Where(c => c.Status != "CANCELLED"
                        && ((c.GateTransactionId != null && ids.Contains(c.GateTransactionId.Value))
                            || (c.EarnedGateTransactionId != null && ids.Contains(c.EarnedGateTransactionId.Value))
                            || (c.BookingContainerId != null && boxes.Contains(c.BookingContainerId.Value))))
            .Select(c => new { c.ChargeCode, c.Amount, c.GateTransactionId, c.EarnedGateTransactionId, c.BookingContainerId, c.MovementCode })
            .ToListAsync(ct);

        var byId = moves.ToDictionary(m => m.GateTransactionId);
        var owned = new List<(Guid Move, string Code, decimal Amount)>();
        foreach (var c in charges)
        {
            Guid? move = c.GateTransactionId is { } g && byId.ContainsKey(g) ? g
                : c.EarnedGateTransactionId is { } e && byId.ContainsKey(e) ? e
                : c.GateTransactionId is null && c.EarnedGateTransactionId is null
                    ? moves.FirstOrDefault(m => m.BookingContainerId == c.BookingContainerId && m.MovementCode == c.MovementCode)?.GateTransactionId
                    : null;
            if (move is { } owner) owned.Add((owner, c.ChargeCode, c.Amount));
        }
        return owned.ToLookup(o => o.Move, o => (o.Code, o.Amount));
    }

    /// <summary>The tenant's own codes for a report (gecko_revenue 31/32), read under its RLS.</summary>
    public static async Task<Dictionary<string, string>> TenantMapAsync(RevenueDbContext db, string reportKey, CancellationToken ct) =>
        await db.ReportChargeColumns.AsNoTracking().Where(m => m.ReportKey == reportKey)
            .ToDictionaryAsync(m => m.ChargeCode, m => m.ColumnKey, StringComparer.OrdinalIgnoreCase, ct);

    /// <summary>Vector's Size and Type from Gecko's type code: "20GP" → ("20", "GP").</summary>
    public static (string? Size, string? Type) SizeType(IReadOnlyDictionary<string, EquipmentTypeRef> types, string? code)
    {
        if (code is null) return (null, null);
        var size = types.GetValueOrDefault(code)?.SizeCode ?? (code.Length > 2 && char.IsDigit(code[0]) && char.IsDigit(code[1]) ? code[..2] : null);
        return size is not null && code.StartsWith(size, StringComparison.Ordinal) ? (size, code[size.Length..]) : (size, code);
    }

    public const string LiftOffWashingKey = "LIFT_OFF_WASHING";

    /// <summary>TMS.Accounting.LiftOffWashing's codes (the RDL's credit codes).</summary>
    public static readonly IReadOnlyDictionary<string, string> LiftOffWashingCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SL001-CR"] = "LIFT_OFF", ["SC001-CR-W"] = "DETERGENT", ["SC001-CR-C"] = "CHEMICAL",
    };

    /// <summary>
    /// TMS.Accounting.LiftOffWashing (Report.usp_Accounting_LiftOffWashing) — "Lift off / Cleaning": a line per container
    /// moved (by default an empty gate-in), its size, in-date and the lift-off, washing ("Detergent") and chemical
    /// charges on that move, then the grand total (no label). Containers are ordered by size, then as they moved.
    /// </summary>
    public static async Task<TabularReport> LiftOffWashingAsync(
        RevenueDbContext db, ITosGateMoves gate, IMasterDataReferences master, AccountingReportContext c,
        TosGateMoveFilter filter, string? size, string? type, CancellationToken ct)
    {
        var moves = await gate.MovesAsync(c.Branch.BranchId, c.Start, c.End, filter, ct);
        var codes = moves.Select(m => m.EquipmentTypeCode).OfType<string>().Distinct().ToList();
        var types = codes.Count == 0 ? new Dictionary<string, EquipmentTypeRef>() : await master.EquipmentTypesAsync(codes, ct);
        moves = moves.Where(m =>
        {
            var (s, t) = SizeType(types, m.EquipmentTypeCode);
            return (size is null || s == size) && (type is null || t == type);
        }).ToList();

        var charges = await ChargesOfAsync(db, moves, ct);
        var tenant = await TenantMapAsync(db, LiftOffWashingKey, ct);
        string? Column(string code) => tenant.GetValueOrDefault(code) ?? LiftOffWashingCodes.GetValueOrDefault(code);

        var lines = moves
            .GroupBy(m => m.ContainerNo)
            .Select(g =>
            {
                var first = g.First();
                var money = g.SelectMany(m => charges[m.GateTransactionId]).ToList();
                decimal Sum(string column) => money.Where(x => Column(x.Code) == column).Sum(x => x.Amount);
                return (Box: first, Size: SizeType(types, first.EquipmentTypeCode).Size ?? "",
                    LiftOff: Sum("LIFT_OFF"), Detergent: Sum("DETERGENT"), Chemical: Sum("CHEMICAL"));
            })
            .OrderBy(x => x.Size, StringComparer.Ordinal)
            .ToList();

        var rows = lines.Select((x, n) => new TabularRow([
            n + 1, x.Box.ContainerNo, x.Box.EquipmentTypeCode, c.Branch.LocalDate(x.Box.TransactionAt),
            x.LiftOff, x.Detergent, x.Chemical, x.LiftOff + x.Detergent + x.Chemical])).ToList();
        var (lift, detergent, chemical) = (lines.Sum(x => x.LiftOff), lines.Sum(x => x.Detergent), lines.Sum(x => x.Chemical));
        rows.Add(new TabularRow([null, null, null, null, lift, detergent, chemical, lift + detergent + chemical], RowKind.Total));

        var month = c.From.ToString("MMMM", CultureInfo.InvariantCulture);
        return new TabularReport(
            FileName: $"LiftOffWashing_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 1.5,
            Heading:
            [
                new(c.BranchName, 11, Bold: true),
                new($"Container gate out :{month} {c.To.Year} For Agent : {filter.LineCode}", 11, Bold: true),
                new("Lift off / Cleaning", 11, Bold: true),
            ],
            HeadingRight: [],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(1.4, null, CellAlign.Center), new(3.6), new(2.0, null, CellAlign.Center), new(2.4, "dd/MM/yy", CellAlign.Center),
                new(3.0, Money, CellAlign.Right), new(3.0, Money, CellAlign.Right), new(3.0, Money, CellAlign.Right), new(3.2, Money, CellAlign.Right),
            ],
            HeaderRows:
            [
                [new("Item"), new("CONTAINER NO."), new("SIZE"), new("IN-DATE"), new("LIFT OFF"), new("Detergent"), new("CHEMICAL"), new("TOTAL ")],
            ],
            Rows: rows,
            PageLabel: null);
    }

    /// <summary>A box's charge billed in the report's window: on an issued receipt dated in it, or an issued invoice.</summary>
    public sealed record BilledCharge(
        Guid Box, Guid? BookingId, string ChargeCode, string? MovementCode, decimal Amount, string BilledNo, DateTimeOffset BilledAt);

    /// <summary>
    /// Vector's "invoice date in the window", owner 2026-10-10: the receipt's date for a cash charge, the invoice's for a
    /// credit one (KORAKIT bills cash). Charges on a box only; cancelled charges, receipts and invoices left out.
    /// </summary>
    public static async Task<List<BilledCharge>> BilledAsync(RevenueDbContext db, AccountingReportContext c, CancellationToken ct)
    {
        var receipted = from x in db.Charges.AsNoTracking()
                        join r in db.Receipts on x.ReceiptId equals r.ReceiptId
                        where x.BranchId == c.Branch.BranchId && x.BookingContainerId != null && x.Status != "CANCELLED"
                              && r.Status == "ISSUED" && r.ReceiptAt >= c.Start && r.ReceiptAt < c.End
                        select new { Box = x.BookingContainerId!.Value, x.BookingId, x.ChargeCode, x.MovementCode, x.Amount, BilledNo = r.ReceiptNo, At = r.ReceiptAt };
        var invoiced = from x in db.Charges.AsNoTracking()
                       join i in db.Invoices on x.InvoiceId equals i.InvoiceId
                       where x.BranchId == c.Branch.BranchId && x.BookingContainerId != null && x.Status != "CANCELLED"
                             && i.Status == "ISSUED" && i.IssuedAt >= c.Start && i.IssuedAt < c.End
                       select new { Box = x.BookingContainerId!.Value, x.BookingId, x.ChargeCode, x.MovementCode, x.Amount, BilledNo = i.InvoiceNo, At = i.IssuedAt };
        return (await receipted.ToListAsync(ct)).Concat(await invoiced.ToListAsync(ct))
            .Select(x => new BilledCharge(x.Box, x.BookingId, x.ChargeCode, x.MovementCode, x.Amount, x.BilledNo, x.At))
            .ToList();
    }

    public const string StorageActivityKey = "STORAGE_ACTIVITY";

    /// <summary>TMS.Accounting.ContainerStorageActivityStandard's codes (the RDL's credit codes).</summary>
    public static readonly IReadOnlyDictionary<string, string> StorageActivityCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SC006-CR"] = "EMPTY_STORAGE", ["SC010-CR"] = "FULL_STORAGE", ["SL001-CR"] = "LOLO_EMPTY", ["SL002-CR"] = "LOLO_LADEN",
        ["SF001-CR"] = "FACILITY", ["SS001-CR"] = "STUFFING",
    };

    private sealed record StorageLine(
        TosBookedBox Box, decimal EmptyDays, decimal EmptyAmount, decimal LoloEmpty, decimal FullDays, decimal FullAmount,
        decimal LoloLaden, decimal Facility, decimal Stuffing);

    /// <summary>
    /// TMS.Accounting.ContainerStorageActivityStandard — CONTAINER STORAGE ACTIVITY: a line per booked box (container ×
    /// order type) with its empty and laden gate dates, storage days and amounts, LO/LO, facility and stuffing, and the
    /// grand total (no label); then the boxes counted by order type × size × dry/reefer; then LIFT OFF EMPTY, the empty
    /// lift-off billed to the line or agent rather than the customer.
    /// Owner 2026-10-10 defaults: the window is the booking's vessel ETA, or the box's first gate-in when the booking has
    /// no vessel call; a lift charge mapped LOLO is empty or laden by its move; totals corrected (the charge's amount,
    /// cancelled charges out, no whole-baht rounding, no HYUNDAI special case, no empty dates copied across bookings).
    /// </summary>
    public static async Task<TabularReport> StorageActivityAsync(
        RevenueDbContext db, ITosBookedBoxes booked, IMasterDataReferences master, AccountingReportContext c,
        TosBookedBoxFilter filter, CancellationToken ct)
    {
        var boxes = await booked.BoxesAsync(c.Branch.BranchId, c.Start, c.End, filter, ct);
        var typeCodes = boxes.Select(b => b.EquipmentTypeCode).Distinct().ToList();
        var types = typeCodes.Count == 0 ? new Dictionary<string, EquipmentTypeRef>() : await master.EquipmentTypesAsync(typeCodes, ct);
        var vessels = boxes.Select(b => b.VesselCode).OfType<string>().Distinct().ToList();
        var vesselNames = vessels.Count == 0 ? new Dictionary<string, VesselRef>() : await master.VesselsAsync(vessels, ct);

        var ids = boxes.Select(b => b.BookingContainerId).ToList();
        var charges = new List<(Guid Box, string Code, string? Movement, string BillTo, decimal Days, decimal Amount)>();
        foreach (var slice in ids.Chunk(2000))
            charges.AddRange((await db.Charges.AsNoTracking()
                    .Where(x => x.BookingContainerId != null && slice.Contains(x.BookingContainerId.Value) && x.Status != "CANCELLED")
                    .Select(x => new { Box = x.BookingContainerId!.Value, x.ChargeCode, x.MovementCode, x.BillTo, Days = x.ChargeableQuantity ?? x.Quantity, x.Amount })
                    .ToListAsync(ct))
                .Select(x => (x.Box, x.ChargeCode, x.MovementCode, x.BillTo, x.Days, x.Amount)));
        var tenant = await TenantMapAsync(db, StorageActivityKey, ct);
        string? Column(string code, string? movement) => (tenant.GetValueOrDefault(code) ?? StorageActivityCodes.GetValueOrDefault(code)) switch
        {
            "LOLO" => movement is not null && movement.StartsWith("FULL", StringComparison.OrdinalIgnoreCase) ? "LOLO_LADEN" : "LOLO_EMPTY",
            var other => other,
        };
        var byBox = charges.Select(x => (x.Box, Column: Column(x.Code, x.Movement), x.BillTo, x.Days, x.Amount))
            .Where(x => x.Column is not null).ToLookup(x => x.Box);

        DateOnly? Day(DateTimeOffset? at) => at is { } a ? c.Branch.LocalDate(a) : null;
        var lines = boxes.Select(b =>
        {
            var mine = byBox[b.BookingContainerId].ToList();
            decimal Amount(string column) => mine.Where(x => x.Column == column).Sum(x => x.Amount);
            decimal Days(string column) => mine.Where(x => x.Column == column).Sum(x => x.Days);
            return new StorageLine(b, Days("EMPTY_STORAGE"), Amount("EMPTY_STORAGE"), Amount("LOLO_EMPTY"),
                Days("FULL_STORAGE"), Amount("FULL_STORAGE"), Amount("LOLO_LADEN"), Amount("FACILITY"), Amount("STUFFING"));
        }).ToList();

        var rows = lines.Select(x => new TabularRow([
            x.Box.ContainerNo, x.Box.EquipmentTypeCode, Day(x.Box.EmptyIn), Day(x.Box.EmptyOut), x.EmptyDays, x.EmptyAmount, x.LoloEmpty,
            Day(x.Box.LadenIn), Day(x.Box.LadenOut), x.FullDays, x.FullAmount, x.LoloLaden,
            x.EmptyAmount + x.FullAmount, x.LoloEmpty + x.LoloLaden, x.Facility, x.Stuffing, x.Box.OrderTypeCode])).ToList();
        decimal Total(Func<StorageLine, decimal> pick) => lines.Sum(pick);
        rows.Add(new TabularRow([
            null, null, null, null, null, Total(x => x.EmptyAmount), Total(x => x.LoloEmpty), null, null, null, Total(x => x.FullAmount),
            Total(x => x.LoloLaden), Total(x => x.EmptyAmount + x.FullAmount), Total(x => x.LoloEmpty + x.LoloLaden),
            Total(x => x.Facility), Total(x => x.Stuffing), null], RowKind.Total));

        // The count: distinct containers by order type × 20'/40'/45' × DRY/REEFER.
        string[] sizes = ["20", "40", "45"];
        object?[] Count(IEnumerable<TosBookedBox> set)
        {
            var list = set.ToList();
            return sizes.SelectMany(size => new[] { false, true }.Select(reefer => (object?)list
                    .Where(b => SizeType(types, b.EquipmentTypeCode).Size == size && (types.GetValueOrDefault(b.EquipmentTypeCode)?.IsReefer ?? false) == reefer)
                    .Select(b => b.ContainerNo).Distinct().Count()))
                .ToArray();
        }
        var matrix = boxes.GroupBy(b => b.OrderTypeCode).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TabularRow([g.Key, .. Count(g)])).ToList();
        matrix.Add(new TabularRow(["Total", .. Count(boxes)], RowKind.Total));

        // LIFT OFF EMPTY: the empty lift-off billed to the line or agent, a line per container.
        var billed = lines
            .Select(x => (x.Box, Amount: byBox[x.Box.BookingContainerId].Where(y => y.Column == "LOLO_EMPTY" && y.BillTo != "CUSTOMER").Sum(y => y.Amount)))
            .Where(x => x.Amount != 0)
            .GroupBy(x => x.Box.ContainerNo).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Box: g.First().Box, Amount: g.Sum(x => x.Amount)))
            .ToList();
        var agentBilled = billed.Select(x => new TabularRow([x.Box.ContainerNo, x.Box.EquipmentTypeCode, Day(x.Box.EmptyIn), x.Amount])).ToList();
        agentBilled.Add(new TabularRow(["Total", null, null, billed.Sum(x => x.Amount)], RowKind.Total));

        var first = boxes.FirstOrDefault();
        var vesselName = first?.VesselCode is { } code ? vesselNames.GetValueOrDefault(code)?.VesselName ?? code : "";
        var window = $"ETD : {c.From.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} - {c.To.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";
        const string Days = "#,0;(#,0);\"-\"";

        return new TabularReport(
            FileName: $"ContainerStorageActivity_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 0.25,
            Heading:
            [
                new(c.BranchName, 10, Bold: true),
                new("CONTAINER STORAGE ACTIVITY", 10, Bold: true),
                new($"AGENT : {first?.LineCode}"),
                new($"VESSEL/VOY :{vesselName}    VOYAGE :{first?.Voyage}"),
                new(window),
            ],
            HeadingRight:
            [
                new($"Printed By: {c.PrintedBy}"),
                new($"Printed On: {c.PrintedOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}"),
            ],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(2.6), new(1.2, null, CellAlign.Center), new(1.5, "dd/MM/yy", CellAlign.Center), new(1.5, "dd/MM/yy", CellAlign.Center),
                new(1.4, Days, CellAlign.Right), new(1.9, Money, CellAlign.Right), new(1.7, Money, CellAlign.Right),
                new(1.5, "dd/MM/yy", CellAlign.Center), new(1.5, "dd/MM/yy", CellAlign.Center), new(1.4, Days, CellAlign.Right),
                new(1.9, Money, CellAlign.Right), new(1.7, Money, CellAlign.Right), new(1.9, Money, CellAlign.Right), new(1.9, Money, CellAlign.Right),
                new(1.7, Money, CellAlign.Right), new(1.7, Money, CellAlign.Right), new(2.4),
            ],
            HeaderRows:
            [
                [new("CONTAINER\nNO."), new("SIZE/\nTYPE"), new("EMPTY\nGATE IN DATE"), new("EMPTY\nGATE OUT DATE"), new("EMPTY\nSTORAGE\nDAYS"),
                 new("STORAGE\nAMOUNT"), new("LOLO EMPTY"), new("LADEN\nIN"), new("LADEN\nOUT"), new("FULL\nSTORAGE\nDAYS"),
                 new("STORAGE\nAMOUNT"), new("LOLO\nLADEN"), new("TOTAL\nSTORAGE"), new("TOTAL\nLOLO"), new("FACILITY CHARGE"),
                 new("STUFFING"), new("STATUS")],
            ],
            Rows: rows,
            After:
            [
                new TabularBlock(null,
                    [new(3.0), .. Enumerable.Range(0, 6).Select(_ => new TabularColumn(1.6, null, CellAlign.Center))],
                    [[new("", RowSpan: 2), new("20'", ColSpan: 2), new("40'", ColSpan: 2), new("45'", ColSpan: 2)],
                     [new("DRY"), new("REEFER"), new("DRY"), new("REEFER"), new("DRY"), new("REEFER")]],
                    matrix),
                new TabularBlock($"AGENT : {first?.LineCode}    {first?.Voyage}    {window}",
                    [new(3.0), new(2.0, null, CellAlign.Center), new(2.0, "dd/MM/yy", CellAlign.Center), new(2.6, Money, CellAlign.Right)],
                    [[new(""), new("SIZE/TYPE"), new("EMPTY"), new("LIFT OFF EMPTY")]],
                    agentBilled),
            ]);
    }

    /// <summary>
    /// TMS.Accounting.ContainerStorageActivityByVslVoy — CONTAINER STORAGE ACTIVITY for a vessel/voyage: the IMPORT boxes
    /// billed in the window, a line each with its empty and laden gate dates, its storage and LO/LO, order type and shipper
    /// (customer code); DAY, ค่าผ่านประตู, HAULAGE and FAS print blank as in the RDL. No totals, as in the RDL.
    /// Owner 2026-10-10 defaults: "invoiced in the window" is billed in it — the receipt's date for a cash charge, the
    /// invoice's for a credit one (KORAKIT bills cash); storage and LO/LO are the box's billed charges summed (the RDL showed
    /// the first line's rate and counted cleaning as storage); columns by the Container Storage Activity mapping.
    /// </summary>
    public static async Task<TabularReport> StorageActivityByVesselAsync(
        RevenueDbContext db, ITosBookingHeaders tos, ITosBookedBoxes booked, IMasterDataReferences master, AccountingReportContext c,
        string? lineCode, string? vesselCode, string? voyage, string? carrierRef, CancellationToken ct)
    {
        var billed = await BilledAsync(db, c, ct);

        var headers = await tos.HeadersAsync(billed.Select(b => b.BookingId).OfType<Guid>().Distinct().ToList(), ct);
        bool Wanted(Guid? bookingId) => bookingId is { } id && headers.TryGetValue(id, out var h) && h.BookingTypeCode == "IMPORT"
            && (lineCode is null || string.Equals(h.LineCode, lineCode, StringComparison.OrdinalIgnoreCase))
            && (vesselCode is null || string.Equals(h.VesselCode, vesselCode, StringComparison.OrdinalIgnoreCase))
            && (voyage is null || string.Equals(h.Voyage, voyage, StringComparison.OrdinalIgnoreCase))
            && (carrierRef is null || string.Equals(h.CarrierRef, carrierRef, StringComparison.OrdinalIgnoreCase));
        billed = billed.Where(b => Wanted(b.BookingId)).ToList();

        var boxes = await booked.BoxesByIdAsync(billed.Select(b => b.Box).Distinct().ToList(), ct);
        var tenant = await TenantMapAsync(db, StorageActivityKey, ct);
        string? Column(string code) => (tenant.GetValueOrDefault(code) ?? StorageActivityCodes.GetValueOrDefault(code)) switch
        {
            "EMPTY_STORAGE" or "FULL_STORAGE" => "STORAGE",
            "LOLO" or "LOLO_EMPTY" or "LOLO_LADEN" => "LOLO",
            _ => null,
        };
        var customers = headers.Values.ToDictionary(h => h.BookingId, h => h.CustomerCode);

        DateOnly? Day(DateTimeOffset? at) => at is { } a ? c.Branch.LocalDate(a) : null;
        var lines = billed
            .Where(b => boxes.ContainsKey(b.Box))
            .GroupBy(b => b.Box)
            .Select(g =>
            {
                var box = boxes[g.Key];
                var h = headers[g.First().BookingId!.Value];
                return (Box: box, Order: h.OrderNo, h.CarrierRef, First: g.Min(x => x.BilledNo)!, Customer: customers.GetValueOrDefault(box.BookingId),
                    Storage: g.Where(x => Column(x.ChargeCode) == "STORAGE").Sum(x => x.Amount),
                    Lolo: g.Where(x => Column(x.ChargeCode) == "LOLO").Sum(x => x.Amount));
            })
            .OrderBy(x => x.Order, StringComparer.Ordinal).ThenBy(x => x.CarrierRef, StringComparer.Ordinal).ThenBy(x => x.First, StringComparer.Ordinal)
            .ToList();

        var rows = lines.Select(x => new TabularRow([
            x.Box.ContainerNo, x.Box.EquipmentTypeCode, Day(x.Box.EmptyIn), Day(x.Box.EmptyOut), Day(x.Box.LadenIn), Day(x.Box.LadenOut),
            null, x.Storage, x.Lolo, null, null, null, x.Box.OrderTypeCode, x.Customer])).ToList();

        var first = lines.FirstOrDefault().Box;
        var vesselName = first?.VesselCode is { } code
            ? (await master.VesselsAsync([code], ct)).GetValueOrDefault(code)?.VesselName ?? code
            : "";

        return new TabularReport(
            FileName: $"ContainerStorageActivityByVslVoy_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 0.5,
            Heading:
            [
                new(c.BranchName, 10, Bold: true),
                new("CONTAINER STORAGE ACTIVITY", 10, Bold: true),
                new($"VESSEL/VOY: {vesselName} {first?.Voyage}"),
            ],
            HeadingRight:
            [
                new($"Printed By: {c.PrintedBy}"),
                new($"Printed On: {c.PrintedOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}"),
            ],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(2.6), new(1.4, null, CellAlign.Center), new(1.6, "dd/MM/yy", CellAlign.Center), new(1.6, "dd/MM/yy", CellAlign.Center),
                new(1.6, "dd/MM/yy", CellAlign.Center), new(1.6, "dd/MM/yy", CellAlign.Center), new(1.2),
                new(2.2, "0.00;(0.00);\"-\"", CellAlign.Right), new(2.0, "0.00;(0.00)", CellAlign.Right),
                new(1.6), new(2.2), new(1.2), new(2.4), new(2.6),
            ],
            HeaderRows:
            [
                [new("CONT NO."), new("SIZE"), new("EMPTY\nIN"), new("EMPTY\nOUT"), new("LADEN\nIN"), new("LADEN\nOUT"), new("DAY"),
                 new("STORAGE\nAMOUNT"), new("LO/LO"), new("ค่าผ่าน\nประตู"), new("HAULAGE\nCD/ECT/PAT"), new("FAS"), new("STATUS"), new("SHIPPER")],
            ],
            Rows: rows);
    }

    public const string ExportFullOutKey = "EXPORT_FULL_OUT";

    /// <summary>TMS.Accounting.ExportFullOut's code: the laden lift-on (the RDL's SL002-CR on FULL OUT).</summary>
    public static readonly IReadOnlyDictionary<string, string> ExportFullOutCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SL002-CR"] = "LIFT_ON",
    };

    /// <summary>The RDL's two order types: CY/CY and CFS export.</summary>
    private const string ExportCy = "EXP CY/CY", ExportCfs = "EXP CFS";

    /// <summary>
    /// TMS.Accounting.ExportFullOut (Report.usp_Accounting_ExportFullOut) — EXPORT FULL OUT (CY/CY): a row per invoice of
    /// laden lift-on on EXPORT bookings' full gate-outs, its vessel &amp; voyage and date, the boxes counted by size for
    /// CY+CFS, then count and amount by size for CY and for CFS, the TOTAL and the 80 % REFUND; then the grand total.
    /// Owner 2026-10-10 defaults: an invoice is the receipt (cash) or invoice (credit) that billed it, by its date;
    /// totals corrected — each line counted under its own size and order type at its own amount (the RDL put the whole
    /// invoice under its first line's size at its rate, and its 20' CY total tested "EXP CY", so always printed 0).
    /// </summary>
    public static async Task<TabularReport> ExportFullOutAsync(
        RevenueDbContext db, ITosBookedBoxes booked, IMasterDataReferences master, AccountingReportContext c,
        string? lineCode, string? vesselCode, string? voyage, CancellationToken ct)
    {
        var tenant = await TenantMapAsync(db, ExportFullOutKey, ct);
        bool LiftOn(string code) => (tenant.GetValueOrDefault(code) ?? ExportFullOutCodes.GetValueOrDefault(code)) == "LIFT_ON";
        var billed = (await BilledAsync(db, c, ct))
            .Where(b => LiftOn(b.ChargeCode) && string.Equals(b.MovementCode, "FULL_OUT", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var boxes = await booked.BoxesByIdAsync(billed.Select(b => b.Box).Distinct().ToList(), ct);
        bool Wanted(TosBookedBox b) => b.BookingTypeCode == "EXPORT"
            && (lineCode is null || string.Equals(b.LineCode, lineCode, StringComparison.OrdinalIgnoreCase))
            && (vesselCode is null || string.Equals(b.VesselCode, vesselCode, StringComparison.OrdinalIgnoreCase))
            && (voyage is null || string.Equals(b.Voyage, voyage, StringComparison.OrdinalIgnoreCase));
        var lines = billed.Where(b => boxes.TryGetValue(b.Box, out var box) && Wanted(box))
            .Select(b => (Billed: b, Box: boxes[b.Box])).ToList();

        var typeCodes = lines.Select(x => x.Box.EquipmentTypeCode).Distinct().ToList();
        var types = typeCodes.Count == 0 ? new Dictionary<string, EquipmentTypeRef>() : await master.EquipmentTypesAsync(typeCodes, ct);
        var vesselCodes = lines.Select(x => x.Box.VesselCode).OfType<string>().Distinct().ToList();
        var vessels = vesselCodes.Count == 0 ? new Dictionary<string, VesselRef>() : await master.VesselsAsync(vesselCodes, ct);

        string[] sizes = ["20", "40", "45"];
        // Per invoice: [CY+CFS 20/40/45 counts] [CY 20 n, amt, 40 n, amt, 45 n, amt] [CFS the same] TOTAL REFUND.
        decimal[] Figures(IEnumerable<(BilledCharge Billed, TosBookedBox Box)> set)
        {
            var list = set.Select(x => (Size: SizeType(types, x.Box.EquipmentTypeCode).Size, x.Box.OrderTypeCode, x.Billed.Amount)).ToList();
            IEnumerable<decimal> Of(string orderType) => sizes.SelectMany(size =>
            {
                var hit = list.Where(x => x.Size == size && x.OrderTypeCode == orderType).ToList();
                return new[] { (decimal)hit.Count, hit.Sum(x => x.Amount) };
            });
            var both = sizes.Select(size => (decimal)list.Count(x => x.Size == size && x.OrderTypeCode is ExportCy or ExportCfs));
            var cy = Of(ExportCy).ToArray();
            var cfs = Of(ExportCfs).ToArray();
            var total = cy.Where((_, i) => i % 2 == 1).Sum() + cfs.Where((_, i) => i % 2 == 1).Sum();
            return [.. both, .. cy, .. cfs, total, total * 0.8m];
        }

        var invoices = lines.GroupBy(x => x.Billed.BilledNo).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var rows = invoices.Select(g =>
        {
            var box = g.First().Box;
            var vessel = box.VesselCode is { } code ? vessels.GetValueOrDefault(code)?.VesselName ?? code : "";
            return new TabularRow([g.Key, $"{vessel} {box.Voyage}".Trim(), c.Branch.LocalDate(g.Min(x => x.Billed.BilledAt)),
                .. Figures(g).Select(v => (object?)v)]);
        }).ToList();
        rows.Add(new TabularRow([null, null, null, .. Figures(lines).Select(v => (object?)v)], RowKind.Total));

        const string Count = "#,0;(#,0)";
        const string Baht = "#,0;(#,0)";
        const string Thb = "#,0.00;(#,0.00);\"-\"";
        TabularColumn N() => new(1.0, Count, CellAlign.Center);
        TabularColumn B() => new(1.5, Baht, CellAlign.Right);

        return new TabularReport(
            FileName: $"ExportFullOut_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 0.5,
            Heading: [new(c.BranchName, 10, Bold: true)],
            HeadingRight: [],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(2.6), new(3.2), new(1.6, "dd/MM/yy", CellAlign.Center), N(), N(), N(),
                N(), B(), N(), B(), N(), B(), N(), B(), N(), B(), N(), B(),
                new(2.2, Thb, CellAlign.Right), new(2.2, Thb, CellAlign.Right),
            ],
            HeaderRows:
            [
                [new("", ColSpan: 3), new("CY+CFS", ColSpan: 3), new("CY", ColSpan: 6), new("CFS", ColSpan: 6), new("TOTAL"), new("REFUND")],
                [new("Invoice No"), new("VESSEL & VOY"), new("DATE"), new("20'"), new("40'"), new("45'"),
                 new("20'"), new("B 500"), new("40'"), new("B 500"), new("45'"), new("B 1000"),
                 new("20'"), new("B 400"), new("40'"), new("B 800"), new("45'"), new("B 800"),
                 new("THB"), new("THB(80%)")],
            ],
            Rows: rows);
    }

    public const string MonitoringKey = "MONITORING";

    /// <summary>TMS.Accounting.MonitoringDay's code: Vector's system setting MONITORING CHARGE (CREDIT), SM001-CR (KORAKIT's too).</summary>
    public static readonly IReadOnlyDictionary<string, string> MonitoringCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SM001-CR"] = "MONITORING",
    };

    /// <summary>
    /// TMS.Accounting.MonitoringDay (Report.usp_Accounting_MonitoringDay) — MONITORING CHARGES FOR REEFER CONTAINER: a line
    /// per monitoring charge on a reefer box that has come in laden, with its plug-on dates (the box's laden gate-in and
    /// gate-out, as Vector's LoadedIn/OutDate), days and amount under 20' or 40' (any size not 20'), total and order type;
    /// then the total row (no label). No date: all of the depot's history that matches the filters, as the RDL.
    /// Owner 2026-10-10 defaults: totals corrected — the 20' and 40' amounts are their own sums (the RDL printed the first
    /// row's amount, or the grand sum or 0 by the first row's size); cancelled charges and bookings left out.
    /// </summary>
    public static async Task<TabularReport> MonitoringDayAsync(
        RevenueDbContext db, ITosBookingHeaders tos, ITosBookedBoxes booked, IMasterDataReferences master, AccountingReportContext c,
        string? lineCode, string? bookingType, string? vesselCode, string? voyage, CancellationToken ct)
    {
        var tenant = await TenantMapAsync(db, MonitoringKey, ct);
        var codes = MonitoringCodes.Keys.Concat(tenant.Where(m => m.Value == "MONITORING").Select(m => m.Key))
            .Where(code => (tenant.GetValueOrDefault(code) ?? MonitoringCodes.GetValueOrDefault(code)) == "MONITORING")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var charges = await db.Charges.AsNoTracking()
            .Where(x => x.BranchId == c.Branch.BranchId && x.BookingContainerId != null && x.Status != "CANCELLED" && codes.Contains(x.ChargeCode))
            .OrderBy(x => x.CreatedAt)
            .Select(x => new { Box = x.BookingContainerId!.Value, Days = x.ChargeableQuantity ?? x.Quantity, x.Amount })
            .ToListAsync(ct);

        var boxes = await booked.BoxesByIdAsync(charges.Select(x => x.Box).Distinct().ToList(), ct);
        var headers = await tos.HeadersAsync(boxes.Values.Select(b => b.BookingId).Distinct().ToList(), ct);
        var typeCodes = boxes.Values.Select(b => b.EquipmentTypeCode).Distinct().ToList();
        var types = typeCodes.Count == 0 ? new Dictionary<string, EquipmentTypeRef>() : await master.EquipmentTypesAsync(typeCodes, ct);
        bool Wanted(TosBookedBox b) => b.LadenIn is not null && (types.GetValueOrDefault(b.EquipmentTypeCode)?.IsReefer ?? false)
            && headers.TryGetValue(b.BookingId, out var h) && h.Status != "CANCELLED"
            && (lineCode is null || string.Equals(b.LineCode, lineCode, StringComparison.OrdinalIgnoreCase))
            && (bookingType is null || string.Equals(b.BookingTypeCode, bookingType, StringComparison.OrdinalIgnoreCase))
            && (vesselCode is null || string.Equals(b.VesselCode, vesselCode, StringComparison.OrdinalIgnoreCase))
            && (voyage is null || string.Equals(b.Voyage, voyage, StringComparison.OrdinalIgnoreCase));

        var lines = charges.Where(x => boxes.TryGetValue(x.Box, out var b) && Wanted(b))
            .Select(x =>
            {
                var box = boxes[x.Box];
                var (size, type) = SizeType(types, box.EquipmentTypeCode);
                return (Box: box, Label: size is null ? box.EquipmentTypeCode : $"{size}'{type}", Twenty: size == "20", x.Days, x.Amount);
            })
            .ToList();

        DateOnly? Day(DateTimeOffset? at) => at is { } a ? c.Branch.LocalDate(a) : null;
        var rows = lines.Select((x, n) => new TabularRow([
            n + 1, x.Box.ContainerNo, x.Label, Day(x.Box.LadenIn), Day(x.Box.LadenOut),
            x.Twenty ? x.Days : 0m, x.Twenty ? x.Amount : 0m, x.Twenty ? 0m : x.Days, x.Twenty ? 0m : x.Amount, x.Amount, x.Box.OrderTypeCode])).ToList();
        rows.Add(new TabularRow([
            null, null, null, null, null,
            lines.Where(x => x.Twenty).Sum(x => x.Days), lines.Where(x => x.Twenty).Sum(x => x.Amount),
            lines.Where(x => !x.Twenty).Sum(x => x.Days), lines.Where(x => !x.Twenty).Sum(x => x.Amount),
            lines.Sum(x => x.Amount), null], RowKind.Total));

        const string Days = "#,0;(#,0);\"-\"";
        return new TabularReport(
            FileName: $"MonitoringDay_{c.Branch.BranchCode}_{c.PrintedOn:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 1.0,
            Heading:
            [
                new(c.BranchName, 10, Bold: true),
                new("MONITORING CHARGES FOR REEFER CONTAINER", 10, Bold: true),
                new($"VESSEL & VOY   {vesselCode}   {voyage}"),
            ],
            HeadingRight:
            [
                new($"Printed By: {c.PrintedBy}"),
                new($"Printed On: {c.PrintedOn.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}"),
            ],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(1.2, null, CellAlign.Center), new(3.0), new(1.8, null, CellAlign.Center),
                new(2.0, "dd/MM/yy", CellAlign.Center), new(2.0, "dd/MM/yy", CellAlign.Center),
                new(1.6, Days, CellAlign.Right), new(2.4, Money, CellAlign.Right), new(1.6, Days, CellAlign.Right), new(2.4, Money, CellAlign.Right),
                new(2.6, Money, CellAlign.Right), new(3.0),
            ],
            HeaderRows:
            [
                [new(""), new(""), new(""), new("MORNITERING", ColSpan: 2), new("NO.OF"), new(""), new("NO.OF"), new(""), new(""), new("")],
                [new("ITEM"), new("CONTAINER NO."), new("SIZE/TYPE"), new("DATE"), new("DATE"), new("DAY"), new("AMOUNT"), new("DAY"),
                 new("AMOUNT"), new("TOTAL"), new("REMARK")],
                [new(""), new(""), new(""), new("PLUG ON"), new("PLUG ON"), new("20'"), new(""), new("40'"), new(""), new(""), new("")],
            ],
            Rows: rows);
    }

    /// <summary>
    /// TMS.Accounting.GateOOCL (Report.usp_Accounting_GateOOCL) — OOCL GATE CHARGE LIST: a line per container the agent moved
    /// through the gate in the window — its import gate in/out, unstuffing, order type, empty in/out with storage days and
    /// amount, export full in/out with storage days and amount, and the gate (LO/LO) charged to the line; then the count
    /// by order type and size, 20' at the RDL's flat 240.
    /// Owner 2026-10-10 defaults: the moves are read by booking type and direction (IMPORT full in/out, any empty in, an
    /// EXPORT or REPO empty out, EXPORT full in/out) rather than Vector's order-type list, which KORAKIT's types never
    /// matched; storage and LO/LO by the Container Storage Activity codes and mapping, at their billed amounts (no 30-day
    /// re-cut from the 20th); Un Stuffing blank (no unstuffing records, D9 — and the RDL only ever filled one debug box);
    /// the summary counts containers, not rows.
    /// </summary>
    public static async Task<TabularReport> GateOoclAsync(
        RevenueDbContext db, ITosGateMoves gate, IMasterDataReferences master, AccountingReportContext c, string? lineCode, CancellationToken ct)
    {
        var moves = await gate.MovesAsync(c.Branch.BranchId, c.Start, c.End, new TosGateMoveFilter(LineCode: lineCode), ct);
        var typeCodes = moves.Select(m => m.EquipmentTypeCode).OfType<string>().Distinct().ToList();
        var types = typeCodes.Count == 0 ? new Dictionary<string, EquipmentTypeRef>() : await master.EquipmentTypesAsync(typeCodes, ct);

        var ids = moves.Select(m => m.BookingContainerId).Distinct().ToList();
        var tenant = await TenantMapAsync(db, StorageActivityKey, ct);
        string? Column(string code) => (tenant.GetValueOrDefault(code) ?? StorageActivityCodes.GetValueOrDefault(code)) switch
        {
            "LOLO" or "LOLO_EMPTY" or "LOLO_LADEN" => "LOLO",
            var other => other,
        };
        var charges = new List<(Guid Box, string? Column, string BillTo, decimal Days, decimal Amount)>();
        foreach (var slice in ids.Chunk(2000))
            charges.AddRange((await db.Charges.AsNoTracking()
                    .Where(x => x.BookingContainerId != null && slice.Contains(x.BookingContainerId.Value) && x.Status != "CANCELLED")
                    .Select(x => new { Box = x.BookingContainerId!.Value, x.ChargeCode, x.BillTo, Days = x.ChargeableQuantity ?? x.Quantity, x.Amount })
                    .ToListAsync(ct))
                .Select(x => (x.Box, Column(x.ChargeCode), x.BillTo, x.Days, x.Amount)));
        var byBox = charges.Where(x => x.Column is not null).ToLookup(x => x.Box);

        DateOnly? Day(DateTimeOffset? at) => at is { } a ? c.Branch.LocalDate(a) : null;
        var lines = moves
            .GroupBy(m => m.ContainerNo)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var all = g.OrderBy(m => m.TransactionAt).ToList();
                DateTimeOffset? First(Func<TosGateMove, bool> which) => all.Where(which).Select(m => (DateTimeOffset?)m.TransactionAt).FirstOrDefault();
                DateTimeOffset? Last(Func<TosGateMove, bool> which) => all.Where(which).Select(m => (DateTimeOffset?)m.TransactionAt).LastOrDefault();
                var mine = g.Select(m => m.BookingContainerId).Distinct().SelectMany(b => byBox[b]).ToList();
                decimal Days(string column) => mine.Where(x => x.Column == column).Sum(x => x.Days);
                decimal Amount(string column) => mine.Where(x => x.Column == column).Sum(x => x.Amount);
                var (size, type) = SizeType(types, all[0].EquipmentTypeCode);
                return (ContainerNo: g.Key, Size: size ?? "", Type: type ?? "", all[0].OrderTypeCode,
                    GateIn: First(m => m.BookingTypeCode == "IMPORT" && m.Direction == "IN" && m.FullEmpty == "FULL"),
                    GateOut: Last(m => m.BookingTypeCode == "IMPORT" && m.Direction == "OUT" && m.FullEmpty == "FULL"),
                    EmptyIn: First(m => m.Direction == "IN" && m.FullEmpty == "EMPTY"),
                    EmptyOut: Last(m => m.BookingTypeCode is "EXPORT" or "REPO" && m.Direction == "OUT" && m.FullEmpty == "EMPTY"),
                    EmptyDays: Days("EMPTY_STORAGE"), EmptyStorage: Amount("EMPTY_STORAGE"),
                    FullIn: First(m => m.BookingTypeCode == "EXPORT" && m.Direction == "IN" && m.FullEmpty == "FULL"),
                    FullOut: Last(m => m.BookingTypeCode == "EXPORT" && m.Direction == "OUT" && m.FullEmpty == "FULL"),
                    FullDays: Days("FULL_STORAGE"), FullStorage: Amount("FULL_STORAGE"),
                    Gate: mine.Where(x => x.Column == "LOLO" && x.BillTo != "CUSTOMER").Sum(x => x.Amount));
            })
            .ToList();

        var rows = lines.Select((x, n) => new TabularRow([
            n + 1, x.ContainerNo, x.Size, x.Type, Day(x.GateIn), Day(x.GateOut), null, x.OrderTypeCode, Day(x.EmptyIn), Day(x.EmptyOut),
            x.EmptyDays, x.EmptyStorage, Day(x.FullIn), Day(x.FullOut), x.FullDays, x.FullStorage, x.Gate])).ToList();

        var summary = lines.GroupBy(x => (x.OrderTypeCode, x.Size))
            .OrderBy(g => g.Key.OrderTypeCode, StringComparer.Ordinal).ThenBy(g => g.Key.Size, StringComparer.Ordinal)
            .Select(g => new TabularRow([$"{g.Key.OrderTypeCode} {g.Key.Size}", g.Count(), g.Key.Size == "20" ? g.Count() * 240m : 0m]))
            .ToList();

        const string Date = "dd-MM-yyyy";
        const string Days = "#,0;(#,0);\"\"";
        const string Amount = "#,0.00;(#,0.00);\"\"";
        return new TabularReport(
            FileName: $"GateOOCL_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 1.0,
            Heading: [new(c.BranchName, 10, Bold: true), new("OOCL GATE CHARGE LIST", 10, Bold: true)],
            HeadingRight: [],
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(0.9, null, CellAlign.Center), new(2.6), new(1.0, null, CellAlign.Center), new(1.0, null, CellAlign.Center),
                new(1.9, Date, CellAlign.Center), new(1.9, Date, CellAlign.Center), new(1.9, Date, CellAlign.Center), new(2.4),
                new(1.9, Date, CellAlign.Center), new(1.9, Date, CellAlign.Center), new(1.1, Days, CellAlign.Right), new(1.8, Amount, CellAlign.Right),
                new(1.9, Date, CellAlign.Center), new(1.9, Date, CellAlign.Center), new(1.1, Days, CellAlign.Right), new(1.8, Amount, CellAlign.Right),
                new(1.6, "#,0.00;(#,0.00)", CellAlign.Right),
            ],
            HeaderRows:
            [
                [new(""), new("Container No"), new("Size"), new("Type"), new("Gate In"), new("Gate Out"), new("Un Stuffing"), new("Order Type"),
                 new("Empty In"), new("Empty Out"), new("Days"), new("Storage"), new("Full In"), new("Full Out"), new("Days"), new("Storage"), new("Gate")],
            ],
            Rows: rows,
            After:
            [
                new TabularBlock(null,
                    [new(3.6), new(2.2, null, CellAlign.Right), new(2.2, "#,0", CellAlign.Right)],
                    [[new(lines.FirstOrDefault().OrderTypeCode ?? ""), new("Container No"), new("")]],
                    summary),
            ]);
    }
}
