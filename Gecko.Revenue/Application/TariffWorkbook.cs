using ClosedXML.Excel;
using Gecko.MasterData.Contracts;

namespace Gecko.Revenue.Application;

/// <summary>
/// The TOS rate template — writing it and reading it back. One writer serves
/// both downloads, so the file that is parsed is always the file we wrote.
///
/// Two kinds, told apart by the very hidden _gecko sheet (template_kind):
///   * SCHEDULE — GET /tariffs/{id}/template: the draft's rates with their
///     keys, plus a token recorded in import.template_export. Uploading it
///     REPLACES the draft's rate set: a deleted line deletes that rate.
///   * BLANK — GET /tariffs/template: no rows, no token, no schedule. Uploading
///     it MERGES into a draft: a line matching an existing rate on every code
///     changes that rate's price, any other line is a new rate, and nothing is
///     removed (gecko_revenue 22).
///
/// Layout 2 (this writer) puts a note in row 1 and the headers in row 2, and
/// lists every code with its description on a visible reference sheet that
/// the drop-downs read. Layout 1 files (headers in row 1) still upload.
///
/// What the user can and cannot change:
///   * RateKey (column A) is locked AND hidden: it is how an edited line finds
///     the rate it came from. Blank = a new line. A copied row carries its key;
///     the importer keeps the first (top-most) occurrence as the edit and treats
///     every later one as a new rate.
///   * Conditions (column Q) are shown, not edited (PLAN.md Q9): a line that
///     keeps its RateKey keeps its surcharges on import.
/// </summary>
internal static class TariffWorkbook
{
    public const int LayoutVersion = 2;
    public const string RatesSheet = "Rates";
    public const string ListsSheet = "Lists";
    public const string MetaSheet = "_gecko";
    public const string KindSchedule = "SCHEDULE";
    public const string KindBlank = "BLANK";
    public const string TierSyntax = "1-7:160; 8-14:275; 15+:390";

    /// <summary>Layouts the reader understands: files already downloaded keep uploading.</summary>
    public static bool ReadsLayout(int layoutVersion) => layoutVersion is 1 or 2;

    /// <summary>The header row of a layout: 1 in layout 1, 2 (under the note) from layout 2.</summary>
    public static int HeaderRow(int layoutVersion) => layoutVersion >= 2 ? 2 : 1;

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

    /// <summary>
    /// What the _gecko sheet says. A BLANK template has no token and no schedule
    /// (all null); a SCHEDULE template has all four.
    /// </summary>
    public sealed record Header(Guid? Token, Guid? ScheduleId, string? ScheduleNo, short? VersionNo, int LayoutVersion, string Kind)
    {
        public bool IsBlank => Kind == KindBlank;

        public static Header Blank() => new(null, null, null, null, TariffWorkbook.LayoutVersion, KindBlank);

        public static Header ForSchedule(Guid token, Guid scheduleId, string scheduleNo, short versionNo) =>
            new(token, scheduleId, scheduleNo, versionNo, TariffWorkbook.LayoutVersion, KindSchedule);
    }

    /// <summary>
    /// The reference sheets, all from the TENANT's master data at download time.
    /// A list that comes back empty gets its sheet but no drop-down (the upload still validates).
    /// </summary>
    public sealed record Lists(
        IReadOnlyList<CodeDescription> ChargeCodes, IReadOnlyList<CodeDescription> BillTo, IReadOnlyList<CodeDescription> PaymentTerms,
        IReadOnlyList<CodeDescription> BillingUnits,
        IReadOnlyList<CodeDescription> OrderTypes, IReadOnlyList<CodeDescription> Movements, IReadOnlyList<CodeDescription> EquipmentTypes,
        IReadOnlyList<CodeDescription> Sizes, IReadOnlyList<CodeDescription> CargoCategories, IReadOnlyList<CodeDescription> TruckCategories);

    /// <summary>Rates-sheet column → its visible reference sheet, in tab order.</summary>
    public static readonly (string Column, string Sheet)[] ReferenceSheets =
    [
        ("ChargeCode", "Charge codes"), ("BillTo", "Bill to"), ("PaymentTerm", "Payment terms"),
        ("BillingUnit", "Billing units"), ("OrderType", "Order types"), ("Movement", "Movements"),
        ("EquipmentType", "Equipment types"), ("Size", "Sizes"), ("CargoCategory", "Cargo categories"),
        ("TruckCategory", "Truck categories"),
    ];

    /// <summary>The 1-based column of a header in <see cref="Columns"/>.</summary>
    public static int ColumnNo(string name) => Array.IndexOf(Columns, name) + 1;

    /// <summary>What each column means, for a depot clerk — shown as a note on the header cell.</summary>
    private static readonly Dictionary<string, string> HeaderNotes = new()
    {
        ["RateKey"] = "System key (hidden). Do not edit. Blank = a new rate.",
        ["ChargeCode"] = "Required. Pick from list (sheet 'Charge codes').",
        ["BillTo"] = "Required. Who pays. Pick from list (sheet 'Bill to').",
        ["PaymentTerm"] = "Required. CASH or CREDIT. Pick from list (sheet 'Payment terms').",
        ["CreditDays"] = "Credit days (number). Leave blank = the charge's default.",
        ["OrderType"] = "Pick from list (sheet 'Order types'). Leave blank = any order type.",
        ["Movement"] = "Pick from list (sheet 'Movements'). Leave blank = any movement.",
        ["EquipmentType"] = "Pick from list (sheet 'Equipment types', e.g. 22G1). Leave blank = any type.",
        ["Size"] = "Container length: pick 20 / 40 / 45 (sheet 'Sizes'). Leave blank = any size.",
        ["CargoCategory"] = "Pick from list (sheet 'Cargo categories'). Leave blank = any cargo.",
        ["TruckCategory"] = "Pick from list (sheet 'Truck categories'). Leave blank = any truck.",
        ["BillingUnit"] = "Pick from list (sheet 'Billing units'). Leave blank = the charge code's unit.",
        ["PricingMethod"] = "Pick from list. Blank = FLAT (one price in Rate).",
        ["TierBasis"] = "Only for TIERED methods: what the tiers count (DAY, HOUR, TEU...). Pick from list.",
        ["Rate"] = "The price (number) for FLAT. Leave blank when using Tiers.",
        ["Tiers"] = $"Only for TIERED methods. Write like {TierSyntax} (counts chargeable units, after free time).",
        ["Conditions"] = "Read only. Surcharges/conditions are edited in GECKO, not here. A copied row does not copy them.",
    };

    public static byte[] Write(Header header, string title, IReadOnlyList<Line> lines, Lists lists)
    {
        var headerRow = HeaderRow(LayoutVersion);
        var firstDataRow = headerRow + 1;
        const int lastValidatedRow = 5000;

        using var book = new XLWorkbook();

        // ── Rates ───────────────────────────────────────────────────────────
        var sheet = book.AddWorksheet(RatesSheet);

        // Row 1: how to fill it in. Above the headers, so sorting and filtering never move it.
        var note = sheet.Range(1, 2, 1, Columns.Length);
        note.Merge();
        note.FirstCell().Value =
            "One line per rate. Pick codes from the drop-downs; the sheets after this one list every code and what it means. " +
            "A blank axis (order type, movement, equipment type, size, cargo, truck) = any. " +
            $"Tiers are written {TierSyntax} and count chargeable units (after free time). " +
            "RateKey (hidden column A) stays blank on new lines.";
        note.Style.Alignment.WrapText = true;
        note.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        note.Style.Font.Italic = true;
        note.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF8E1");
        sheet.Row(1).Height = 48;

        for (var c = 0; c < Columns.Length; c++)
            sheet.Cell(headerRow, c + 1).Value = Columns[c];

        var row = firstDataRow;
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

        var headers = sheet.Row(headerRow);
        headers.Style.Font.Bold = true;
        headers.Style.Fill.BackgroundColor = XLColor.FromHtml("#DDE7EE");
        sheet.SheetView.FreezeRows(headerRow);
        for (var c = 0; c < Columns.Length; c++)
            if (HeaderNotes.TryGetValue(Columns[c], out var help))
            {
                var comment = sheet.Cell(headerRow, c + 1).CreateComment();
                comment.Author = "GECKO";
                comment.AddText(help);
            }

        // Everything is editable except the note, the header, the key and the
        // read-only conditions column. Rows may be inserted and deleted.
        sheet.Style.Protection.Locked = false;
        sheet.Row(1).Style.Protection.Locked = true;
        headers.Style.Protection.Locked = true;
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
        sheet.Range(headerRow, 1, Math.Max(row - 1, headerRow), Columns.Length).SetAutoFilter();

        void DropDown(string column, IXLRange source)
        {
            var target = ColumnNo(column);
            var validation = sheet.Range(firstDataRow, target, lastValidatedRow, target).CreateDataValidation();
            validation.List(source);
            validation.IgnoreBlanks = true;
            validation.ShowErrorMessage = true;
            validation.ErrorStyle = XLErrorStyle.Stop;
            validation.ErrorTitle = column;
            validation.ErrorMessage = $"Pick a {column} from the list (or leave it blank where allowed).";
        }

        // ── reference sheets: code + description, visible, read-only ─────────
        var byColumn = new Dictionary<string, IReadOnlyList<CodeDescription>>
        {
            ["ChargeCode"] = lists.ChargeCodes, ["BillTo"] = lists.BillTo, ["PaymentTerm"] = lists.PaymentTerms,
            ["BillingUnit"] = lists.BillingUnits, ["OrderType"] = lists.OrderTypes, ["Movement"] = lists.Movements,
            ["EquipmentType"] = lists.EquipmentTypes, ["Size"] = lists.Sizes, ["CargoCategory"] = lists.CargoCategories,
            ["TruckCategory"] = lists.TruckCategories,
        };
        foreach (var (column, name) in ReferenceSheets)
        {
            var values = byColumn[column];
            var reference = book.AddWorksheet(name);
            reference.Cell(1, 1).Value = "Code";
            reference.Cell(1, 2).Value = "Description";
            reference.Row(1).Style.Font.Bold = true;
            reference.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#DDE7EE");
            for (var i = 0; i < values.Count; i++)
            {
                reference.Cell(i + 2, 1).Value = values[i].Code;
                reference.Cell(i + 2, 2).Value = values[i].Description;
            }
            if (values.Count == 0) reference.Cell(2, 2).Value = "(none defined for this depot)";
            reference.SheetView.FreezeRows(1);
            reference.Columns(1, 2).AdjustToContents();
            reference.Protect()
                .AllowElement(XLSheetProtectionElements.AutoFilter)
                .AllowElement(XLSheetProtectionElements.Sort);
            if (values.Count > 0) DropDown(column, reference.Range(2, 1, values.Count + 1, 1));
        }

        // ── Lists: the two fixed vocabularies (hidden; Excel validations still read it) ──
        var listSheet = book.AddWorksheet(ListsSheet);
        var listColumn = 0;
        void List(string column, IReadOnlyList<string> values)
        {
            var at = ++listColumn;
            listSheet.Cell(1, at).Value = column;
            for (var i = 0; i < values.Count; i++) listSheet.Cell(i + 2, at).Value = values[i];
            DropDown(column, listSheet.Range(2, at, values.Count + 1, at));
        }
        List("PricingMethod", ["FLAT", "TIERED_INCREMENTAL", "TIERED_BAND", "TIERED_BLOCK"]);
        List("TierBasis", ["DAY", "HOUR", "TEU", "FLEET_TEU"]);
        listSheet.Protect();
        listSheet.Visibility = XLWorksheetVisibility.Hidden;

        sheet.Cell(1, Columns.Length + 2).Value = title;
        sheet.Cell(2, Columns.Length + 2).Value = "Pick codes from the drop-downs. Copy a row to add a similar rate. Hover a header for help.";
        sheet.Cell(2, Columns.Length + 2).Style.Font.Italic = true;
        sheet.Columns().AdjustToContents(headerRow, Math.Min(row, 200));
        foreach (var c in new[] { "ChargeCode", "BillTo", "PaymentTerm", "OrderType", "Movement", "EquipmentType",
                     "CargoCategory", "TruckCategory", "BillingUnit", "PricingMethod", "TierBasis" })
            if (sheet.Column(ColumnNo(c)).Width < 16) sheet.Column(ColumnNo(c)).Width = 16;

        // The key stays in the file for the round trip, out of sight (and locked).
        sheet.Column(1).Hide();

        // ── what this file is ───────────────────────────────────────────────
        var meta = book.AddWorksheet(MetaSheet);
        meta.Cell(1, 1).Value = "token";          meta.Cell(1, 2).Value = header.Token?.ToString() ?? "";
        meta.Cell(2, 1).Value = "schedule_id";    meta.Cell(2, 2).Value = header.ScheduleId?.ToString() ?? "";
        meta.Cell(3, 1).Value = "schedule_no";    meta.Cell(3, 2).Value = header.ScheduleNo ?? "";
        meta.Cell(4, 1).Value = "version_no";     meta.Cell(4, 2).Value = header.VersionNo is { } version ? version : Blank.Value;
        meta.Cell(5, 1).Value = "layout_version"; meta.Cell(5, 2).Value = header.LayoutVersion;
        meta.Cell(6, 1).Value = "template_kind";  meta.Cell(6, 2).Value = header.Kind;
        meta.Visibility = XLWorksheetVisibility.VeryHidden;
        meta.Protect();

        sheet.SetTabActive();
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    public sealed record ReadRow(int RowNo, IReadOnlyDictionary<string, string?> Cells);

    public sealed record ReadResult(Header? Header, IReadOnlyList<ReadRow> Rows, string? Error);

    private const string NotOurs = "This workbook was not downloaded from GECKO. Download a template from GECKO and fill that in.";

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
                || !int.TryParse(meta.Cell(5, 2).GetString(), out var layout))
                return new ReadResult(null, [], NotOurs);

            // Layout 1 had no template_kind row: every layout-1 file is a SCHEDULE template.
            var kind = meta.Cell(6, 2).GetString().Trim().ToUpperInvariant() is { Length: > 0 } k ? k : KindSchedule;
            Header header;
            if (kind == KindBlank)
                header = new Header(null, null, null, null, layout, KindBlank);
            else if (kind == KindSchedule
                     && Guid.TryParse(meta.Cell(1, 2).GetString(), out var token)
                     && Guid.TryParse(meta.Cell(2, 2).GetString(), out var scheduleId))
                header = new Header(token, scheduleId, meta.Cell(3, 2).GetString(),
                    (short)meta.Cell(4, 2).GetValue<int>(), layout, KindSchedule);
            else
                return new ReadResult(null, [], NotOurs);

            if (!ReadsLayout(layout))
                return new ReadResult(header, [], "This workbook uses a layout this GECKO does not know. Download a fresh template.");

            if (!book.TryGetWorksheet(RatesSheet, out var sheet))
                return new ReadResult(header, [], $"The '{RatesSheet}' sheet is missing.");

            var headerRow = HeaderRow(layout);
            for (var c = 0; c < Columns.Length; c++)
                if (!string.Equals(sheet.Cell(headerRow, c + 1).GetString().Trim(), Columns[c], StringComparison.OrdinalIgnoreCase))
                    return new ReadResult(header, [], $"Column {c + 1} should be '{Columns[c]}'. Do not rename, move or remove columns.");

            var rows = new List<ReadRow>();
            var last = sheet.LastRowUsed()?.RowNumber() ?? headerRow;
            for (var r = headerRow + 1; r <= last; r++)
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
