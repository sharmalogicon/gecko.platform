using System.Globalization;
using Gecko.Data.Documents;
using Gecko.Identity.Contracts;
using Gecko.MasterData.Contracts;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Gecko.Tos.Application;

/// <summary>
/// The truck-in form, laid out as KORAKIT's Vector report TMS_TruckInForm(KPS).rdl (owner 2026-10-08: the
/// columns and wording exactly the RDL's): an A4 portrait sheet per box on the truck — the "DAILY EQUIPMENT
/// DETAIL CONTACT YARD" header, then Agent / Shipper / Commodity, Booking No / Size/Type / Settemp / Vent /
/// Pre-Cool, the container and requested seal, Haulier / Plate / Time / Destination Port, Remark, and the
/// customer / survey / technician signature lines. Data is Report.usp_TruckInForm_KPS's, read from Gecko:
/// the boxes the visit gated plus the ones it is still here to collect.
///
/// Gecko additions the RDL has no rule against: a voided move still prints, under the VOID watermark.
/// Owner 2026-10-20: each sheet also says whether the truck drops the box off (ใบฝากตู้) or picks it up (ใบปล่อยตู้),
/// whether the customer pays cash at the gate (เงินสด) or is billed (เครดิต), and whether the box is FULL or EMPTY.
/// Owner 2026-10-22: the haulier gets room for its whole name; each sheet also prints the box's yard location (the
/// position keyed at the gate-in screen, or where the yard stock now has it), its weight, and the driver's name, phone
/// and a line for the driver to sign.
/// Times are the branch's clock; names are MDM's and Identity's at print time.
/// </summary>
internal sealed class TruckInDocument(TosDbContext db, IMasterDataReferences master, IUserDirectory users, BranchClock clock)
{
    public sealed record Rendered(string FileName, byte[] Pdf);

    /// <summary>One sheet of the form: a box line on the truck, every value as printed.</summary>
    internal sealed record Sheet(
        string AgentCode, string CustomerName, string CommodityDesc,
        string BookingBlNo, string SizeType, string SetTemp, string Vent, string PreCool,
        string ContainerNo, string SealNo1,
        string HaulierName, string TruckNo, string TimeIn, string DestinationPort,
        string Remarks, bool IsVoided,
        string SlipTitle = "", string PaymentTerm = "",
        string YardLocation = "", string Weight = "", string DriverName = "", string DriverMobile = "");

    /// <summary>The form's page header, the same on every sheet (the RDL's First() of the dataset).</summary>
    internal sealed record Header(string CompanyName, string CreatedBy, string DateTimeIn);

    public async Task<Rendered?> RenderAsync(Guid truckVisitId, CancellationToken ct)
    {
        var visit = await db.TruckVisits.AsNoTracking().SingleOrDefaultAsync(v => v.TruckVisitId == truckVisitId, ct);
        if (visit is null) return null;

        // Vector TruckMovementDt: every box line on the truck — what it gated, and what it is still here to collect.
        var gated = await (
            from g in db.GateTransactions.AsNoTracking().Where(x => x.TruckVisitId == truckVisitId)
            join bc in db.BookingContainers on g.BookingContainerId equals bc.BookingContainerId
            join r in db.EquipmentRequirements on bc.EquipmentRequirementId equals r.EquipmentRequirementId
            select new Line(g.TransactionAt, g.PositionNo, g.ContainerNo, g.Status == "VOIDED", g.BookingId, bc, r.EquipmentTypeCode,
                r.ReeferSetTempC, r.ReeferVentPct, g.Direction, g.FullEmpty,
                g.PositionText, g.GrossWeightKg, g.TareWeightKg, g.CargoWeightKg)).ToListAsync(ct);
        var collecting = await (
            from p in db.VisitPickups.AsNoTracking().Where(x => x.TruckVisitId == truckVisitId && x.Status == "PLANNED")
            join bc in db.BookingContainers on p.BookingContainerId equals bc.BookingContainerId
            join r in db.EquipmentRequirements on bc.EquipmentRequirementId equals r.EquipmentRequirementId
            select new Line(p.PlannedAt, byte.MaxValue, p.ContainerNo ?? bc.ContainerNo, false, bc.BookingId, bc, r.EquipmentTypeCode,
                r.ReeferSetTempC, r.ReeferVentPct, "OUT", null, null, null, null, null)).ToListAsync(ct);

        // A box still to collect is not gated yet: its full/empty is that of the next pick-up step planned for it.
        var collectIds = collecting.Select(l => l.Box.BookingContainerId).Distinct().ToList();
        var nextOut = collectIds.Count == 0 ? new Dictionary<Guid, string>() : (await db.MovementPlans.AsNoTracking()
                .Where(m => collectIds.Contains(m.BookingContainerId) && m.GateTransactionId == null && m.SkippedAt == null
                            && m.MovementCode.Contains("OUT"))
                .Select(m => new { m.BookingContainerId, m.SequenceNo, m.MovementCode }).ToListAsync(ct))
            .GroupBy(m => m.BookingContainerId)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.SequenceNo).First().MovementCode);
        collecting = collecting.Select(l => l with { FullEmpty = FullEmptyOf(nextOut.GetValueOrDefault(l.Box.BookingContainerId)) }).ToList();

        var lines = gated.Concat(collecting).OrderBy(l => l.At).ThenBy(l => l.PositionNo).ToList();

        // A box the truck takes out: where the yard stock has it now, else what its gate-in recorded — and its gate-in weights.
        var outBoxes = lines.Where(l => l.Direction != "IN" && l.ContainerNo is { Length: > 0 }).Select(l => l.ContainerNo!).Distinct().ToList();
        var inYard = outBoxes.Count == 0 ? new Dictionary<string, string?>() : (await db.VwContainerInYards.AsNoTracking()
                .Where(v => v.BranchId == visit.BranchId && outBoxes.Contains(v.ContainerNo))
                .Select(v => new { v.ContainerNo, v.PositionText }).ToListAsync(ct))
            .GroupBy(v => v.ContainerNo).ToDictionary(g => g.Key, g => g.First().PositionText);
        var lastIn = outBoxes.Count == 0 ? new Dictionary<string, GateIn>() : (await db.GateTransactions.AsNoTracking()
                .Where(g => g.BranchId == visit.BranchId && g.Direction == "IN" && g.Status == "COMPLETED" && outBoxes.Contains(g.ContainerNo!))
                .Select(g => new { g.ContainerNo, g.TransactionAt, g.PositionText, g.GrossWeightKg, g.TareWeightKg, g.CargoWeightKg })
                .ToListAsync(ct))
            .GroupBy(g => g.ContainerNo!).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.TransactionAt)
                .Select(x => new GateIn(x.PositionText, x.GrossWeightKg, x.TareWeightKg, x.CargoWeightKg)).First());

        var bookingIds = lines.Select(l => l.BookingId).Distinct().ToList();
        var bookings = await db.Bookings.AsNoTracking().Where(b => bookingIds.Contains(b.BookingId)).ToDictionaryAsync(b => b.BookingId, ct);
        var parties = await master.PartiesAsync(
            bookings.Values.Select(b => b.CustomerPartyCode).Append(visit.HaulierPartyCode).OfType<string>().Distinct(), ct);
        var paysCash = await PaysCashAsync(bookings.Values, ct);
        var typeCodes = lines.Select(l => l.EquipmentTypeCode).Distinct().ToList();
        var types = typeCodes.Count == 0 ? new Dictionary<string, EquipmentTypeRef>() : await master.EquipmentTypesAsync(typeCodes, ct);

        var branch = (await clock.BranchesAsync([visit.BranchId], ct)).GetValueOrDefault(visit.BranchId);
        var company = await master.InvoicingCompanyAsync(visit.BranchId, ct);
        var createdBy = visit.CreatedBy is { } by ? (await users.DisplayNamesAsync([by], ct)).GetValueOrDefault(by) : null;
        var timeIn = branch is null ? visit.GateInAt ?? visit.ArrivedAt : TimeZoneInfo.ConvertTime(visit.GateInAt ?? visit.ArrivedAt, branch.Zone);

        string? Name(string? code) => code is not null && parties.TryGetValue(code, out var p) ? p.Name : null;
        var sheets = lines.Select(l =>
        {
            var booking = bookings.GetValueOrDefault(l.BookingId);
            return new Sheet(
                AgentCode: booking?.AgentPartyCode ?? "",
                CustomerName: Name(booking?.CustomerPartyCode) ?? "",
                CommodityDesc: Commodity(l.FullEmpty, booking?.CommodityCode),
                BookingBlNo: booking?.CarrierRef ?? "",
                SizeType: SizeType(l.EquipmentTypeCode, types.GetValueOrDefault(l.EquipmentTypeCode)?.SizeCode),
                SetTemp: SetTemp(l.Box.ReeferSetTempC ?? l.SetTempC),
                Vent: Vent(l.Box.ReeferVentPct ?? l.VentPct),
                PreCool: l.Box.IsPreCool == true ? "YES" : "NO",
                ContainerNo: l.ContainerNo ?? "",
                SealNo1: l.Box.DeclaredSealNo ?? "",
                HaulierName: Name(visit.HaulierPartyCode) ?? "",
                TruckNo: visit.TruckPlate,
                TimeIn: TimeOfDay(timeIn),
                DestinationPort: booking?.FpdPortCode ?? "",
                Remarks: Remarks(booking?.Remarks, l.Box.Remarks),
                IsVoided: l.IsVoided,
                SlipTitle: SlipTitle(l.Direction),
                PaymentTerm: booking is null ? "" : PaymentTermText(paysCash.GetValueOrDefault(booking.BookingId)),
                YardLocation: YardLocationOf(l, inYard, lastIn),
                Weight: Kg(WeightOf(l.Gross, l.Tare, l.Cargo)
                           ?? (l.Direction != "IN" && l.ContainerNo is { } no && lastIn.TryGetValue(no, out var gin) ? WeightOf(gin.Gross, gin.Tare, gin.Cargo) : null)
                           ?? l.Box.DeclaredVgmKg),
                DriverName: visit.DriverName ?? "",
                DriverMobile: visit.DriverMobile ?? "");
        }).ToList();

        var header = new Header(
            company?.LegalNameEn ?? branch?.BranchCode ?? "",
            createdBy ?? "",
            timeIn.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture));
        return new Rendered($"{visit.VisitNo}-truck-in.pdf", Render(header, sheets));
    }

    private sealed record Line(
        DateTimeOffset At, byte PositionNo, string? ContainerNo, bool IsVoided, Guid BookingId,
        Infrastructure.Persistence.Entities.BookingContainer Box, string EquipmentTypeCode, decimal? SetTempC, decimal? VentPct,
        string Direction, string? FullEmpty,
        string? PositionText, decimal? Gross, decimal? Tare, decimal? Cargo);

    /// <summary>What a box's gate-in recorded: the position keyed at the gate-in screen and the weights.</summary>
    private sealed record GateIn(string? PositionText, decimal? Gross, decimal? Tare, decimal? Cargo);

    /// <summary>
    /// A drop-off prints the position its gate-in recorded. A pick-up prints where the yard stock has the box now (a yard
    /// move may have shifted it), else the position its own gate-out or its last gate-in recorded.
    /// </summary>
    private static string YardLocationOf(Line l, IReadOnlyDictionary<string, string?> inYard, IReadOnlyDictionary<string, GateIn> lastIn)
    {
        if (l.Direction == "IN" || l.ContainerNo is not { Length: > 0 } no) return l.PositionText ?? "";
        return (inYard.GetValueOrDefault(no) ?? l.PositionText ?? lastIn.GetValueOrDefault(no)?.PositionText) ?? "";
    }

    /// <summary>
    /// Whether each booking's customer pays cash at the gate, by the order type's payment rule (R2) as the cash quoter applies
    /// it: CASH_ONLY is cash; otherwise a customer whose default term is CREDIT is billed and everyone else pays cash.
    /// </summary>
    private async Task<Dictionary<Guid, bool>> PaysCashAsync(IEnumerable<Infrastructure.Persistence.Entities.Booking> bookings, CancellationToken ct)
    {
        var list = bookings.ToList();
        if (list.Count == 0) return [];
        var rules = await master.OrderTypePlansAsync(list.Select(b => b.OrderTypeCode).Distinct(), ct);
        var customers = list.Select(b => b.CustomerPartyCode).OfType<string>().Distinct().ToList();
        var credit = customers.Count == 0 ? new Dictionary<string, CustomerCreditRef>() : await master.CustomerCreditAsync(customers, ct);
        return list.ToDictionary(b => b.BookingId, b =>
            rules.GetValueOrDefault(b.OrderTypeCode)?.RulesOrNone.PaymentRule == OrderTypeRules.CashOnly
            || b.CustomerPartyCode is null
            || credit.GetValueOrDefault(b.CustomerPartyCode) is not { IsOnCredit: true });
    }

    // ── owner 2026-10-20 additions ──────────────────────────────────────────

    /// <summary>Drop-off: ใบฝากตู้; pick-up: ใบปล่อยตู้.</summary>
    internal static string SlipTitle(string direction) => direction switch
    {
        "IN" => "ใบฝากตู้",
        "OUT" => "ใบปล่อยตู้",
        _ => "",
    };

    /// <summary>Cash at the gate: เงินสด; billed on credit: เครดิต.</summary>
    internal static string PaymentTermText(bool paysCash) => paysCash ? "เงินสด" : "เครดิต";

    /// <summary>FULL or EMPTY in front of the commodity: "FULL / RICE", "EMPTY".</summary>
    internal static string Commodity(string? fullEmpty, string? commodity) =>
        string.Join(" / ", new[] { fullEmpty, commodity }.Where(x => !string.IsNullOrWhiteSpace(x)));

    /// <summary>A movement code's load: FULL_OUT is FULL, MTY_OUT is EMPTY; anything else unknown.</summary>
    internal static string? FullEmptyOf(string? movementCode) =>
        movementCode is null ? null
        : movementCode.StartsWith("FULL", StringComparison.OrdinalIgnoreCase) ? "FULL"
        : movementCode.StartsWith("MTY", StringComparison.OrdinalIgnoreCase) || movementCode.StartsWith("EMPTY", StringComparison.OrdinalIgnoreCase) ? "EMPTY"
        : null;

    /// <summary>The box's weight as the gate weighed it: the gross, else tare + cargo, else whichever of the two was keyed.</summary>
    internal static decimal? WeightOf(decimal? gross, decimal? tare, decimal? cargo) =>
        gross is > 0 ? gross
        : tare is > 0 && cargo is > 0 ? tare + cargo
        : tare is > 0 ? tare
        : cargo is > 0 ? cargo
        : null;

    /// <summary>"12,340 KG"; nothing when no weight is known.</summary>
    internal static string Kg(decimal? kg) => kg is > 0 ? $"{kg.Value.ToString("#,0", CultureInfo.InvariantCulture)} KG" : "";

    // ── the RDL's expressions ───────────────────────────────────────────────

    /// <summary><c>=Fields!Size.Value + " " + Fields!Type.Value</c>: Vector kept size and type apart; Gecko's code is size then type ("20GP").</summary>
    internal static string SizeType(string typeCode, string? sizeCode) =>
        sizeCode is { Length: > 0 } size && typeCode.StartsWith(size, StringComparison.OrdinalIgnoreCase)
            ? $"{size} {typeCode[size.Length..]}"
            : typeCode.Length > 2 ? $"{typeCode[..2]} {typeCode[2..]}" : typeCode;

    /// <summary>
    /// <c>=IIF(Temperature&lt;&gt;0, Temperature.tostring() + " " + TemperatureModeDescription, "")</c>. Vector's numeric(5,2)
    /// prints two decimals; Gecko keeps the set point in Celsius, Vector's TempratureMode CEL.
    /// </summary>
    internal static string SetTemp(decimal? celsius) =>
        celsius is { } t && t != 0 ? $"{t.ToString("0.00", CultureInfo.InvariantCulture)} CEL" : "";

    /// <summary>
    /// <c>=IIF(Vent&lt;&gt;0, Vent.tostring() + " " + VentilationModeDescription, "")</c>. Vector's numeric(8,2); Gecko records the
    /// opening as a number with no ventilation mode, so the mode prints empty rather than guessed.
    /// </summary>
    internal static string Vent(decimal? vent) =>
        vent is { } v && v != 0 ? $"{v.ToString("0.00", CultureInfo.InvariantCulture)} " : "";

    /// <summary><c>=format(DateTimeIn, "HH:mm") + " Hrs"</c>.</summary>
    internal static string TimeOfDay(DateTimeOffset at) => at.ToString("HH:mm", CultureInfo.InvariantCulture) + " Hrs";

    /// <summary><c>=Fields!Remarks.Value + " " + Fields!ContainerRemarks.Value</c>: the booking's remark, then the box's.</summary>
    internal static string Remarks(string? booking, string? box) => $"{booking} {box}".Trim();

    // ── the layout, in the RDL's own coordinates (inches) ───────────────────

    private const float Inch = 72f;
    private const string Black = "#000000";

    /// <summary>A4 portrait sheets, the RDL's 0.1in margins, header 0.91678in, a 3.35926in body per box, footer 0.26042in.</summary>
    internal static byte[] Render(Header header, IReadOnlyList<Sheet> sheets)
    {
        GeckoPdf.EnsureInitialised();
        return Document.Create(document =>
        {
            foreach (var sheet in sheets)
                SheetPage(document, header, sheet);
        }).GeneratePdf();
    }

    /// <summary>One A4 page per box: the header (with the sheet's slip title and payment term) over its body.</summary>
    private static void SheetPage(IDocumentContainer document, Header header, Sheet sheet)
    {
        document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(0.1f, Unit.Inch);
            page.DefaultTextStyle(t => t.FontSize(10).FontFamily(GeckoPdf.LatinFont, GeckoPdf.ThaiFont).FontColor(Black));

            page.Header().Height(0.91678f, Unit.Inch).Layers(layers =>
            {
                layers.PrimaryLayer();
                Box(layers, 0, 0.0243f, 7.9347f, 0.2362f, header.CompanyName, 8, bold: true, align: Center);
                Box(layers, 0.30566f, 0.0868f, 3.85639f, 0.25f, "** DAILY EQUIPMENT DETAIL CONTACT YARD **", 8, bold: true);
                Box(layers, 0.30566f, 5.72415f, 0.82514f, 0.25f, "Created By", 8);
                Box(layers, 0.30566f, 6.61873f, 1.34029f, 0.25f, header.CreatedBy, 8);
                Box(layers, 0.59733f, 5.72415f, 0.82514f, 0.25f, "Printed Date", 8);
                Box(layers, 0.59733f, 6.61874f, 1.34028f, 0.25f, header.DateTimeIn, 8);
                // Owner 2026-10-20: the slip title above the form's name, the payment term beside it
                Box(layers, 0f, 0.0868f, 1.8f, 0.3f, sheet.SlipTitle, 13, bold: true);
                Box(layers, 0.24f, 4.05f, 1.6f, 0.35f, sheet.PaymentTerm, 16, bold: true);
            });

            page.Content().Height(3.35926f, Unit.Inch).Layers(layers =>
            {
                layers.PrimaryLayer();
                if (sheet.IsVoided) layers.Layer().VoidWatermark();
                SheetBody(layers, sheet);
            });

            page.Footer().Height(0.26042f, Unit.Inch);
        });
    }

    /// <summary>Rectangle1 of Tablix1 (top 0.11966in, left 0.0868in); every item at its RDL position inside it.</summary>
    private static void SheetBody(LayersDescriptor layers, Sheet s)
    {
        const float top = 0.11966f, left = 0.0868f;
        void Label(float t, float l, float w, string text) => Box(layers, top + t, left + l, w, 0.25f, text, 8);
        void Value(float t, float l, float w, string text, float size = 10, bool bold = true) =>
            Box(layers, top + t, left + l, w, 0.25f, text, size, bold, dotted: true);

        // Agent · Shipper · Commodity
        Label(0.13479f, 0.06250f, 0.57291f, "Agent");
        Value(0.13479f, 0.67708f, 0.95833f, s.AgentCode);
        Label(0.13479f, 1.69570f, 0.44791f, "Shipper");
        Value(0.13479f, 2.19917f, 2.98832f, s.CustomerName);
        Label(0.13479f, 5.25694f, 0.63542f, "Commodity");
        Value(0.13479f, 5.96180f, 1.91042f, s.CommodityDesc);

        // Booking No · Size/Type · Settemp · Vent · Pre-Cool
        Label(0.61520f, 0.06250f, 0.87499f, "Booking No");
        Value(0.61520f, 0.99305f, 1.17361f, s.BookingBlNo);
        Label(0.61520f, 2.32070f, 0.57291f, "Size/Type");
        Value(0.61520f, 2.96305f, 0.50223f, s.SizeType);
        Label(0.61520f, 3.53472f, 0.48958f, "Settemp");
        Value(0.61520f, 4.09374f, 0.63749f, s.SetTemp);
        Label(0.61520f, 4.78679f, 0.48958f, "Vent");
        Value(0.61520f, 5.30414f, 0.58822f, s.Vent);
        Label(0.61520f, 5.98875f, 0.62499f, "Pre-Cool");
        Value(0.61520f, 6.62011f, 1.03682f, s.PreCool);

        // Cntr no. 1, and the requested seal (its value sits just above the "Request Seal" label, as in the RDL)
        Value(0.93881f, 6.65761f, 1.12432f, s.ContainerNo);
        Label(0.96520f, 5.98875f, 0.62499f, "Cntr no. 1");
        Value(1.35473f, 6.74511f, 1.03682f, s.SealNo1);

        // Owner 2026-10-22: Yard Location · Weight, and Destination Port moved up to give the haulier room
        Label(1.03000f, 0.06250f, 0.80000f, "Yard Location");
        Value(1.03000f, 1.06000f, 1.20000f, s.YardLocation);
        Label(1.03000f, 2.32000f, 0.52000f, "Weight");
        Value(1.03000f, 2.86000f, 0.95000f, s.Weight);
        Label(1.03000f, 3.86000f, 1.05000f, "Destination Port");
        Value(1.03000f, 4.93000f, 0.98000f, s.DestinationPort);

        // Haulier (its whole name: three times the RDL's 0.925in) · Plate · Time · Request Seal
        Label(1.38000f, 0.06250f, 0.57291f, "Haulier");
        Value(1.38000f, 0.65486f, 2.95000f, s.HaulierName, size: 9);
        Label(1.38000f, 3.68000f, 0.32916f, "Plate");
        Value(1.38000f, 4.03000f, 0.80000f, s.TruckNo);
        Label(1.38000f, 4.86000f, 0.30208f, "Time");
        Value(1.38000f, 5.18000f, 0.78000f, s.TimeIn);
        Label(1.38000f, 5.98875f, 0.73957f, "Request Seal");

        // Remark
        Label(1.71520f, 0.06250f, 0.57291f, "Remark");
        Value(1.71520f, 0.69097f, 6.59375f, s.Remarks, size: 8, bold: false);

        // Owner 2026-10-22: the driver, and a line for the driver to sign
        Label(2.09827f, 0.06250f, 0.50000f, "Driver");
        Value(2.09827f, 0.62000f, 2.30000f, s.DriverName);
        Label(2.09827f, 3.00000f, 0.45000f, "Phone");
        Value(2.09827f, 3.48000f, 1.55000f, s.DriverMobile);
        Label(2.78229f, 0.06250f, 1.08333f, "Driver Signature");
        DottedLine(layers, top + 3.03799f, left + 1.24876f, 1.28999f);

        // Signatures
        Label(2.09827f, 5.24305f, 1.08333f, "Survey By Body");
        DottedLine(layers, top + 2.35397f, left + 6.42931f, 1.28999f);
        Label(2.44271f, 0.06250f, 1.08333f, "Customer Signature");
        Label(2.44271f, 5.24305f, 1.08333f, "Survey By Cleaning");
        DottedLine(layers, top + 2.69271f, left + 6.42931f, 1.28999f);
        DottedLine(layers, top + 2.69841f, left + 1.24876f, 1.28999f);
        Label(2.78229f, 5.24305f, 1.08333f, "Technician Check");
        DottedLine(layers, top + 3.03229f, left + 6.42931f, 1.28999f);
    }

    private enum Align { Left, Center }
    private const Align Center = Align.Center;

    /// <summary>
    /// An RDL Textbox (2pt padding, CanGrow). A value box keeps its RDL width and grows downward, its dotted 1pt
    /// bottom border following the text; a label runs to its full width — the embedded Noto Sans is wider than the
    /// RDL's Tahoma, and SSRS would have grown the box rather than cut the word.
    /// </summary>
    private static void Box(LayersDescriptor layers, float top, float left, float width, float height, string text,
        float size, bool bold = false, bool dotted = false, Align align = Align.Left)
    {
        var at = layers.Layer().PaddingTop(top * Inch).PaddingLeft(left * Inch).AlignTop().AlignLeft();
        void Write(IContainer box)
        {
            var aligned = align == Align.Center ? box.AlignCenter() : box.AlignLeft();
            aligned.Text(t =>
            {
                var span = t.Span(text).FontSize(size);
                if (bold) span.Bold();
            });
        }

        if (dotted)
            at.Width(width * Inch).Column(c =>
            {
                Write(c.Item().MinHeight(height * Inch - 1).Padding(2));
                c.Item().LineHorizontal(1).LineColor(Black).LineDashPattern([1f, 2f]);
            });
        else if (align == Align.Center)
            Write(at.Width(width * Inch).MinHeight(height * Inch).Padding(2));
        else
            Write(at.MinWidth(width * Inch).MinHeight(height * Inch).Padding(2));
    }

    /// <summary>An RDL Line: dotted, 1pt, horizontal.</summary>
    private static void DottedLine(LayersDescriptor layers, float top, float left, float width) =>
        layers.Layer().PaddingTop(top * Inch).PaddingLeft(left * Inch).AlignTop().AlignLeft()
            .Width(width * Inch).LineHorizontal(1).LineColor(Black).LineDashPattern([1f, 2f]);
}
