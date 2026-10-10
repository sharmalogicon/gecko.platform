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
}
