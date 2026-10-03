using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Gecko.Revenue.Domain;
using Gecko.Revenue.Endpoints.Imports;
using Gecko.Revenue.Endpoints.Tariffs;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// ROADMAP 2.7 end to end: download → edit in Excel (ClosedXML stands in for the
/// user) → upload → preview → confirm. Each test edits its own draft tariff.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class ImportApiTests(RevenueApiFactory api)
{
    private const string Tariffs = "/api/revenue/tariffs";

    private static readonly object[] StartingRates =
    [
        new { chargeCode = "LIFTIN", billTo = "CUSTOMER", paymentTermCode = "CASH", orderTypeCode = "LIN", equipmentSize = "40", rate = 555 },
        new
        {
            chargeCode = "STORAGE", billTo = "CUSTOMER", paymentTermCode = "CASH", orderTypeCode = "LIN", equipmentSize = "20",
            pricingMethod = "TIERED_INCREMENTAL", tierBasis = "DAY",
            tiers = new object[] { new { fromQty = 1, toQty = 7, rate = 160 }, new { fromQty = 8, toQty = (int?)null, rate = 275 } },
            conditions = new object[] { new { axis = "IS_DG", op = "IS", flag = true, modifierOp = "MULTIPLY", modifierValue = 1.5, label = "DG x1.5" } },
        },
        new { chargeCode = "GATEFEE", billTo = "CUSTOMER", paymentTermCode = "CASH", rate = 150 },
        new { chargeCode = "SEALFEE", billTo = "CUSTOMER", paymentTermCode = "CASH", rate = 60 },
    ];

    private async Task<(HttpClient Client, ScheduleResponse Draft, string No)> DraftAsync(CancellationToken ct)
    {
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var no = $"ZZ-XL-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var created = await client.PostAsJsonAsync(Tariffs, new
        {
            scheduleNo = no, name = "Excel test", moduleCode = "TOS", scheduleType = "SPOT",
            bookingRef = $"BKG-{no}", customerPartyCode = "CUS-TAE", effectiveFrom = "2031-01-01",
        }, ct);
        var draft = (await created.Content.ReadFromJsonAsync<ScheduleResponse>(ct))!;
        var put = await client.PutAsJsonAsync($"{Tariffs}/{draft.ScheduleId}/rates", new { rowVersion = draft.RowVersion, rates = StartingRates }, ct);
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync(ct));
        return (client, (await client.GetFromJsonAsync<ScheduleResponse>($"{Tariffs}/{draft.ScheduleId}", ct))!, no);
    }

    private static async Task<XLWorkbook> DownloadAsync(HttpClient client, Guid scheduleId, CancellationToken ct)
    {
        var response = await client.GetAsync($"{Tariffs}/{scheduleId}/template", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
    }

    private static byte[] Save(XLWorkbook book)
    {
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid scheduleId, byte[] file, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(content, "file", "tariff.xlsx");
        return await client.PostAsync($"{Tariffs}/{scheduleId}/imports", form, ct);
    }

    private static async Task<ImportPreview> PreviewOf(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"upload returned {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<ImportPreview>(body, JsonSerializerOptions.Web)!;
    }

    private static int RowOf(IXLWorksheet sheet, string chargeCode) =>
        sheet.RowsUsed().Single(r => r.Cell(2).GetString() == chargeCode).RowNumber();

    [Fact]
    public async Task Download_edit_upload_preview_confirm()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, draft, no) = await DraftAsync(ct);
        try
        {
            using var book = await DownloadAsync(client, draft.ScheduleId, ct);

            // What the user receives.
            var meta = book.Worksheet("_gecko");
            Assert.Equal(XLWorksheetVisibility.VeryHidden, meta.Visibility);
            var sheet = book.Worksheet("Rates");
            Assert.True(sheet.IsProtected);
            Assert.Equal("1-7:160; 8+:275", sheet.Cell(RowOf(sheet, "STORAGE"), 16).GetString());
            Assert.Equal("DG x1.5", sheet.Cell(RowOf(sheet, "STORAGE"), 17).GetString());
            Assert.Equal("555", sheet.Cell(RowOf(sheet, "LIFTIN"), 15).GetFormattedString());

            // The user's edits: a price change, a deleted line, a new line, an untouched line.
            sheet.Cell(RowOf(sheet, "LIFTIN"), 15).Value = 600;
            sheet.Row(RowOf(sheet, "GATEFEE")).Delete();
            var newRow = sheet.LastRowUsed()!.RowNumber() + 1;
            sheet.Cell(newRow, 2).Value = "LIFTOUT";
            sheet.Cell(newRow, 3).Value = "customer";
            sheet.Cell(newRow, 4).Value = "CASH";
            sheet.Cell(newRow, 6).Value = "LOUT";
            sheet.Cell(newRow, 9).Value = "40";
            sheet.Cell(newRow, 15).Value = 500;
            var file = Save(book);

            var preview = await PreviewOf(await UploadAsync(client, draft.ScheduleId, file, ct), ct);
            Assert.Equal(("VALIDATED", 0), (preview.Status, preview.RowsError));
            Assert.Equal((1, 1, 1, 2), (preview.RowsInsert, preview.RowsUpdate, preview.RowsDelete, preview.RowsUnchanged));

            // Nothing is applied by a preview.
            var before = (await client.GetFromJsonAsync<RateSetResponse>($"{Tariffs}/{draft.ScheduleId}/rates", ct))!;
            Assert.Equal(555m, before.Rates.Single(r => r.ChargeCode == "LIFTIN").Rate);

            var confirm = await client.PostAsync($"/api/revenue/imports/{preview.ImportBatchId}/confirm", null, ct);
            Assert.True(confirm.IsSuccessStatusCode, await confirm.Content.ReadAsStringAsync(ct));

            var after = (await client.GetFromJsonAsync<RateSetResponse>($"{Tariffs}/{draft.ScheduleId}/rates", ct))!;
            Assert.Equal(["LIFTIN", "LIFTOUT", "SEALFEE", "STORAGE"], after.Rates.Select(r => r.ChargeCode).Order());
            Assert.Equal(600m, after.Rates.Single(r => r.ChargeCode == "LIFTIN").Rate);
            Assert.All(after.Rates, r => Assert.Equal("IMPORTED", r.Source));
            var storage = after.Rates.Single(r => r.ChargeCode == "STORAGE");
            Assert.Equal("DG x1.5", Assert.Single(storage.Conditions).Label);   // kept although Excel cannot edit it
            Assert.Equal([new TierItem(1, 7, 160), new TierItem(8, null, 275)], storage.Tiers);

            // The same file twice is a double click.
            var again = await UploadAsync(client, draft.ScheduleId, file, ct);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    [Fact]
    public async Task Every_bad_cell_is_reported_against_its_row_and_nothing_can_be_confirmed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, draft, no) = await DraftAsync(ct);
        try
        {
            using var book = await DownloadAsync(client, draft.ScheduleId, ct);
            var sheet = book.Worksheet("Rates");
            var lift = RowOf(sheet, "LIFTIN");
            var storage = RowOf(sheet, "STORAGE");
            var seal = RowOf(sheet, "SEALFEE");
            sheet.Unprotect();
            sheet.Cell(lift, 2).Value = "LIFTN";                                   // typo
            sheet.Cell(storage, 16).Value = "1-7=160; 8+:275";                     // bad tier text
            sheet.Cell(seal, 15).Value = "sixty";                                  // not a number
            sheet.Cell(seal, 1).Value = sheet.Cell(lift, 1).GetString();           // copied key

            var preview = await PreviewOf(await UploadAsync(client, draft.ScheduleId, Save(book), ct), ct);

            Assert.Equal(3, preview.RowsError);
            ImportRowView Row(int n) => preview.Rows.Single(r => r.Sheet == "Rates" && r.RowNo == n);
            Assert.Contains(Row(lift).Issues, i => i.Column == "ChargeCode" && i.Message.Contains("LIFTN"));
            Assert.Contains(Row(storage).Issues, i => i.Column == "Tiers");
            Assert.Contains(Row(seal).Issues, i => i.Column == "Rate" && i.Code == "NOT_A_NUMBER");
            Assert.Contains(Row(seal).Issues, i => i.Code == "COPIED_ROW" && i.Severity == "WARNING");   // a copied key = a new line, not an error

            var confirm = await client.PostAsync($"/api/revenue/imports/{preview.ImportBatchId}/confirm", null, ct);
            Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);

            var cancel = await client.PostAsync($"/api/revenue/imports/{preview.ImportBatchId}/cancel", null, ct);
            Assert.Equal("CANCELLED", (await cancel.Content.ReadFromJsonAsync<ImportPreview>(ct))!.Status);
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    /// <summary>Decision 6: only a workbook GECKO wrote, and a tariff's own workbook belongs to that tariff.</summary>
    [Fact]
    public async Task Only_a_workbook_downloaded_for_this_tariff_is_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, draft, no) = await DraftAsync(ct);
        var (_, other, otherNo) = await DraftAsync(ct);
        try
        {
            using var blank = new XLWorkbook();
            blank.AddWorksheet("Rates").Cell(1, 1).Value = "RateKey";
            var blankResponse = await UploadAsync(client, draft.ScheduleId, Save(blank), ct);
            Assert.Equal(HttpStatusCode.BadRequest, blankResponse.StatusCode);
            Assert.Contains("not downloaded from GECKO", await blankResponse.Content.ReadAsStringAsync(ct));

            using var foreign = await DownloadAsync(client, other.ScheduleId, ct);
            var foreignResponse = await UploadAsync(client, draft.ScheduleId, Save(foreign), ct);
            Assert.Equal(HttpStatusCode.BadRequest, foreignResponse.StatusCode);
            Assert.Contains(otherNo, await foreignResponse.Content.ReadAsStringAsync(ct));

            var notExcel = await UploadAsync(client, draft.ScheduleId, "RateKey,ChargeCode\n"u8.ToArray(), ct);
            Assert.Equal(HttpStatusCode.BadRequest, notExcel.StatusCode);
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
            await TestDatabase.RemoveTariffAsync(otherNo);
        }
    }

    [Fact]
    public async Task A_tariff_that_moved_is_flagged_on_upload_and_refused_on_confirm()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, draft, no) = await DraftAsync(ct);
        async Task<ScheduleResponse> Touch(ScheduleResponse s)
        {
            var put = await client.PutAsJsonAsync($"{Tariffs}/{s.ScheduleId}/rates", new { rowVersion = s.RowVersion, rates = StartingRates }, ct);
            Assert.True(put.IsSuccessStatusCode);
            return (await client.GetFromJsonAsync<ScheduleResponse>($"{Tariffs}/{s.ScheduleId}", ct))!;
        }
        try
        {
            using var book = await DownloadAsync(client, draft.ScheduleId, ct);
            var current = await Touch(draft);        // someone edits after the download

            var preview = await PreviewOf(await UploadAsync(client, draft.ScheduleId, Save(book), ct), ct);
            Assert.True(preview.ScheduleChangedSinceExport);
            Assert.Contains(preview.Rows.SelectMany(r => r.Issues), i => i.Code == "SCHEDULE_CHANGED" && i.Severity == "WARNING");
            // The keys in the old workbook no longer exist after a replace: they are errors, not silent inserts.
            Assert.Contains(preview.Rows.SelectMany(r => r.Issues), i => i.Code == "UNKNOWN_RATE_KEY");

            using var fresh = await DownloadAsync(client, draft.ScheduleId, ct);
            var ok = await PreviewOf(await UploadAsync(client, draft.ScheduleId, Save(fresh), ct), ct);
            Assert.Equal((0, 4), (ok.RowsError, ok.RowsUnchanged));

            await Touch(current);                   // …and again, between preview and confirm
            var confirm = await client.PostAsync($"/api/revenue/imports/{ok.ImportBatchId}/confirm", null, ct);
            Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
            Assert.Contains("edited after this upload", await confirm.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    [Fact]
    public async Task An_approved_tariff_can_be_downloaded_but_not_imported_into()
    {
        var ct = TestContext.Current.CancellationToken;
        var (maker, draft, no) = await DraftAsync(ct);
        var checker = await api.ClientForAsync(RevenueApiFactory.SctOwner);
        try
        {
            var submitted = await (await maker.PostAsJsonAsync($"{Tariffs}/{draft.ScheduleId}/submit", new { rowVersion = draft.RowVersion }, ct))
                .Content.ReadFromJsonAsync<ScheduleResponse>(ct);
            var approve = await checker.PostAsJsonAsync($"{Tariffs}/{draft.ScheduleId}/approve", new { rowVersion = submitted!.RowVersion }, ct);
            Assert.True(approve.IsSuccessStatusCode, await approve.Content.ReadAsStringAsync(ct));

            using var book = await DownloadAsync(maker, draft.ScheduleId, ct);
            var response = await UploadAsync(maker, draft.ScheduleId, Save(book), ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("revision", await response.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    // ── tier text ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1-7:160; 8-14:275; 15+:390", 3)]
    [InlineData(" 1 - 7 : 160 ;8+:1,275.50 ", 2)]
    [InlineData("", 0)]
    public void Tier_text_parses(string text, int count)
    {
        var (tiers, error) = TierText.Parse(text);
        Assert.Null(error);
        Assert.Equal(count, tiers.Count);
    }

    [Theory]
    [InlineData("1-7=160")]
    [InlineData("1-7:abc")]
    [InlineData("seven:160")]
    [InlineData("1-2-3:160")]
    public void Tier_text_says_what_is_wrong(string text) => Assert.NotNull(TierText.Parse(text).Error);

    [Fact]
    public void Tier_text_round_trips()
    {
        Tier[] tiers = [new(1, 7, 160), new(8, 14, 275.5m), new(15, null, 390)];
        Assert.Equal("1-7:160; 8-14:275.5; 15+:390", TierText.Format(tiers));
        Assert.Equal(tiers, TierText.Parse(TierText.Format(tiers)).Tiers);
    }

    // ── the depot clerk's workflow: blank draft, drop-downs, copied rows ────

    private async Task<(HttpClient Client, ScheduleResponse Draft, string No)> EmptyDraftAsync(CancellationToken ct)
    {
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var no = $"ZZ-XB-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var created = await client.PostAsJsonAsync(Tariffs, new
        {
            scheduleNo = no, name = "Blank quotation", moduleCode = "TOS", scheduleType = "SPOT",
            bookingRef = $"BKG-{no}", customerPartyCode = "CUS-TAE", effectiveFrom = "2031-01-01",
        }, ct);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(ct));
        return (client, (await created.Content.ReadFromJsonAsync<ScheduleResponse>(ct))!, no);
    }

    /// <summary>The sheet a drop-down reads: a visible reference sheet, or the hidden Lists sheet.</summary>
    private static string ListSheet(string column) =>
        Gecko.Revenue.Application.TariffWorkbook.ReferenceSheets.SingleOrDefault(r => r.Column == column).Sheet ?? "Lists";

    /// <summary>The values of one drop-down, as its sheet carries them.</summary>
    private static List<string> ListValues(XLWorkbook book, string column)
    {
        var sheet = book.Worksheet(ListSheet(column));
        var col = sheet.Name == "Lists" ? sheet.Row(1).CellsUsed().Single(c => c.GetString() == column).Address.ColumnNumber : 1;
        return sheet.Column(col).CellsUsed().Skip(1).Select(c => c.GetString()).ToList();
    }

    private static int Col(string name) => Array.IndexOf(Gecko.Revenue.Application.TariffWorkbook.Columns, name) + 1;

    [Fact]
    public async Task The_template_hides_the_key_and_offers_a_drop_down_for_every_code_column()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, draft, no) = await EmptyDraftAsync(ct);
        try
        {
            using var book = await DownloadAsync(client, draft.ScheduleId, ct);
            var sheet = book.Worksheet("Rates");

            Assert.True(sheet.Column(1).IsHidden);
            Assert.Equal("RateKey", sheet.Cell(2, 1).GetString());                 // layout 2: the note is row 1
            Assert.Contains("1-7:160; 8-14:275; 15+:390", sheet.Cell(1, 2).GetString());
            Assert.True(sheet.Cell(3, 1).Style.Protection.Locked);
            Assert.True(sheet.IsProtected);
            Assert.Equal(2, sheet.SheetView.SplitRow);
            Assert.True(sheet.AutoFilter.IsEnabled);
            Assert.Equal(XLWorksheetVisibility.Hidden, book.Worksheet("Lists").Visibility);
            Assert.All(Gecko.Revenue.Application.TariffWorkbook.Columns, c => Assert.True(sheet.Cell(2, Col(c)).HasComment, $"{c} has no note"));

            string[] dropDowns = ["ChargeCode", "BillTo", "PaymentTerm", "OrderType", "Movement", "EquipmentType", "Size",
                "CargoCategory", "TruckCategory", "BillingUnit", "PricingMethod", "TierBasis"];
            foreach (var column in dropDowns)
            {
                Assert.NotEmpty(ListValues(book, column));
                var validation = sheet.DataValidations.SingleOrDefault(v => v.Ranges.Any(r =>
                    r.FirstCell().Address.ColumnNumber == Col(column) && r.FirstCell().Address.RowNumber == 3
                    && r.LastCell().Address.RowNumber == 5000));
                Assert.True(validation is not null, $"no drop-down on {column}");
                Assert.Equal(XLAllowedValues.List, validation.AllowedValues);
                Assert.True(validation.IgnoreBlanks);
                Assert.Contains(ListSheet(column), validation.Value);
            }
            Assert.Contains("LIN", ListValues(book, "OrderType"));
            Assert.Contains("40", ListValues(book, "Size"));
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    [Fact]
    public async Task A_blank_draft_is_filled_from_the_drop_downs_and_applied()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, draft, no) = await EmptyDraftAsync(ct);
        try
        {
            using var book = await DownloadAsync(client, draft.ScheduleId, ct);
            var sheet = book.Worksheet("Rates");
            Assert.True(sheet.Cell(3, Col("ChargeCode")).IsEmpty());

            string Pick(string column, string value)
            {
                Assert.Contains(value, ListValues(book, column));
                return value;
            }

            // Row 3: a lift-on for 40' LIN boxes. Row 4: a gate fee for one equipment type, cargo and truck category.
            sheet.Cell(3, Col("ChargeCode")).Value = Pick("ChargeCode", "LIFTIN");
            sheet.Cell(3, Col("BillTo")).Value = Pick("BillTo", "CUSTOMER");
            sheet.Cell(3, Col("PaymentTerm")).Value = Pick("PaymentTerm", "CASH");
            sheet.Cell(3, Col("OrderType")).Value = Pick("OrderType", "LIN");
            sheet.Cell(3, Col("Size")).Value = Pick("Size", "40");
            sheet.Cell(3, Col("Rate")).Value = 555;

            var equipment = ListValues(book, "EquipmentType")[0];
            sheet.Cell(4, Col("ChargeCode")).Value = Pick("ChargeCode", "GATEFEE");
            sheet.Cell(4, Col("BillTo")).Value = Pick("BillTo", "CUSTOMER");
            sheet.Cell(4, Col("PaymentTerm")).Value = Pick("PaymentTerm", "CASH");
            sheet.Cell(4, Col("CargoCategory")).Value = ListValues(book, "CargoCategory")[0];
            sheet.Cell(4, Col("TruckCategory")).Value = ListValues(book, "TruckCategory")[0];
            sheet.Cell(4, Col("EquipmentType")).Value = equipment;
            sheet.Cell(4, Col("Rate")).Value = 150;

            var preview = await PreviewOf(await UploadAsync(client, draft.ScheduleId, Save(book), ct), ct);
            Assert.True(preview.RowsError == 0, string.Join("; ", preview.Rows.SelectMany(r => r.Issues).Select(i => $"{i.Column}: {i.Message}")));
            Assert.Equal((2, 0, 0, 0), (preview.RowsInsert, preview.RowsUpdate, preview.RowsDelete, preview.RowsUnchanged));
            Assert.All(preview.Rows, r => Assert.Equal("INSERT", r.Action));

            var confirm = await client.PostAsync($"/api/revenue/imports/{preview.ImportBatchId}/confirm", null, ct);
            Assert.True(confirm.IsSuccessStatusCode, await confirm.Content.ReadAsStringAsync(ct));

            var after = (await client.GetFromJsonAsync<RateSetResponse>($"{Tariffs}/{draft.ScheduleId}/rates", ct))!;
            Assert.Equal(["GATEFEE", "LIFTIN"], after.Rates.Select(r => r.ChargeCode).Order());
            Assert.Equal(555m, after.Rates.Single(r => r.ChargeCode == "LIFTIN").Rate);
            Assert.Equal(equipment, after.Rates.Single(r => r.ChargeCode == "GATEFEE").EquipmentTypeCode);
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    [Fact]
    public async Task A_copied_row_keeps_the_original_as_the_edit_and_is_added_as_a_new_rate()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, draft, no) = await DraftAsync(ct);
        try
        {
            using var book = await DownloadAsync(client, draft.ScheduleId, ct);
            var sheet = book.Worksheet("Rates");
            var lift = RowOf(sheet, "LIFTIN");
            var copy = sheet.LastRowUsed()!.RowNumber() + 1;
            for (var c = 1; c <= Gecko.Revenue.Application.TariffWorkbook.Columns.Length; c++)   // hidden key and all
                sheet.Cell(copy, c).Value = sheet.Cell(lift, c).Value;
            Assert.Equal(sheet.Cell(lift, 1).GetString(), sheet.Cell(copy, 1).GetString());
            sheet.Cell(copy, Col("Size")).Value = "20";
            sheet.Cell(copy, Col("Rate")).Value = 450;

            var preview = await PreviewOf(await UploadAsync(client, draft.ScheduleId, Save(book), ct), ct);
            Assert.Equal(0, preview.RowsError);
            Assert.Equal((1, 0, 0, 4), (preview.RowsInsert, preview.RowsUpdate, preview.RowsDelete, preview.RowsUnchanged));
            ImportRowView Row(int n) => preview.Rows.Single(r => r.Sheet == "Rates" && r.RowNo == n);
            Assert.Equal("UNCHANGED", Row(lift).Action);
            Assert.Equal("INSERT", Row(copy).Action);
            var note = Assert.Single(Row(copy).Issues);
            Assert.Equal(("COPIED_ROW", "WARNING"), (note.Code, note.Severity));

            var confirm = await client.PostAsync($"/api/revenue/imports/{preview.ImportBatchId}/confirm", null, ct);
            Assert.True(confirm.IsSuccessStatusCode, await confirm.Content.ReadAsStringAsync(ct));
            var lifts = (await client.GetFromJsonAsync<RateSetResponse>($"{Tariffs}/{draft.ScheduleId}/rates", ct))!
                .Rates.Where(r => r.ChargeCode == "LIFTIN").ToList();
            Assert.Equal([450m, 555m], lifts.Select(r => r.Rate!.Value).Order());
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    // ── the blank template (gecko_revenue 22): download before any draft, upload merges ──

    private static async Task<XLWorkbook> DownloadBlankAsync(HttpClient client, CancellationToken ct)
    {
        var response = await client.GetAsync($"{Tariffs}/template", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Gecko-Tariff-Template.xlsx", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        return new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
    }

    /// <summary>Writes one line under the headers of a layout-2 Rates sheet; returns its row.</summary>
    private static int AddLine(IXLWorksheet sheet, params (string Column, object Value)[] cells)
    {
        var row = Math.Max(sheet.LastRowUsed()!.RowNumber(), 2) + 1;
        foreach (var (column, value) in cells)
            sheet.Cell(row, Col(column)).Value = value switch { int i => i, decimal d => d, var v => v.ToString() };
        return row;
    }

    [Fact]
    public async Task The_blank_template_is_the_same_workbook_with_no_rows_no_token_and_described_codes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, draft, no) = await EmptyDraftAsync(ct);
        try
        {
            using var blank = await DownloadBlankAsync(client, ct);
            using var own = await DownloadAsync(client, draft.ScheduleId, ct);

            // The same Rates sheet: same headers in the same order, the key hidden, no data rows.
            var sheet = blank.Worksheet("Rates");
            var columns = Gecko.Revenue.Application.TariffWorkbook.Columns;
            Assert.Equal(columns, Enumerable.Range(1, columns.Length).Select(c => sheet.Cell(2, c).GetString()));
            Assert.Equal(columns, Enumerable.Range(1, columns.Length).Select(c => own.Worksheet("Rates").Cell(2, c).GetString()));
            Assert.True(sheet.Column(1).IsHidden);
            Assert.All(Enumerable.Range(1, columns.Length), c => Assert.True(sheet.Cell(3, c).IsEmpty()));
            var note = sheet.Cell(1, 2).GetString();
            Assert.Contains("1-7:160; 8-14:275; 15+:390", note);
            Assert.Contains("RateKey", note);

            // No token, no schedule: the _gecko sheet says BLANK.
            var meta = blank.Worksheet("_gecko");
            Assert.Equal(XLWorksheetVisibility.VeryHidden, meta.Visibility);
            Assert.Equal(("", "", "2", "BLANK"), (meta.Cell(1, 2).GetString(), meta.Cell(2, 2).GetString(), meta.Cell(5, 2).GetString(), meta.Cell(6, 2).GetString()));
            Assert.Equal("SCHEDULE", own.Worksheet("_gecko").Cell(6, 2).GetString());

            // Visible reference sheets, code + description, in both templates; the drop-downs read them.
            foreach (var (column, name) in Gecko.Revenue.Application.TariffWorkbook.ReferenceSheets)
            {
                foreach (var book in new[] { blank, own })
                {
                    var reference = book.Worksheet(name);
                    Assert.Equal(XLWorksheetVisibility.Visible, reference.Visibility);
                    Assert.Equal(("Code", "Description"), (reference.Cell(1, 1).GetString(), reference.Cell(1, 2).GetString()));
                }
                Assert.Equal(ListValues(own, column), ListValues(blank, column));
            }
            var charges = blank.Worksheet("Charge codes");
            var liftIn = charges.Column(1).CellsUsed().Single(c => c.GetString() == "LIFTIN");
            Assert.False(string.IsNullOrWhiteSpace(charges.Cell(liftIn.Address.RowNumber, 2).GetString()));
            Assert.Contains(sheet.DataValidations, v => v.Ranges.Any(r => r.FirstCell().Address.ColumnNumber == Col("ChargeCode"))
                                                        && v.Value.Contains("Charge codes"));

            // Downloading a blank template records nothing.
            await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
            Assert.Equal(1, db.TemplateExports.Count(e => e.ScheduleId == draft.ScheduleId));   // the tariff's own download only
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    [Fact]
    public async Task The_blank_template_needs_the_import_permission()
    {
        var ct = TestContext.Current.CancellationToken;
        var ops = await api.ClientForAsync(RevenueApiFactory.SctOpsLcb);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync($"{Tariffs}/template", ct)).StatusCode);
    }

    /// <summary>The UI's flow: download blank → fill offline → save a draft header → upload → all INSERT → apply.</summary>
    [Fact]
    public async Task A_blank_template_fills_an_empty_draft()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        using var book = await DownloadBlankAsync(client, ct);
        var sheet = book.Worksheet("Rates");
        AddLine(sheet, ("ChargeCode", "LIFTIN"), ("BillTo", "CUSTOMER"), ("PaymentTerm", "CASH"), ("OrderType", "LIN"), ("Size", "40"), ("Rate", 555));
        AddLine(sheet, ("ChargeCode", "STORAGE"), ("BillTo", "CUSTOMER"), ("PaymentTerm", "CASH"), ("PricingMethod", "TIERED_INCREMENTAL"),
            ("TierBasis", "DAY"), ("BillingUnit", "PER_DAY"), ("Tiers", "1-7:160; 8-14:275; 15+:390"));
        var file = Save(book);

        var (_, draft, no) = await EmptyDraftAsync(ct);   // the draft is created after the file was filled
        try
        {
            var preview = await PreviewOf(await UploadAsync(client, draft.ScheduleId, file, ct), ct);
            Assert.True(preview.RowsError == 0, string.Join("; ", preview.Rows.SelectMany(r => r.Issues).Select(i => $"{i.Column}: {i.Message}")));
            Assert.False(preview.ScheduleChangedSinceExport);
            Assert.Equal((2, 0, 0, 0), (preview.RowsInsert, preview.RowsUpdate, preview.RowsDelete, preview.RowsUnchanged));
            Assert.All(preview.Rows, r => Assert.Equal("INSERT", r.Action));

            var confirm = await client.PostAsync($"/api/revenue/imports/{preview.ImportBatchId}/confirm", null, ct);
            Assert.True(confirm.IsSuccessStatusCode, await confirm.Content.ReadAsStringAsync(ct));
            var after = (await client.GetFromJsonAsync<RateSetResponse>($"{Tariffs}/{draft.ScheduleId}/rates", ct))!;
            Assert.Equal(["LIFTIN", "STORAGE"], after.Rates.Select(r => r.ChargeCode).Order());
            Assert.Equal(3, after.Rates.Single(r => r.ChargeCode == "STORAGE").Tiers.Count);

            await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
            Assert.Null(db.ImportBatches.Single(b => b.ImportBatchId == preview.ImportBatchId).TemplateExportId);
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    [Fact]
    public async Task A_blank_template_updates_matching_prices_inserts_new_lines_and_removes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, draft, no) = await DraftAsync(ct);
        try
        {
            var before = (await client.GetFromJsonAsync<RateSetResponse>($"{Tariffs}/{draft.ScheduleId}/rates", ct))!;
            using var book = await DownloadBlankAsync(client, ct);
            var sheet = book.Worksheet("Rates");
            var lift = AddLine(sheet, ("ChargeCode", "LIFTIN"), ("BillTo", "CUSTOMER"), ("PaymentTerm", "CASH"), ("OrderType", "LIN"),
                ("Size", "40"), ("Rate", 600));                                                      // 555 → 600
            var storage = AddLine(sheet, ("ChargeCode", "STORAGE"), ("BillTo", "CUSTOMER"), ("PaymentTerm", "CASH"), ("OrderType", "LIN"),
                ("Size", "20"), ("PricingMethod", "TIERED_INCREMENTAL"), ("TierBasis", "DAY"), ("BillingUnit", "PER_DAY"),
                ("Tiers", "1-7:170; 8+:290"));                                                      // new tiers
            var gate = AddLine(sheet, ("ChargeCode", "GATEFEE"), ("BillTo", "CUSTOMER"), ("PaymentTerm", "CASH"), ("Rate", 150));   // same price
            var liftOut = AddLine(sheet, ("ChargeCode", "LIFTOUT"), ("BillTo", "CUSTOMER"), ("PaymentTerm", "CASH"), ("OrderType", "LOUT"),
                ("Size", "40"), ("Rate", 500));                                                     // no match
            var anySize = AddLine(sheet, ("ChargeCode", "LIFTIN"), ("BillTo", "CUSTOMER"), ("PaymentTerm", "CASH"), ("OrderType", "LIN"),
                ("Rate", 400));                                                                     // blank Size = any: not the 40' line
            // SEALFEE is not in the file.

            var preview = await PreviewOf(await UploadAsync(client, draft.ScheduleId, Save(book), ct), ct);
            Assert.True(preview.RowsError == 0, string.Join("; ", preview.Rows.SelectMany(r => r.Issues).Select(i => $"{i.Column}: {i.Message}")));
            Assert.False(preview.ScheduleChangedSinceExport);
            Assert.Equal((2, 2, 0, 1), (preview.RowsInsert, preview.RowsUpdate, preview.RowsDelete, preview.RowsUnchanged));
            Assert.Equal(0, preview.RowsWarning);                                                 // a price change is INFO, not a warning
            Assert.DoesNotContain(preview.Rows, r => r.Sheet != "Rates");                         // nothing listed for removal

            ImportRowView Row(int n) => preview.Rows.Single(r => r.Sheet == "Rates" && r.RowNo == n);
            Guid Id(string charge, string? size) => before.Rates.Single(r => r.ChargeCode == charge && r.EquipmentSize == size).TosRateId;
            Assert.Equal(("UPDATE", "OK", Id("LIFTIN", "40")), (Row(lift).Action, Row(lift).Status, Row(lift).RateKey));
            var change = Assert.Single(Row(lift).Issues);
            Assert.Equal(("INFO", "PRICE_CHANGE", "Rate", "555 → 600"), (change.Severity, change.Code, change.Column, change.Message));
            Assert.Equal("UPDATE", Row(storage).Action);
            Assert.Equal("TIERED_INCREMENTAL by DAY 1-7:160; 8+:275 → TIERED_INCREMENTAL by DAY 1-7:170; 8+:290", Assert.Single(Row(storage).Issues).Message);
            Assert.Equal(("UNCHANGED", Id("GATEFEE", null)), (Row(gate).Action, Row(gate).RateKey));
            Assert.Equal(("INSERT", (Guid?)null), (Row(liftOut).Action, Row(liftOut).RateKey));
            Assert.Equal("INSERT", Row(anySize).Action);

            var confirm = await client.PostAsync($"/api/revenue/imports/{preview.ImportBatchId}/confirm", null, ct);
            Assert.True(confirm.IsSuccessStatusCode, await confirm.Content.ReadAsStringAsync(ct));

            var after = (await client.GetFromJsonAsync<RateSetResponse>($"{Tariffs}/{draft.ScheduleId}/rates", ct))!;
            Assert.Equal(["GATEFEE", "LIFTIN", "LIFTIN", "LIFTOUT", "SEALFEE", "STORAGE"], after.Rates.Select(r => r.ChargeCode).Order());
            RateResponse After(Guid id) => after.Rates.Single(r => r.TosRateId == id);

            // updated IN PLACE: same rate, new price, marked imported
            Assert.Equal((600m, "IMPORTED"), (After(Id("LIFTIN", "40")).Rate, After(Id("LIFTIN", "40")).Source));
            var tiers = After(Id("STORAGE", "20"));
            Assert.Equal([new TierItem(1, 7, 170), new TierItem(8, null, 290)], tiers.Tiers);
            Assert.Equal("DG x1.5", Assert.Single(tiers.Conditions).Label);                     // surcharges are not the price
            // untouched: the unchanged line and the line not in the file
            Assert.Equal(("MANUAL", 150m), (After(Id("GATEFEE", null)).Source, After(Id("GATEFEE", null)).Rate));
            Assert.Equal(("MANUAL", 60m), (After(Id("SEALFEE", null)).Source, After(Id("SEALFEE", null)).Rate));
            Assert.Equal(400m, after.Rates.Single(r => r.ChargeCode == "LIFTIN" && r.EquipmentSize is null).Rate);
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    [Fact]
    public async Task A_blank_template_keeps_credit_days_and_ignores_a_pasted_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, draft, no) = await DraftAsync(ct);
        try
        {
            var before = (await client.GetFromJsonAsync<RateSetResponse>($"{Tariffs}/{draft.ScheduleId}/rates", ct))!;
            using var book = await DownloadBlankAsync(client, ct);
            var sheet = book.Worksheet("Rates");
            sheet.Unprotect();
            var seal = AddLine(sheet, ("RateKey", before.Rates.Single(r => r.ChargeCode == "GATEFEE").TosRateId.ToString()),
                ("ChargeCode", "SEALFEE"), ("BillTo", "CUSTOMER"), ("PaymentTerm", "CASH"), ("CreditDays", 30), ("Rate", 70));

            var preview = await PreviewOf(await UploadAsync(client, draft.ScheduleId, Save(book), ct), ct);
            var row = preview.Rows.Single(r => r.RowNo == seal);
            Assert.Equal(("UPDATE", "WARNING"), (row.Action, row.Status));
            Assert.Equal(before.Rates.Single(r => r.ChargeCode == "SEALFEE").TosRateId, row.RateKey);   // matched on codes, not the key
            Assert.Equal(["CREDIT_DAYS_KEPT", "PRICE_CHANGE", "RATE_KEY_IGNORED"], row.Issues.Select(i => i.Code).Order());

            var confirm = await client.PostAsync($"/api/revenue/imports/{preview.ImportBatchId}/confirm", null, ct);
            Assert.True(confirm.IsSuccessStatusCode, await confirm.Content.ReadAsStringAsync(ct));
            var sealFee = (await client.GetFromJsonAsync<RateSetResponse>($"{Tariffs}/{draft.ScheduleId}/rates", ct))!
                .Rates.Single(r => r.ChargeCode == "SEALFEE");
            Assert.Equal((70m, (short?)null), (sealFee.Rate, sealFee.CreditTermDays));
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    [Fact]
    public async Task A_blank_template_with_the_same_line_twice_is_an_error()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, draft, no) = await EmptyDraftAsync(ct);
        try
        {
            using var book = await DownloadBlankAsync(client, ct);
            var sheet = book.Worksheet("Rates");
            AddLine(sheet, ("ChargeCode", "GATEFEE"), ("BillTo", "CUSTOMER"), ("PaymentTerm", "CASH"), ("Rate", 150));
            var twice = AddLine(sheet, ("ChargeCode", "GATEFEE"), ("BillTo", "CUSTOMER"), ("PaymentTerm", "CASH"), ("Rate", 160));

            var preview = await PreviewOf(await UploadAsync(client, draft.ScheduleId, Save(book), ct), ct);
            Assert.Equal(1, preview.RowsError);
            Assert.Contains(preview.Rows.Single(r => r.RowNo == twice).Issues, i => i.Message.Contains("billing unit"));
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    /// <summary>gecko_revenue 22: the billing unit is part of a rate's identity, and the pricer breaks the tie on it.</summary>
    [Fact]
    public async Task Two_lines_may_differ_by_billing_unit_alone_and_the_charges_own_unit_prices()
    {
        var ct = TestContext.Current.CancellationToken;
        var (maker, draft, no) = await EmptyDraftAsync(ct);
        var checker = await api.ClientForAsync(RevenueApiFactory.SctOwner);
        try
        {
            using var book = await DownloadBlankAsync(maker, ct);
            var sheet = book.Worksheet("Rates");
            AddLine(sheet, ("ChargeCode", "LIFTIN"), ("BillTo", "CUSTOMER"), ("PaymentTerm", "CASH"), ("OrderType", "LIN"), ("Size", "40"),
                ("BillingUnit", "PER_TEU"), ("Rate", 300));
            AddLine(sheet, ("ChargeCode", "LIFTIN"), ("BillTo", "CUSTOMER"), ("PaymentTerm", "CASH"), ("OrderType", "LIN"), ("Size", "40"),
                ("Rate", 555));                                                                     // blank unit = LIFTIN's own, PER_CONTAINER
            var preview = await PreviewOf(await UploadAsync(maker, draft.ScheduleId, Save(book), ct), ct);
            Assert.True(preview.RowsError == 0, string.Join("; ", preview.Rows.SelectMany(r => r.Issues).Select(i => $"{i.Column}: {i.Message}")));
            Assert.True((await maker.PostAsync($"/api/revenue/imports/{preview.ImportBatchId}/confirm", null, ct)).IsSuccessStatusCode);

            var current = (await maker.GetFromJsonAsync<ScheduleResponse>($"{Tariffs}/{draft.ScheduleId}", ct))!;
            var submitted = await (await maker.PostAsJsonAsync($"{Tariffs}/{draft.ScheduleId}/submit", new { rowVersion = current.RowVersion }, ct))
                .Content.ReadFromJsonAsync<ScheduleResponse>(ct);
            var approve = await checker.PostAsJsonAsync($"{Tariffs}/{draft.ScheduleId}/approve", new { rowVersion = submitted!.RowVersion }, ct);
            Assert.True(approve.IsSuccessStatusCode, await approve.Content.ReadAsStringAsync(ct));

            var price = await maker.PostAsJsonAsync("/api/revenue/price", new Gecko.Revenue.Contracts.PriceRequest(
                "TOS", null, new DateTimeOffset(2031, 1, 5, 3, 0, 0, TimeSpan.Zero), "LIFTIN", "CUSTOMER", "CASH",
                CustomerPartyCode: "CUS-TAE", BookingRef: $"BKG-{no}", OrderTypeCode: "LIN", EquipmentSize: "40"), ct);
            var body = await price.Content.ReadAsStringAsync(ct);
            Assert.True(price.IsSuccessStatusCode, body);
            var result = JsonSerializer.Deserialize<Gecko.Revenue.Contracts.PriceResult>(body, JsonSerializerOptions.Web)!;
            Assert.Equal((draft.ScheduleId, "PER_CONTAINER", 555m), (result.ScheduleId!.Value, result.BillingUnitCode!, result.Amount!.Value));
            Assert.Contains(result.PrecedenceTrail, t => t.Contains("2 lines tie") && t.Contains("PER_CONTAINER is the charge's own unit"));
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    /// <summary>A workbook downloaded before layout 2 (headers in row 1, no template_kind) still uploads.</summary>
    [Fact]
    public async Task A_layout_1_workbook_still_uploads()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, draft, no) = await DraftAsync(ct);
        try
        {
            using var book = await DownloadAsync(client, draft.ScheduleId, ct);
            var sheet = book.Worksheet("Rates");
            sheet.Unprotect();
            sheet.Row(1).Delete();                                                                  // headers back in row 1
            var meta = book.Worksheet("_gecko");
            meta.Unprotect();
            meta.Cell(5, 2).Value = 1;
            meta.Row(6).Delete();
            Assert.Equal("RateKey", sheet.Cell(1, 1).GetString());
            sheet.Cell(RowOf(sheet, "LIFTIN"), Col("Rate")).Value = 600;

            var preview = await PreviewOf(await UploadAsync(client, draft.ScheduleId, Save(book), ct), ct);
            Assert.Equal((0, 0, 1, 0, 3), (preview.RowsError, preview.RowsInsert, preview.RowsUpdate, preview.RowsDelete, preview.RowsUnchanged));
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }
}
