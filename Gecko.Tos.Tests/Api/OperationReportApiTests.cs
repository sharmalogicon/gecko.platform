using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Gecko.Data;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Vessels;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Vector's TMS.Operation.* reports (owner 2026-10-11) through the real host: each answers a PDF and an Excel sheet
/// with the RDL's headings and columns, and the box the test gated is a row with the values Vector would print.
///
/// Boxes are ZZTU numbers booked with a ZZO- carrier ref, gated through the real barrier, removed in finally.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class OperationReportApiTests(TosApiFactory api)
{
    private const string Reports = "/api/tos/reports";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewRef() => $"ZZO-{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static string NewBox()
    {
        var ten = $"ZZTU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    /// <summary>An EXP CY/CY booking on EASTPIONEER-069S (a call carrying ONEY) with one 20GP box, gated in full.</summary>
    private static async Task<VesselCallSummaryResponse> ExportGatedInAsync(HttpClient client, string carrierRef, string box, CancellationToken ct)
    {
        var call = (await client.GetFromJsonAsync<PagedResult<VesselCallSummaryResponse>>("/api/tos/vessel-calls?pageSize=200", ct))!
            .Items.Single(c => c.CallRef == "EASTPIONEER-069S");
        var booked = await client.PostAsJsonAsync("/api/tos/bookings", new
        {
            branchId = SctLcb01, orderTypeCode = "EXP CY/CY", lineCode = "ONEY", customerCode = "CUS-BKF", carrierRef,
            vesselCallId = call.VesselCallId, polPortCode = "THLCH", podPortCode = "SGSIN",
            validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
            containers = new object[] { new { containerNo = box } },
        }, ct);
        Assert.True(booked.StatusCode == HttpStatusCode.Created, $"booking returned {(int)booked.StatusCode}: {await booked.Content.ReadAsStringAsync(ct)}");
        // The empty pick-up (MTY_OUT, step 1) is taken as done: the box comes back full.
        using (var json = System.Text.Json.JsonDocument.Parse(await booked.Content.ReadAsStringAsync(ct)))
            await TestDatabase.MarkFirstStepDoneAsync(json.RootElement.GetProperty("containers")[0].GetProperty("bookingContainerId").GetGuid());

        var gated = await client.PostAsJsonAsync("/api/tos/gate/transactions", new
        {
            branchId = SctLcb01, containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT",
            tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 18000m, customsPermitNo = "ZZ-PERMIT-1",
            truck = new { plate = "70-4321", driverName = "Somchai P." }, grossWeightKg = 24100m, weightSource = "WEIGHBRIDGE",
            seals = new object[] { new { sealNo = $"ZZ-{box[^4..]}", sealType = "LINE", isIntact = true } },
            transactionAt = DateTimeOffset.UtcNow.AddHours(-2), lateOverrideReason = "ZZ test: the fixture call's cut-off has passed",
        }, ct);
        Assert.True(gated.StatusCode == HttpStatusCode.Created, $"gate in returned {(int)gated.StatusCode}: {await gated.Content.ReadAsStringAsync(ct)}");
        return call;
    }

    /// <summary>An IMP CY/CY booking (MAEU, CUS-TAE) with one 20GP box, gated in full two hours ago.</summary>
    private static async Task ImportGatedInAsync(HttpClient client, string carrierRef, string box, CancellationToken ct)
    {
        var booked = await client.PostAsJsonAsync("/api/tos/bookings", new
        {
            branchId = SctLcb01, orderTypeCode = "IMP CY/CY", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
            validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
            containers = new object[] { new { containerNo = box } },
        }, ct);
        Assert.True(booked.StatusCode == HttpStatusCode.Created, $"booking returned {(int)booked.StatusCode}: {await booked.Content.ReadAsStringAsync(ct)}");
        var gated = await client.PostAsJsonAsync("/api/tos/gate/transactions", new
        {
            branchId = SctLcb01, containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT",
            tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 18000m, customsPermitNo = "ZZ-PERMIT-1",
            truck = new { plate = "70-4321", driverName = "Somchai P." }, grossWeightKg = 24100m, weightSource = "WEIGHBRIDGE",
            seals = new object[] { new { sealNo = $"ZZ-{box[^4..]}", sealType = "LINE", isIntact = true } },
            transactionAt = DateTimeOffset.UtcNow.AddHours(-2),
        }, ct);
        Assert.True(gated.StatusCode == HttpStatusCode.Created, $"gate in returned {(int)gated.StatusCode}: {await gated.Content.ReadAsStringAsync(ct)}");
    }

    /// <summary>The depot day two hours ago (the gate time the helpers use).</summary>
    private static DateOnly GateDay => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7 - 2));

    /// <summary>Every used row of the first sheet, as formatted text.</summary>
    internal static async Task<List<string[]>> SheetAsync(HttpClient client, string url, CancellationToken ct)
    {
        var response = await client.GetAsync(url, ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
        var sheet = book.Worksheet(1);
        var width = sheet.LastColumnUsed()!.ColumnNumber();
        return Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
            .Select(r => Enumerable.Range(1, width).Select(c => sheet.Cell(r, c).GetFormattedString()).ToArray()).ToList();
    }

    internal static async Task PdfAsync(HttpClient client, string url, CancellationToken ct)
    {
        var response = await client.GetAsync(url, ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Outbound_storage_activity_lists_an_export_box_under_its_vessels_eta()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        try
        {
            var call = await ExportGatedInAsync(client, carrierRef, box, ct);
            var eta = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(call.Eta, TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok")).DateTime);
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
            var query = $"branchId={SctLcb01}&dateFrom={eta:yyyy-MM-dd}&dateTo={eta:yyyy-MM-dd}&agentCode=ONEY";

            var rows = await SheetAsync(client, $"{Reports}/outbound-container-storage-activity.xlsx?{query}", ct);
            Assert.Contains(rows, r => r[0] == "<< OUTBOUND CONTAINER STORAGE ACTIVITY >>");
            Assert.Contains(rows, r => r[..13].SequenceEqual(["ETD", "Container No", "Size", "Empty IN", "Empty Out", "Days", "Laden IN",
                "Laden Out", "Days", "Service", "Shipper", "Booking Agent", "Remarks"]));
            var row = Assert.Single(rows, r => r[1] == box);
            Assert.Equal(["20GP", "", "", "0", today.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture), "", "0", "EXP CY/CY"], row[2..10]);
            Assert.Equal(carrierRef, row[11]);
            Assert.Contains(rows, r => r[0] == "Service" && r.Contains("20GP"));

            // Another booking type selects nothing, as on Vector (its proc hard-codes EXPORT).
            Assert.DoesNotContain(await SheetAsync(client, $"{Reports}/outbound-container-storage-activity.xlsx?{query}&bookingType=IMPORT", ct),
                r => r[1] == box);
            await PdfAsync(client, $"{Reports}/outbound-container-storage-activity.pdf?{query}", ct);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.GetAsync($"{Reports}/outbound-container-storage-activity.pdf?branchId={SctLcb01}&dateFrom={eta:yyyy-MM-dd}", ct)).StatusCode);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    [Fact]
    public async Task Daily_import_full_in_counts_the_box_under_its_agent_day_and_cy_size()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        var day = GateDay;
        var url = $"{Reports}/daily-import-full-in.xlsx?branchId={SctLcb01}&dateFrom={day:yyyy-MM-dd}&dateTo={day:yyyy-MM-dd}&agentCode=MAEU";
        string Label() => day.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        static int[] Counts(string[]? row) => row is null ? new int[10] : row[1..11].Select(c => c.Length == 0 ? 0 : int.Parse(c)).ToArray();
        try
        {
            var before = Counts((await SheetAsync(client, url, ct)).FirstOrDefault(r => r[0] == Label()));
            await ImportGatedInAsync(client, carrierRef, box, ct);

            var rows = await SheetAsync(client, url, ct);
            Assert.Contains(rows, r => r[0] == "<<Daily Import FULL IN>>");
            Assert.Contains(rows, r => r[0] == "Date" && r[1] == "FULL IN" && r[2] == "CY" && r[5] == "CYD" && r[8] == "CFS");
            Assert.Contains(rows, r => r[0] == "MAEU" && r[2] == "20'" && r[10] == "45'");
            var after = Counts(rows.Single(r => r[0] == Label()));
            Assert.Equal([1, 1, 0, 0, 0, 0, 0, 0, 0, 0], after.Zip(before, (a, b) => a - b));
            Assert.Contains(rows, r => r[0] == "Total");
            await PdfAsync(client, url.Replace(".xlsx", ".pdf"), ct);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }
}
