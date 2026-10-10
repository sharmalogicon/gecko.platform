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
}
