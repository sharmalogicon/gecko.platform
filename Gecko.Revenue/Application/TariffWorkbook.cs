using ClosedXML.Excel;

namespace Gecko.Revenue.Application;

/// <summary>
/// The TOS rate template, layout version 1 — writing it and reading it back.
///
/// EXPORT-THEN-EDIT (ROADMAP decision 6): a workbook is only ever generated
/// from a real tariff, with its codes filled in, and carries a token in a very
/// hidden sheet. An upload without a token we issued is refused — a blank
/// template invites typed codes, and typed codes are how Vector ended up with
/// 20ST → 'XXXX'.
///
/// What the user can and cannot change:
///   * RateKey (column A) is locked AND hidden: it is how an edited line finds
///     the rate it came from. Blank = a new line. A copied row carries its key;
///     the importer keeps the first (top-most) occurrence as the edit and treats
///     every later one as a new rate.
///   * Deleting a line deletes that rate.
///   * Conditions (column Q) are shown, not edited (PLAN.md Q9): a line that
///     keeps its RateKey keeps its surcharges on import.
/// </summary>
internal static class TariffWorkbook
{
    public const int LayoutVersion = 1;
    public const string RatesSheet = "Rates";
    public const string ListsSheet = "Lists";
    public const string MetaSheet = "_gecko";

    public static readonly string[] Columns =
    [
        "RateKey", "ChargeCode", "BillTo", "PaymentTerm", "CreditDays", "OrderType", "Movement",
        "EquipmentType", "Size", "CargoCategory", "TruckCategory", "BillingUnit",
        "PricingMethod", "TierBasis", "Rate", "Tiers", "Conditions",
    ];

    public sealed record Line(
        Guid RateKey, string ChargeCode, string BillTo, string PaymentTerm, short? CreditDays,
        string? OrderType, string? Movement, string? EquipmentType, string? Size, string? CargoCategory,
        string? TruckCategory, string BillingUnit, string PricingMethod, string? TierBasis, decimal? Rate,
        string Tiers, string Conditions);

    public sealed record Header(Guid Token, Guid ScheduleId, string ScheduleNo, short VersionNo, int LayoutVersion);

    /// <summary>
    /// The drop-down values, all from the TENANT's master data at download time.
    /// A list that comes back empty simply gets no drop-down (the upload still validates).
    /// </summary>
    public sealed record Lists(
        IReadOnlyList<string> ChargeCodes, IReadOnlyList<string> BillTo, IReadOnlyList<string> PaymentTerms,
        IReadOnlyList<string> BillingUnits,
        IReadOnlyList<string> OrderTypes, IReadOnlyList<string> Movements, IReadOnlyList<string> EquipmentTypes,
        IReadOnlyList<string> Sizes, IReadOnlyList<string> CargoCategories, IReadOnlyList<string> TruckCategories);

    /// <summary>The 1-based column of a header in <see cref="Columns"/>.</summary>
    public static int ColumnNo(string name) => Array.IndexOf(Columns, name) + 1;

    /// <summary>What each column means, for a depot clerk — shown as a note on the header cell.</summary>
    private static readonly Dictionary<string, string> HeaderNotes = new()
    {
        ["RateKey"] = "System key (hidden). Do not edit. Blank = a new rate.",
        ["ChargeCode"] = "Required. Pick from list.",
        ["BillTo"] = "Required. Who pays. Pick from list.",
        ["PaymentTerm"] = "Required. CASH or CREDIT. Pick from list.",
        ["CreditDays"] = "Credit days (number). Leave blank = the charge's default.",
        ["OrderType"] = "Pick from list. Leave blank = any order type.",
        ["Movement"] = "Pick from list. Leave blank = any movement.",
        ["EquipmentType"] = "Pick from list (e.g. 22G1). Leave blank = any type.",
        ["Size"] = "Container length: pick 20 / 40 / 45. Leave blank = any size.",
        ["CargoCategory"] = "Pick from list. Leave blank = any cargo.",
        ["TruckCategory"] = "Pick from list. Leave blank = any truck.",
        ["BillingUnit"] = "Pick from list. Leave blank = the charge code's unit.",
        ["PricingMethod"] = "Pick from list. Blank = FLAT (one price in Rate).",
        ["TierBasis"] = "Only for TIERED methods: what the tiers count (DAY, HOUR, TEU...). Pick from list.",
        ["Rate"] = "The price (number) for FLAT. Leave blank when using Tiers.",
        ["Tiers"] = "Only for TIERED methods. Write like 1-7:160; 8-14:275; 15+:390 (counts chargeable units, after free time).",
        ["Conditions"] = "Read only. Surcharges/conditions are edited in GECKO, not here. A copied row does not copy them.",
    };

    public static byte[] Write(Header header, string scheduleName, IReadOnlyList<Line> lines, Lists lists)
    {
        using var book = new XLWorkbook();

        // ── Rates ───────────────────────────────────────────────────────────
        var sheet = book.AddWorksheet(RatesSheet);
        for (var c = 0; c < Columns.Length; c++)
            sheet.Cell(1, c + 1).Value = Columns[c];

        var row = 2;
        foreach (var l in lines)
        {
            object?[] values =
            [
                l.RateKey.ToString(), l.ChargeCode, l.BillTo, l.PaymentTerm, l.CreditDays, l.OrderType, l.Movement,
                l.EquipmentType, l.Size, l.CargoCategory, l.TruckCategory, l.BillingUnit,
                l.PricingMethod, l.TierBasis, l.Rate, l.Tiers, l.Conditions,
            ];
            for (var c = 0; c < values.Length; c++)
                sheet.Cell(row, c + 1).Value = values[c] switch
                {
                    null => Blank.Value,
                    decimal d => d,
                    short s => s,
                    var v => v.ToString(),
                };
            row++;
        }

        var header1 = sheet.Row(1);
        header1.Style.Font.Bold = true;
        header1.Style.Fill.BackgroundColor = XLColor.FromHtml("#DDE7EE");
        sheet.SheetView.FreezeRows(1);
        for (var c = 0; c < Columns.Length; c++)
            if (HeaderNotes.TryGetValue(Columns[c], out var note))
            {
                var comment = sheet.Cell(1, c + 1).CreateComment();
                comment.Author = "GECKO";
                comment.AddText(note);
            }

        // Everything is editable except the header, the key and the read-only
        // conditions column. Rows may be inserted and deleted.
        sheet.Style.Protection.Locked = false;
        header1.Style.Protection.Locked = true;
        sheet.Column(1).Style.Protection.Locked = true;
        sheet.Column(1).Style.Font.FontColor = XLColor.Gray;
        sheet.Column(Columns.Length).Style.Protection.Locked = true;
        sheet.Column(Columns.Length).Style.Font.Italic = true;
        sheet.Protect()
            .AllowElement(XLSheetProtectionElements.InsertRows)
            .AllowElement(XLSheetProtectionElements.DeleteRows)
            .AllowElement(XLSheetProtectionElements.AutoFilter)
            .AllowElement(XLSheetProtectionElements.Sort)
            .AllowElement(XLSheetProtectionElements.FormatColumns);
        sheet.Range(1, 1, Math.Max(row - 1, 1), Columns.Length).SetAutoFilter();

        // ── Lists (drop-downs, so codes are picked rather than typed) ────────
        // Hidden (not very hidden): Excel list validations still read it.
        var listSheet = book.AddWorksheet(ListsSheet);
        var listColumn = 0;
        void List(string title, IReadOnlyList<string> values)
        {
            var column = ++listColumn;
            listSheet.Cell(1, column).Value = title;
            for (var i = 0; i < values.Count; i++) listSheet.Cell(i + 2, column).Value = values[i];
            if (values.Count == 0) return;
            var source = listSheet.Range(2, column, values.Count + 1, column);
            var target = ColumnNo(title);
            var validation = sheet.Range(2, target, 5000, target).CreateDataValidation();
            validation.List(source);
            validation.IgnoreBlanks = true;
            validation.ShowErrorMessage = true;
            validation.ErrorStyle = XLErrorStyle.Stop;
            validation.ErrorTitle = title;
            validation.ErrorMessage = $"Pick a {title} from the list (or leave it blank where allowed).";
        }
        List("ChargeCode", lists.ChargeCodes);
        List("BillTo", lists.BillTo);
        List("PaymentTerm", lists.PaymentTerms);
        List("OrderType", lists.OrderTypes);
        List("Movement", lists.Movements);
        List("EquipmentType", lists.EquipmentTypes);
        List("Size", lists.Sizes);
        List("CargoCategory", lists.CargoCategories);
        List("TruckCategory", lists.TruckCategories);
        List("BillingUnit", lists.BillingUnits);
        List("PricingMethod", ["FLAT", "TIERED_INCREMENTAL", "TIERED_BAND", "TIERED_BLOCK"]);
        List("TierBasis", ["DAY", "HOUR", "TEU", "FLEET_TEU"]);
        listSheet.Cell(1, listColumn + 2).Value = "Tiers are written 1-7:160; 8-14:275; 15+:390 and count CHARGEABLE units (after free time).";
        listSheet.Protect();
        listSheet.Visibility = XLWorksheetVisibility.Hidden;

        sheet.Cell(1, Columns.Length + 2).Value = $"{header.ScheduleNo} v{header.VersionNo} — {scheduleName}";
        sheet.Cell(2, Columns.Length + 2).Value = "Pick codes from the drop-downs. Blank axis = any. Copy a row to add a similar rate. Hover a header for help.";
        sheet.Cell(2, Columns.Length + 2).Style.Font.Italic = true;
        sheet.Columns().AdjustToContents(1, Math.Min(row, 200));
        foreach (var c in new[] { "ChargeCode", "BillTo", "PaymentTerm", "OrderType", "Movement", "EquipmentType",
                     "CargoCategory", "TruckCategory", "BillingUnit", "PricingMethod", "TierBasis" })
            if (sheet.Column(ColumnNo(c)).Width < 16) sheet.Column(ColumnNo(c)).Width = 16;

        // The key stays in the file for the round trip, out of sight (and locked).
        sheet.Column(1).Hide();

        // ── the token ───────────────────────────────────────────────────────
        var meta = book.AddWorksheet(MetaSheet);
        meta.Cell(1, 1).Value = "token";          meta.Cell(1, 2).Value = header.Token.ToString();
        meta.Cell(2, 1).Value = "schedule_id";    meta.Cell(2, 2).Value = header.ScheduleId.ToString();
        meta.Cell(3, 1).Value = "schedule_no";    meta.Cell(3, 2).Value = header.ScheduleNo;
        meta.Cell(4, 1).Value = "version_no";     meta.Cell(4, 2).Value = header.VersionNo;
        meta.Cell(5, 1).Value = "layout_version"; meta.Cell(5, 2).Value = header.LayoutVersion;
        meta.Visibility = XLWorksheetVisibility.VeryHidden;
        meta.Protect();

        sheet.SetTabActive();
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    public sealed record ReadRow(int RowNo, IReadOnlyDictionary<string, string?> Cells);

    public sealed record ReadResult(Header? Header, IReadOnlyList<ReadRow> Rows, string? Error);

    /// <summary>Reads cell TEXT only — types are decided by the importer, which can then say which cell is wrong.</summary>
    public static ReadResult Read(Stream file)
    {
        XLWorkbook book;
        try { book = new XLWorkbook(file); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new ReadResult(null, [], "This is not an Excel workbook (.xlsx).");
        }

        using (book)
        {
            if (!book.TryGetWorksheet(MetaSheet, out var meta)
                || !Guid.TryParse(meta.Cell(1, 2).GetString(), out var token)
                || !Guid.TryParse(meta.Cell(2, 2).GetString(), out var scheduleId))
                return new ReadResult(null, [], "This workbook was not downloaded from GECKO. Download the tariff's template and edit that.");

            var header = new Header(token, scheduleId, meta.Cell(3, 2).GetString(),
                (short)meta.Cell(4, 2).GetValue<int>(), meta.Cell(5, 2).GetValue<int>());

            if (!book.TryGetWorksheet(RatesSheet, out var sheet))
                return new ReadResult(header, [], $"The '{RatesSheet}' sheet is missing.");

            for (var c = 0; c < Columns.Length; c++)
                if (!string.Equals(sheet.Cell(1, c + 1).GetString().Trim(), Columns[c], StringComparison.OrdinalIgnoreCase))
                    return new ReadResult(header, [], $"Column {c + 1} should be '{Columns[c]}'. Do not rename, move or remove columns.");

            var rows = new List<ReadRow>();
            var last = sheet.LastRowUsed()?.RowNumber() ?? 1;
            for (var r = 2; r <= last; r++)
            {
                var cells = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var c = 0; c < Columns.Length; c++)
                {
                    var text = sheet.Cell(r, c + 1).GetFormattedString().Trim();
                    cells[Columns[c]] = text.Length == 0 ? null : text;
                }
                // A line with nothing but a key or a conditions note is an emptied line — skip it.
                if (Columns[1..^1].All(col => cells[col] is null)) continue;
                rows.Add(new ReadRow(r, cells));
            }
            return new ReadResult(header, rows, null);
        }
    }
}
