using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Gecko.Tos.Endpoints.Vessels;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// PLAN 3.5: the schedule from Excel — preview writes nothing, confirm applies all
/// or none, the same file must be confirmed, a re-upload is UNCHANGED and a moved
/// ETD is an UPDATE. SCT's fixture vessel CHAOPHRAYA at Laem Chabang.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class VesselScheduleImportTests(TosApiFactory api)
{
    private const string Calls = "/api/tos/vessel-calls";

    [Fact]
    public async Task A_schedule_is_previewed_then_applied_all_or_nothing_and_a_re_upload_changes_only_what_moved()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var callRef = $"ZZI-{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var voyage = $"T{Random.Shared.Next(100000, 999999)}";
        var eta = DateTime.UtcNow.Date.AddDays(14).AddHours(6);
        try
        {
            var template = await client.GetAsync($"{Calls}/import/template", ct);
            Assert.Equal(HttpStatusCode.OK, template.StatusCode);
            Assert.Equal("vessel-schedule-template.xlsx", template.Content.Headers.ContentDisposition!.FileName?.Trim('"'));

            byte[] Sheet(DateTime etd, bool withBadCall) => Workbook(sheet =>
            {
                Row(sheet, 2, callRef, "CHAOPHRAYA", "THLCH", "LCB-A0", voyage, eta, etd, "MAEU", null, etd.AddHours(-30));
                Row(sheet, 3, callRef, "CHAOPHRAYA", "THLCH", "LCB-A0", voyage, eta, etd, "HLCU", "AGT-SEA", etd.AddHours(-30));
                if (withBadCall) Row(sheet, 4, callRef + "X", "NOSUCHVESSEL", "THLCH", null, voyage, eta, etd, "MAEU", null, null);
            });

            // ── one bad call spoils the file: previewed, refused on confirm
            var withError = await UploadAsync(client, Sheet(eta.AddDays(1), withBadCall: true), confirm: false, sha: null, ct);
            Assert.Equal((1, 1, false), (withError.Creates, withError.Errors, withError.Applied));
            Assert.Contains(withError.Calls.Single(c => c.Action == "ERROR").Issues, i => i.Contains("NOSUCHVESSEL"));
            Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(client, Sheet(eta.AddDays(1), withBadCall: true), true, withError.FileSha256, ct)).StatusCode);
            Assert.Equal(0, await CountAsync(client, callRef, ct));

            // ── the clean file: preview, a different file is refused, the previewed one applies
            var clean = Sheet(eta.AddDays(1), withBadCall: false);
            var preview = await UploadAsync(client, clean, confirm: false, sha: null, ct);
            var planned = Assert.Single(preview.Calls);
            Assert.Equal(("CREATE", callRef), (planned.Action, planned.CallRef));
            Assert.Equal(["MAEU", "HLCU"], planned.Lines);
            Assert.Equal(0, await CountAsync(client, callRef, ct));   // a preview writes nothing

            Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(client, clean, true, new string('0', 64), ct)).StatusCode);
            var applied = await UploadAsync(client, clean, confirm: true, sha: preview.FileSha256, ct);
            Assert.True(applied.Applied);
            Assert.Equal(1, await CountAsync(client, callRef, ct));

            // ── the same file again changes nothing; a delayed ETD is an update
            Assert.Equal("UNCHANGED", Assert.Single((await UploadAsync(client, clean, false, null, ct)).Calls).Action);
            var delayed = Sheet(eta.AddDays(2), withBadCall: false);
            var update = Assert.Single((await UploadAsync(client, delayed, false, null, ct)).Calls);
            Assert.Equal("UPDATE", update.Action);
            Assert.Contains(update.Changes, c => c.StartsWith("ETD", StringComparison.Ordinal));
        }
        finally { await TestDatabase.RemoveCallAsync(callRef); }
    }

    private static byte[] Workbook(Action<IXLWorksheet> fill)
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Calls");
        string[] headers = ["Call ref", "Vessel", "Port", "Terminal", "Voyage in", "Voyage out", "ETA", "ETB", "ETD",
            "Line", "Agent", "Line voyage in", "Line voyage out", "Service", "Port cut-off (dry)", "Yard cut-off (dry)", "Remarks"];
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        fill(sheet);
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    private static void Row(IXLWorksheet s, int r, string callRef, string vessel, string port, string? terminal, string voyage,
        DateTime eta, DateTime etd, string line, string? agent, DateTime? yardCutoff)
    {
        s.Cell(r, 1).Value = callRef; s.Cell(r, 2).Value = vessel; s.Cell(r, 3).Value = port; s.Cell(r, 4).Value = terminal;
        s.Cell(r, 6).Value = voyage; s.Cell(r, 7).Value = eta; s.Cell(r, 9).Value = etd; s.Cell(r, 10).Value = line;
        s.Cell(r, 11).Value = agent; s.Cell(r, 13).Value = voyage + line[0];
        if (yardCutoff is { } y) s.Cell(r, 16).Value = y;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, byte[] xlsx, bool confirm, string? sha, CancellationToken ct)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(xlsx);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(file, "file", "schedule.xlsx");
        return client.PostAsync($"{Calls}/import?confirm={confirm.ToString().ToLowerInvariant()}{(sha is null ? "" : $"&expectedSha256={sha}")}", form, ct);
    }

    private static async Task<ScheduleImportResponse> UploadAsync(HttpClient client, byte[] xlsx, bool confirm, string? sha, CancellationToken ct)
    {
        var response = await PostAsync(client, xlsx, confirm, sha, ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<ScheduleImportResponse>(ct))!;
    }

    private static async Task<int> CountAsync(HttpClient client, string callRef, CancellationToken ct)
    {
        var page = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{Calls}?search={callRef}&pageSize=5", ct);
        return page.GetProperty("totalCount").GetInt32();
    }
}
