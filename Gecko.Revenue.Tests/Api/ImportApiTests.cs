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

    /// <summary>Decision 6: no blank templates, and a workbook belongs to the tariff it was downloaded for.</summary>
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

    /// <summary>The values of one drop-down, as the Lists sheet carries them.</summary>
    private static List<string> ListValues(XLWorkbook book, string column)
    {
        var lists = book.Worksheet("Lists");
        var col = lists.Row(1).CellsUsed().Single(c => c.GetString() == column).Address.ColumnNumber;
        return lists.Column(col).CellsUsed().Skip(1).Select(c => c.GetString()).ToList();
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
            Assert.Equal("RateKey", sheet.Cell(1, 1).GetString());
            Assert.True(sheet.Cell(2, 1).Style.Protection.Locked);
            Assert.True(sheet.IsProtected);
            Assert.Equal(1, sheet.SheetView.SplitRow);
            Assert.True(sheet.AutoFilter.IsEnabled);
            Assert.Equal(XLWorksheetVisibility.Hidden, book.Worksheet("Lists").Visibility);
            Assert.All(Gecko.Revenue.Application.TariffWorkbook.Columns, c => Assert.True(sheet.Cell(1, Col(c)).HasComment, $"{c} has no note"));

            string[] dropDowns = ["ChargeCode", "BillTo", "PaymentTerm", "OrderType", "Movement", "EquipmentType", "Size",
                "CargoCategory", "TruckCategory", "BillingUnit", "PricingMethod", "TierBasis"];
            foreach (var column in dropDowns)
            {
                Assert.NotEmpty(ListValues(book, column));
                var validation = sheet.DataValidations.SingleOrDefault(v => v.Ranges.Any(r =>
                    r.FirstCell().Address.ColumnNumber == Col(column) && r.FirstCell().Address.RowNumber == 2
                    && r.LastCell().Address.RowNumber == 5000));
                Assert.True(validation is not null, $"no drop-down on {column}");
                Assert.Equal(XLAllowedValues.List, validation.AllowedValues);
                Assert.True(validation.IgnoreBlanks);
                Assert.Contains("Lists", validation.Value);
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
            Assert.True(sheet.Cell(2, Col("ChargeCode")).IsEmpty());

            string Pick(string column, string value)
            {
                Assert.Contains(value, ListValues(book, column));
                return value;
            }

            // Row 2: a lift-on for 40' LIN boxes. Row 3: a gate fee for one equipment type, cargo and truck category.
            sheet.Cell(2, Col("ChargeCode")).Value = Pick("ChargeCode", "LIFTIN");
            sheet.Cell(2, Col("BillTo")).Value = Pick("BillTo", "CUSTOMER");
            sheet.Cell(2, Col("PaymentTerm")).Value = Pick("PaymentTerm", "CASH");
            sheet.Cell(2, Col("OrderType")).Value = Pick("OrderType", "LIN");
            sheet.Cell(2, Col("Size")).Value = Pick("Size", "40");
            sheet.Cell(2, Col("Rate")).Value = 555;

            var equipment = ListValues(book, "EquipmentType")[0];
            sheet.Cell(3, Col("ChargeCode")).Value = Pick("ChargeCode", "GATEFEE");
            sheet.Cell(3, Col("BillTo")).Value = Pick("BillTo", "CUSTOMER");
            sheet.Cell(3, Col("PaymentTerm")).Value = Pick("PaymentTerm", "CASH");
            sheet.Cell(3, Col("CargoCategory")).Value = ListValues(book, "CargoCategory")[0];
            sheet.Cell(3, Col("TruckCategory")).Value = ListValues(book, "TruckCategory")[0];
            sheet.Cell(3, Col("EquipmentType")).Value = equipment;
            sheet.Cell(3, Col("Rate")).Value = 150;

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
}
