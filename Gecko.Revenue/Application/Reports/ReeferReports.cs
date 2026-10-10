using System.Globalization;
using Gecko.Data.Documents;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Tos.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Application.Reports;

/// <summary>
/// Vector's reefer electricity reports (TMS.Accounting.ElectricDay and its ELEC / PRE-COOL / PTI cuts): reefer boxes
/// moved through the gate in the window, with their PTI, pre-cool and electricity charges. Owner 2026-10-10 defaults:
/// plug on/off are the box's laden gate-in and gate-out, as Vector's LoadedIn/OutDate, and NO.OF DAY counts their
/// calendar days inclusive; PTI-1/2/3 dates print blank (Gecko has no work orders, gap D8); amounts are the charges'
/// own (no whole-baht rounding, no recomputed rate × days, no rows doubled by old gate-ins); TOTAL is PTI + pre-cool +
/// electricity; columns by charge code — the RDL's credit codes plus the tenant's in billing.report_charge_column.
/// </summary>
internal static class ReeferReports
{
    private const string Money = "#,0.00;(#,0.00);\"-\"";
    private const string Count = "#,0;(#,0);\"-\"";

    public const string ElectricityKey = "ELECTRICITY";

    /// <summary>The RDLs' codes: SE003-CR PTI, SE002-CR pre-cool, SE004-CR electricity.</summary>
    public static readonly IReadOnlyDictionary<string, string> ElectricityCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SE003-CR"] = "PTI", ["SE002-CR"] = "PRECOOL", ["SE004-CR"] = "ELECTRICITY",
    };

    /// <summary>Which gate moves bring a box into the report.</summary>
    public enum Driver
    {
        /// <summary>ElectricDay / PTI: an empty or full gate-out in the window.</summary>
        GateOut,
        /// <summary>ELEC: a full gate-out in the window, of a box that also came in full.</summary>
        FullOut,
        /// <summary>PRE-COOL: an EXPORT box's empty gate-out, any other box's full gate-in, in the window.</summary>
        PreCool,
    }

    /// <param name="BookingType">Gecko's booking type code; null = every type (the RDLs' blank).</param>
    public sealed record Filter(string? LineCode, string? VesselCode, string? Voyage, string? BookingType, string? OrderType);

    /// <summary>One printed line: a booked box and its charges by column.</summary>
    public sealed record ReeferLine(
        TosBookedBox Box, string Label, string Size, string Type, string BookingType, string? VesselName,
        Charges Pti, Charges PreCool, Charges Electricity, int LoadedDays);

    /// <param name="Rate">The highest unit rate charged (the matrices' SELLING RATE).</param>
    public sealed record Charges(int Lines, decimal Rate, decimal Amount);

    public static async Task<List<ReeferLine>> LinesAsync(
        RevenueDbContext db, ITosGateMoves gate, ITosBookedBoxes booked, ITosBookingHeaders tos, IMasterDataReferences master,
        AccountingReportContext c, Driver driver, Filter f, CancellationToken ct)
    {
        var moves = (await gate.MovesAsync(c.Branch.BranchId, c.Start, c.End, new TosGateMoveFilter(), ct))
            .Where(m => driver switch
            {
                Driver.GateOut => m.Direction == "OUT",
                Driver.FullOut => m.Direction == "OUT" && m.FullEmpty == "FULL",
                _ => m.BookingTypeCode == "EXPORT" ? m.Direction == "OUT" && m.FullEmpty == "EMPTY" : m.Direction == "IN" && m.FullEmpty == "FULL",
            })
            .ToList();
        var boxes = await booked.BoxesByIdAsync(moves.Select(m => m.BookingContainerId).Distinct().ToList(), ct);
        var headers = await tos.HeadersAsync(boxes.Values.Select(b => b.BookingId).Distinct().ToList(), ct);
        var typeCodes = boxes.Values.Select(b => b.EquipmentTypeCode).Distinct().ToList();
        var types = typeCodes.Count == 0 ? new Dictionary<string, EquipmentTypeRef>() : await master.EquipmentTypesAsync(typeCodes, ct);

        bool Wanted(TosBookedBox b) => (types.GetValueOrDefault(b.EquipmentTypeCode)?.IsReefer ?? false)
            && headers.TryGetValue(b.BookingId, out var h) && h.Status != "CANCELLED"
            && (driver != Driver.FullOut || (b.LadenIn is not null && b.LadenOut is not null))
            && (f.LineCode is null || string.Equals(b.LineCode, f.LineCode, StringComparison.OrdinalIgnoreCase))
            && (f.VesselCode is null || string.Equals(b.VesselCode, f.VesselCode, StringComparison.OrdinalIgnoreCase))
            && (f.Voyage is null || string.Equals(b.Voyage, f.Voyage, StringComparison.OrdinalIgnoreCase))
            && (f.BookingType is null || string.Equals(b.BookingTypeCode, f.BookingType, StringComparison.OrdinalIgnoreCase))
            && (f.OrderType is null || string.Equals(b.OrderTypeCode, f.OrderType, StringComparison.OrdinalIgnoreCase));
        var wanted = boxes.Values.Where(Wanted).ToList();

        var tenant = await OperationChargeReports.TenantMapAsync(db, ElectricityKey, ct);
        string? Column(string code) => tenant.GetValueOrDefault(code) ?? ElectricityCodes.GetValueOrDefault(code);
        var ids = wanted.Select(b => b.BookingContainerId).ToList();
        var charges = new List<(Guid Box, string? Column, decimal Rate, decimal Amount)>();
        foreach (var slice in ids.Chunk(2000))
            charges.AddRange((await db.Charges.AsNoTracking()
                    .Where(x => x.BookingContainerId != null && slice.Contains(x.BookingContainerId.Value) && x.Status != "CANCELLED")
                    .Select(x => new { Box = x.BookingContainerId!.Value, x.ChargeCode, x.UnitRate, x.Amount })
                    .ToListAsync(ct))
                .Select(x => (x.Box, Column(x.ChargeCode), x.UnitRate ?? 0m, x.Amount)));
        var byBox = charges.Where(x => x.Column is not null).ToLookup(x => x.Box);

        var vesselCodes = wanted.Select(b => b.VesselCode).OfType<string>().Distinct().ToList();
        var vessels = vesselCodes.Count == 0 ? new Dictionary<string, VesselRef>() : await master.VesselsAsync(vesselCodes, ct);

        return wanted
            .Select(b =>
            {
                var mine = byBox[b.BookingContainerId].ToList();
                Charges Of(string column)
                {
                    var hit = mine.Where(x => x.Column == column).ToList();
                    return new Charges(hit.Count, hit.Select(x => x.Rate).DefaultIfEmpty().Max(), hit.Sum(x => x.Amount));
                }
                var (size, type) = OperationChargeReports.SizeType(types, b.EquipmentTypeCode);
                var days = b.LadenIn is { } on && b.LadenOut is { } off
                    ? c.Branch.LocalDate(off).DayNumber - c.Branch.LocalDate(on).DayNumber + 1 : 0;
                return new ReeferLine(b, $"{size}{type}", size ?? "", type ?? "", b.BookingTypeCode,
                    b.VesselCode is { } v ? vessels.GetValueOrDefault(v)?.VesselName ?? v : null,
                    Of("PTI"), Of("PRECOOL"), Of("ELECTRICITY"), days);
            })
            .OrderBy(x => x.Size, StringComparer.Ordinal).ThenBy(x => x.Type, StringComparer.Ordinal)
            .ThenBy(x => x.Box.ContainerNo, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The page header the four ElectricDay RDLs share; PRE-COOL prints the vessel's name, the others its code.</summary>
    private static (IReadOnlyList<ReportText> Left, IReadOnlyList<ReportText> Right) Header(
        AccountingReportContext c, IReadOnlyList<ReeferLine> lines, bool vesselName)
    {
        var first = lines.FirstOrDefault();
        var vessel = vesselName ? first?.VesselName : first?.Box.VesselCode;
        var eta = first?.Box.Eta is { } at ? c.Branch.LocalDate(at).ToString("dd/MM/yy", CultureInfo.InvariantCulture) : "";
        return (
            [
                new(c.BranchName, 10, Bold: true),
                new("ELECTRICITY CHARGES FOR REEFER CONTAINER", 10, Bold: true),
                new($"VESSEL & VOY : {vessel}  {first?.Box.Voyage}"),
                new($"ETD : {eta}"),
                new($"CARRIER : {first?.Box.LineCode}"),
            ],
            [
                new($"Printed By: {c.PrintedBy}"),
                new($"Printed On: {c.PrintedOn.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}"),
                new(first?.BookingType ?? ""),
            ]);
    }

    /// <summary>A per-size summary under the list: SELLING RATE / DAY / AMOUNT / TOTAL by size/type.</summary>
    private static TabularBlock Matrix(
        string title, IReadOnlyList<ReeferLine> lines, Func<ReeferLine, Charges> pick, Func<ReeferLine, decimal> days)
    {
        var sizes = lines.Where(x => pick(x).Lines > 0).GroupBy(x => x.Label).OrderBy(g => g.First().Size, StringComparer.Ordinal)
            .ThenBy(g => g.First().Type, StringComparer.Ordinal).ToList();
        object?[] Row(string label, Func<IGrouping<string, ReeferLine>, object?> value) => [label, .. sizes.Select(value)];
        var amount = (Func<IGrouping<string, ReeferLine>, object?>)(g => g.Sum(x => pick(x).Amount));
        // A column per size/type present; one blank column when there is none, so the heading still has a place.
        IReadOnlyList<HeaderCell> labels = sizes.Count == 0 ? [new("")] : [.. sizes.Select(g => new HeaderCell(g.Key))];
        return new TabularBlock(null,
            [new(3.0), .. labels.Select(_ => new TabularColumn(2.0, Money, CellAlign.Right))],
            [[new(""), new(title, ColSpan: labels.Count)], [new(""), .. labels]],
            sizes.Count == 0 ? [] :
            [
                new TabularRow(Row("SELLING RATE", g => g.Max(x => pick(x).Rate))),
                // A count, not money: written as text so the column's money format leaves it whole.
                new TabularRow(Row("DAY", g => g.Where(x => pick(x).Lines > 0).Sum(days).ToString("#,0", CultureInfo.InvariantCulture))),
                new TabularRow(Row("AMOUNT", amount)),
                new TabularRow(Row("TOTAL", amount), RowKind.Total),
            ]);
    }

    private static DateOnly? Day(AccountingReportContext c, DateTimeOffset? at) => at is { } a ? c.Branch.LocalDate(a) : null;

    /// <summary>
    /// TMS.Accounting.ElectricDay (Report.usp_Accounting_ElectricDay) — a line per reefer box gated out (empty or full) in
    /// the window: empty-in date, PTI dates and amount, pre-cool date (the box's required date, when pre-cool is charged)
    /// and amount, plug on/off with days and the electricity amount, the line's TOTAL and order type; the total row;
    /// then the PTI, PRECOOL and ELECTRICITY matrices by size/type.
    /// </summary>
    public static TabularReport ElectricDay(AccountingReportContext c, IReadOnlyList<ReeferLine> lines)
    {
        const string Date = "dd-MM-yyyy";
        var rows = lines.Select((x, n) => new TabularRow([
            n + 1, x.Box.ContainerNo, x.Label, Day(c, x.Box.EmptyIn), null, null, null, x.Pti.Amount,
            x.PreCool.Amount > 0 ? x.Box.RequiredDate : null, x.PreCool.Amount,
            Day(c, x.Box.LadenIn), Day(c, x.Box.LadenOut), x.LoadedDays, x.Electricity.Amount,
            x.Pti.Amount + x.PreCool.Amount + x.Electricity.Amount, x.Box.OrderTypeCode])).ToList();
        rows.Add(new TabularRow([
            null, null, null, null, null, null, null, lines.Sum(x => x.Pti.Amount), null, lines.Sum(x => x.PreCool.Amount),
            null, null, lines.Sum(x => x.LoadedDays), lines.Sum(x => x.Electricity.Amount),
            lines.Sum(x => x.Pti.Amount + x.PreCool.Amount + x.Electricity.Amount), null], RowKind.Total));

        var (left, right) = Header(c, lines, vesselName: false);
        return new TabularReport(
            FileName: $"ElectricDay_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 1.0,
            Heading: left,
            HeadingRight: right,
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(1.0, null, CellAlign.Center), new(2.6), new(1.4, null, CellAlign.Center), new(1.9, Date, CellAlign.Center),
                new(1.9, Date, CellAlign.Center), new(1.9, Date, CellAlign.Center), new(1.9, Date, CellAlign.Center), new(1.8, Money, CellAlign.Right),
                new(1.9, Date, CellAlign.Center), new(1.8, Money, CellAlign.Right), new(1.9, Date, CellAlign.Center), new(1.9, Date, CellAlign.Center),
                new(1.4, Count, CellAlign.Right), new(1.9, Money, CellAlign.Right), new(2.0, Money, CellAlign.Right), new(2.0),
            ],
            HeaderRows:
            [
                [new(""), new(""), new(""), new(""), new("PTI", ColSpan: 4), new("PRECOOL", ColSpan: 2), new("ELC", ColSpan: 4), new(""), new("")],
                [new("ITEM"), new("CONTAINER NO."), new("SIZE/TYPE"), new(""), new("DATE", ColSpan: 3), new("AMOUNT"), new("DATE"), new("AMOUNT"),
                 new("DATE"), new("DATE"), new("NO.OF DAY"), new("AMOUNT"), new("TOTAL"), new("REMARK")],
                [new(""), new(""), new(""), new("Empty In Date"), new("PTI-1"), new("PTI-2"), new("PTI-3"), new("Price"), new("PRECOOL"), new(""),
                 new("PLUG ON"), new("PLUG OFF"), new("Loaded Hrs"), new(""), new(""), new("")],
            ],
            Rows: rows,
            PageLabel: null,
            After:
            [
                Matrix("PTI", lines, x => x.Pti, _ => 1),
                Matrix("PRECOOL", lines, x => x.PreCool, _ => 1),
                Matrix("ELECTRICITY", lines, x => x.Electricity, x => x.LoadedDays),
            ]);
    }

    /// <summary>
    /// TMS.Accounting.ElectricDay_ELC (Report.usp_Accounting_Elec) — the electricity cut: a line per reefer box gated out
    /// full (that came in full) in the window, plug on/off, days and electricity amount, booking type and order type; the
    /// total row; then the ELECTRICITY matrix (its AMOUNT the charges', not the RDL's rate × days).
    /// </summary>
    public static TabularReport ElectricDayElc(AccountingReportContext c, IReadOnlyList<ReeferLine> lines)
    {
        const string Date = "dd/MM/yyyy";
        var rows = lines.Select((x, n) => new TabularRow([
            n + 1, x.Box.ContainerNo, x.Label, Day(c, x.Box.LadenIn), Day(c, x.Box.LadenOut), x.LoadedDays, x.Electricity.Amount,
            $"{x.BookingType} {x.Box.OrderTypeCode}"])).ToList();
        rows.Add(new TabularRow([null, null, null, null, null, lines.Sum(x => x.LoadedDays), lines.Sum(x => x.Electricity.Amount), null], RowKind.Total));

        var (left, right) = Header(c, lines, vesselName: false);
        return new TabularReport(
            FileName: $"ElectricDayELC_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 1.0,
            Heading: left,
            HeadingRight: right,
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(1.2, null, CellAlign.Center), new(3.0), new(1.8, null, CellAlign.Center), new(2.4, Date, CellAlign.Center),
                new(2.4, Date, CellAlign.Center), new(2.0, Count, CellAlign.Right), new(2.6, Money, CellAlign.Right), new(4.0),
            ],
            HeaderRows:
            [
                [new(""), new(""), new(""), new("ELC", ColSpan: 4), new("")],
                [new("ITEM"), new("CONTAINER NO."), new("SIZE/TYPE"), new("DATE"), new("DATE"), new("NO.OF DAY"), new("AMOUNT"), new("REMARK")],
                [new(""), new(""), new(""), new("PLUG ON"), new("PLUG OFF"), new("Loaded Hrs"), new(""), new("")],
            ],
            Rows: rows,
            PageLabel: null,
            After: [Matrix("ELECTRICITY", lines, x => x.Electricity, x => x.LoadedDays)]);
    }

    /// <summary>
    /// TMS.Accounting.ElectricDay_PRECOOL (Report.usp_Accounting_ElectricDay_PRECOOL) — the pre-cool cut: a line per
    /// reefer box charged pre-cool, moved in the window (an EXPORT box's empty gate-out, any other's full gate-in), its
    /// empty gate-out date and pre-cool amount, order type; the total; then the PRECOOL matrix (DAY = boxes charged).
    /// </summary>
    public static TabularReport ElectricDayPreCool(AccountingReportContext c, IReadOnlyList<ReeferLine> all)
    {
        const string Date = "dd/MM/yyyy";
        var lines = all.Where(x => x.PreCool.Amount > 0).ToList();
        var rows = lines.Select((x, n) => new TabularRow([
            n + 1, x.Box.ContainerNo, x.Label, Day(c, x.Box.EmptyOut), x.PreCool.Amount, x.Box.OrderTypeCode])).ToList();
        rows.Add(new TabularRow([null, null, null, null, lines.Sum(x => x.PreCool.Amount), null], RowKind.Total));

        var (left, right) = Header(c, lines, vesselName: true);
        return new TabularReport(
            FileName: $"ElectricDayPRECOOL_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 1.0,
            Heading: left,
            HeadingRight: right,
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(1.2, null, CellAlign.Center), new(3.0), new(1.8, null, CellAlign.Center), new(2.6, Date, CellAlign.Center),
                new(2.6, Money, CellAlign.Right), new(3.0),
            ],
            HeaderRows:
            [
                [new(""), new(""), new(""), new("PRECOOL", ColSpan: 2), new("")],
                [new("ITEM"), new("CONTAINER NO."), new("SIZE/TYPE"), new("DATE"), new("AMOUNT"), new("REMARK")],
                [new(""), new(""), new(""), new("PRECOOL"), new(""), new("")],
            ],
            Rows: rows,
            PageLabel: null,
            After: [Matrix("PRECOOL", lines, x => x.PreCool, _ => 1)]);
    }

    /// <summary>
    /// TMS.Accounting.ElectricDay_PTI (Report.usp_Accounting_ElectricDay) — the PTI cut: a line per reefer box gated out
    /// in the window, its PTI date (blank: no work orders) and PTI amount, order type; the total; then the PTI matrix.
    /// </summary>
    public static TabularReport ElectricDayPti(AccountingReportContext c, IReadOnlyList<ReeferLine> lines)
    {
        const string Date = "dd/MM/yyyy";
        var rows = lines.Select((x, n) => new TabularRow([
            n + 1, x.Box.ContainerNo, x.Label, null, x.Pti.Amount, x.Box.OrderTypeCode])).ToList();
        rows.Add(new TabularRow([null, null, null, null, lines.Sum(x => x.Pti.Amount), null], RowKind.Total));

        var (left, right) = Header(c, lines, vesselName: false);
        return new TabularReport(
            FileName: $"ElectricDayPTI_{c.Branch.BranchCode}_{c.From:yyyyMMdd}-{c.To:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 1.0,
            Heading: left,
            HeadingRight: right,
            Preamble: [],
            PreambleRight: [],
            Columns:
            [
                new(1.2, null, CellAlign.Center), new(3.0), new(1.8, null, CellAlign.Center), new(2.6, Date, CellAlign.Center),
                new(2.6, Money, CellAlign.Right), new(3.0),
            ],
            HeaderRows:
            [
                [new(""), new(""), new(""), new("PTI", ColSpan: 2), new("")],
                [new("ITEM"), new("CONTAINER NO."), new("SIZE/TYPE"), new("DATE"), new("AMOUNT"), new("REMARK")],
                [new(""), new(""), new(""), new("PTI"), new("Price"), new("")],
            ],
            Rows: rows,
            PageLabel: null,
            After: [Matrix("PTI", lines, x => x.Pti, _ => 1)]);
    }
}
