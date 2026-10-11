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
    private static async Task<VesselCallSummaryResponse> ExportGatedInAsync(HttpClient client, string carrierRef, string box, CancellationToken ct,
        DateOnly? requiredDate = null)
    {
        var call = (await client.GetFromJsonAsync<PagedResult<VesselCallSummaryResponse>>("/api/tos/vessel-calls?pageSize=200", ct))!
            .Items.Single(c => c.CallRef == "EASTPIONEER-069S");
        var booked = await client.PostAsJsonAsync("/api/tos/bookings", new
        {
            branchId = SctLcb01, orderTypeCode = "EXP CY/CY", lineCode = "ONEY", customerCode = "CUS-BKF", carrierRef,
            vesselCallId = call.VesselCallId, polPortCode = "THLCH", podPortCode = "SGSIN",
            validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
            containers = new object[] { new { containerNo = box, requiredDate } },
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

    /// <summary>
    /// An empty box brought in on an INT IN (MAEU) and taken out on an INT OUT by another truck: an empty gate-out now.
    /// Through the one-call trip Save, as GateOutTripApiTests gates INT IN / INT OUT.
    /// </summary>
    private static async Task EmptyInThenOutAsync(HttpClient client, string carrierRef, string box, CancellationToken ct,
        string line = "MAEU", string outOrderType = "INT OUT", Guid? outCall = null)
    {
        foreach (var (orderType, suffix, move, plate, call) in new (string, string, object, string, Guid?)[]
                 {
                     ("INT IN", "I", new { containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m }, "70-4321", null),
                     (outOrderType, "O", new
                     {
                         containerNo = box, direction = "OUT", tripType = "PICK_UP_CONT", grossWeightKg = 2200m, weightSource = "WEIGHBRIDGE",
                         seals = new object[] { new { sealNo = $"ZZ-{box[^4..]}", sealType = "LINE", isIntact = true } },
                     }, "70-4322", outCall),
                 })
        {
            var booked = await client.PostAsJsonAsync("/api/tos/bookings", new
            {
                branchId = SctLcb01, orderTypeCode = orderType, lineCode = line, customerCode = call is null ? "CUS-TAE" : "CUS-BKF",
                carrierRef = carrierRef + suffix, vesselCallId = call, polPortCode = call is null ? null : "THLCH", podPortCode = call is null ? null : "SGSIN",
                validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
                containers = new object[] { new { containerNo = box } },
            }, ct);
            Assert.True(booked.StatusCode == HttpStatusCode.Created, $"{orderType} booking returned {(int)booked.StatusCode}: {await booked.Content.ReadAsStringAsync(ct)}");
            using var json = System.Text.Json.JsonDocument.Parse(await booked.Content.ReadAsStringAsync(ct));
            var place = json.RootElement.GetProperty("containers")[0].GetProperty("bookingContainerId").GetGuid();

            // An empty pick-up is planned when the truck arrives and gated on a second Save of the same visit at the out lane.
            Guid? visit = null;
            var status = "";
            var last = "";
            for (var pass = 0; pass < (DirectionOf(move) == "OUT" ? 2 : 1); pass++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tos/gate/trips")
                {
                    Content = JsonContent.Create(new
                    {
                        branchId = SctLcb01, draftId = Guid.NewGuid(), truckVisitId = visit, truck = visit is null ? new { plate } : null,
                        rows = new[] { new { bookingContainerId = place, move } },
                    }),
                };
                request.Headers.Add(Idempotency.Header, Guid.NewGuid().ToString());
                var saved = await client.SendAsync(request, ct);
                var body = await saved.Content.ReadAsStringAsync(ct);
                Assert.True(saved.StatusCode == HttpStatusCode.Created, $"{orderType} gate returned {(int)saved.StatusCode}: {body}");
                using var answer = System.Text.Json.JsonDocument.Parse(body);
                visit = answer.RootElement.GetProperty("truckVisitId").GetGuid();
                (status, last) = (answer.RootElement.GetProperty("rows")[0].GetProperty("status").GetString()!, body);
                if (status == "GATED") break;
            }
            Assert.True(status == "GATED", $"{orderType}: {last}");
        }
    }

    private static string DirectionOf(object move) => (string)move.GetType().GetProperty("direction")!.GetValue(move)!;

    /// <summary>The depot day now (the empty-out helper gates at the current time).</summary>
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));

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

    [Fact]
    public async Task Empty_out_lists_every_empty_gate_out_and_counts_it_by_size()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        var day = Today;
        var url = $"{Reports}/empty-out.xlsx?branchId={SctLcb01}&dateFrom={day:yyyy-MM-dd}&dateTo={day:yyyy-MM-dd}&agentCode=MAEU";
        try
        {
            await EmptyInThenOutAsync(client, carrierRef, box, ct);

            var rows = await SheetAsync(client, url, ct);
            Assert.Contains(rows, r => r[0] == "INBOUND EMPTY OUT REPORT");
            Assert.Contains(rows, r => r[0] == "AGENT:MAEU");
            Assert.Contains(rows, r => r[..13].SequenceEqual(["Container No", "Size", "Plate No.", "TRUCK COMPANY", "NEXT LOCATION", "Vessel Name",
                "Voyage No", "Consignee / SHIPPER", "Date", "Time", "Status", "Damage Condition", "Remarks"]));
            var row = Assert.Single(rows, r => r[0] == box);
            Assert.Equal(["20GP", "70-4322"], row[1..3]);
            Assert.Equal(day.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture), row[8]);
            // SUM counts every move listed, each once.
            var listed = rows.Skip(rows.FindIndex(r => r[0] == "Container No") + 1).TakeWhile(r => r[0] != "Summary").Count(r => r[0].Length > 0);
            Assert.Equal(listed.ToString(), rows.Single(r => r[0] == "TOTAL")[15]);
            Assert.Contains(rows, r => r[0] == "Container Size" && r[1] == "20GP" && r[15] == "SUM");
            await PdfAsync(client, url.Replace(".xlsx", ".pdf"), ct);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBoxReservationsAsync(box);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    [Fact]
    public async Task Import_export_summary_counts_a_full_gate_in_under_its_agent_and_month()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        var day = GateDay;
        var url = $"{Reports}/import-export-summary.xlsx?branchId={SctLcb01}&year={day.Year}&bookingType=IMPORT";
        int Cell(string[]? row, int i) => row is null || row[i].Length == 0 ? 0 : int.Parse(row[i]);
        var (cont, teu) = (1 + (day.Month - 1) * 2, 2 + (day.Month - 1) * 2);
        try
        {
            var before = (await SheetAsync(client, url, ct)).FirstOrDefault(r => r[0] == "MAEU");
            await ImportGatedInAsync(client, carrierRef, box, ct);

            var rows = await SheetAsync(client, url, ct);
            Assert.Contains(rows, r => r[0] == "<< SUMMARY GATE IN CONTAINER >>");
            Assert.Contains(rows, r => r[0] == "AGENT" && r[1] == "JAN" && r[25] == "TOTAL");
            Assert.Contains(rows, r => r[1] == "CONT" && r[2] == "TEU");
            var after = rows.Single(r => r[0] == "MAEU");
            Assert.Equal((1, 1, 1, 1), (Cell(after, cont) - Cell(before, cont), Cell(after, teu) - Cell(before, teu),
                Cell(after, 25) - Cell(before, 25), Cell(after, 26) - Cell(before, 26)));
            Assert.Contains(rows, r => r[0] == "Total");
            await PdfAsync(client, url.Replace(".xlsx", ".pdf"), ct);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{url}&movementCode=SOMETIMES", ct)).StatusCode);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    [Fact]
    public async Task Inbound_full_in_lists_the_box_with_its_seal_and_weight()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        var day = GateDay;
        var url = $"{Reports}/inbound-full-in.xlsx?branchId={SctLcb01}&dateFrom={day:yyyy-MM-dd}&dateTo={day:yyyy-MM-dd}&agentCode=MAEU&bookingType=IMPORT";
        try
        {
            await ImportGatedInAsync(client, carrierRef, box, ct);

            var rows = await SheetAsync(client, url, ct);
            Assert.Contains(rows, r => r[0] == "<< INBOUND FULL IN REPORT >>");
            Assert.Contains(rows, r => r[0] == "Container No." && r[3] == "PAY RECEIPT " && r[13] == "สภาพตู้" && r[16] == "Weight");
            var row = Assert.Single(rows, r => r[0] == box);
            Assert.Equal(day.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture), row[1]);
            Assert.Equal(["MAEU", "20'GP", "70-4321"], row[3..6]);
            Assert.Equal([$"ZZ-{box[^4..]}", "", ""], row[7..10]);
            Assert.Equal([carrierRef, "24 TONS"], row[15..17]);
            Assert.Contains(rows, r => r[0] == "Container Size" && r[1] == "20HC" && r[16] == "SUM");
            await PdfAsync(client, url.Replace(".xlsx", ".pdf"), ct);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    [Fact]
    public async Task Customs_import_unstuffing_numbers_the_import_full_gate_ins()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        var day = GateDay;
        var url = $"{Reports}/customs-import-unstuffing.xlsx?branchId={SctLcb01}&dateFrom={day:yyyy-MM-dd}&dateTo={day:yyyy-MM-dd}&agentCode=MAEU";
        try
        {
            await ImportGatedInAsync(client, carrierRef, box, ct);

            var rows = await SheetAsync(client, url, ct);
            Assert.Contains(rows, r => r[0] == "สมุดควบคุมคอนเทนเนอร์ (กศก 84)");
            Assert.Contains(rows, r => r[0] == "Item" && r[5] == "Unstuffing Date" && r[10] == "Loading Port" && r[13] == "Remark");
            var row = Assert.Single(rows, r => r[2] == box);
            Assert.Equal(["20'GP", "FCL", "", ""], row[3..7]);
            Assert.Equal([carrierRef, "/"], row[7..9]);
            Assert.Equal("MAEU", row[11]);
            Assert.Contains(rows, r => r[0] == "Container Size" && r[15] == "20 TK" && r[16] == "SUM");
            await PdfAsync(client, url.Replace(".xlsx", ".pdf"), ct);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    [Fact]
    public async Task Daily_unstuffing_prints_its_heading_and_columns_with_no_lines()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var url = $"{Reports}/daily-unstuffing.xlsx?branchId={SctLcb01}&dateFrom=2026-10-01&dateTo=2026-10-10";

        var rows = await SheetAsync(client, url, ct);
        Assert.Contains(rows, r => r[0] == "DAILY UNSTUFFING REPORT ");
        Assert.Contains(rows, r => r[0] == "UNSTUFFING DATE FROM : 01-10-2026   TO : 10-10-2026");
        Assert.Equal("Container No.", rows[^1][0]);
        Assert.Equal("Invoice No", rows[^1][9]);
        await PdfAsync(client, url.Replace(".xlsx", ".pdf"), ct);
    }

    [Theory]
    [InlineData("pti", "PTI REPORT", "Item", "Damage Code", 8)]
    [InlineData("container-repair-summary", "ContainerRepairSummaryList", "TOTAL", "0.00", 8)]
    public async Task Mr_reports_print_their_heading_and_columns_until_gecko_keeps_work_orders(
        string report, string title, string lastRowFirst, string lastRowCell, int cell)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var url = $"{Reports}/{report}.xlsx?branchId={SctLcb01}&dateFrom=2026-10-01&dateTo=2026-10-10";

        var rows = await SheetAsync(client, url, ct);
        Assert.Contains(rows, r => r[0] == title);
        Assert.Equal((lastRowFirst, lastRowCell), (rows[^1][0], rows[^1][cell]));
        await PdfAsync(client, url.Replace(".xlsx", ".pdf"), ct);
    }

    [Fact]
    public async Task Truck_turn_around_lists_the_empty_gate_out_with_the_trucks_times()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        var day = Today;
        var url = $"{Reports}/truck-turn-around.xlsx?branchId={SctLcb01}&dateFrom={day:yyyy-MM-dd}&dateTo={day:yyyy-MM-dd}&agentCode=MAEU";
        try
        {
            await EmptyInThenOutAsync(client, carrierRef, box, ct);

            var rows = await SheetAsync(client, url, ct);
            Assert.Contains(rows, r => r[0] == "มาตรฐานปล่อยตู้เปล่า CY");
            Assert.Contains(rows, r => r[0] == "ลำดับ" && r[3] == "เวลาติดต่อเอกสาร" && r[9] == "หมายเหตุ");
            var row = Assert.Single(rows, r => r[5] == box);
            Assert.Equal(day.ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture), row[1]);
            Assert.Equal(["70-4322"], row[2..3]);
            Assert.Equal(["20", "GP"], row[6..8]);
            Assert.Equal("MAEU", row[9]);
            await PdfAsync(client, url.Replace(".xlsx", ".pdf"), ct);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBoxReservationsAsync(box);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    [Fact]
    public async Task Stock_report_lists_an_empty_release_to_an_export_booking()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        var day = Today;
        var url = $"{Reports}/stock-report.xlsx?branchId={SctLcb01}&dateFrom={day:yyyy-MM-dd}&dateTo={day:yyyy-MM-dd}&agentCode=ONEY";
        try
        {
            var call = (await client.GetFromJsonAsync<PagedResult<VesselCallSummaryResponse>>("/api/tos/vessel-calls?pageSize=200", ct))!
                .Items.Single(c => c.CallRef == "EASTPIONEER-069S");
            await EmptyInThenOutAsync(client, carrierRef, box, ct, line: "ONEY", outOrderType: "EXP CY/CY", outCall: call.VesselCallId);

            var rows = await SheetAsync(client, url, ct);
            Assert.Contains(rows, r => r[0] == "<< STOCK REPORT >>");
            Assert.Contains(rows, r => r[0] == "CONTAINER NO." && r[3] == "EMPTY IN" && r[5] == "EMPTY OUT" && r[14] == "REPO");
            Assert.Contains(rows, r => r[3] == "DATE" && r[4] == "TIME");
            var row = Assert.Single(rows, r => r[0] == box);
            Assert.Equal("20GP", row[2]);
            var date = day.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(("", date), (row[3], row[5]));   // it came in on another booking (INT IN): no empty in on this one
            Assert.Equal(carrierRef + "O", row[13]);
            await PdfAsync(client, url.Replace(".xlsx", ".pdf"), ct);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBoxReservationsAsync(box);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    [Fact]
    public async Task Stuffing_unstuffing_lists_the_full_gate_in_with_its_booking_dates()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        var day = GateDay;
        var url = $"{Reports}/stuffing-unstuffing.xlsx?branchId={SctLcb01}&dateFrom={day:yyyy-MM-dd}&dateTo={day:yyyy-MM-dd}&bookingBlNo={carrierRef}&bookingType=IMPORT";
        try
        {
            await ImportGatedInAsync(client, carrierRef, box, ct);

            var rows = await SheetAsync(client, url, ct);
            Assert.Contains(rows, r => r[0] == "เปลียนชื่อ (IMP UNSTUFFING)");
            Assert.Contains(rows, r => r[0] == "Item" && r[4] == "Empty" && r[6] == "Full" && r[15] == "Remarks");
            var row = Assert.Single(rows, r => r[1] == box);
            Assert.Equal(["1", box, "20GP", "FCL", "", "", day.ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture), ""], row[..8]);
            Assert.Equal([$"ZZ-{box[^4..]}", carrierRef, day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), "/"], row[8..12]);
            Assert.Equal(["Total", "1", "1"], rows.Single(r => r[0] == "Total")[..3]);
            await PdfAsync(client, url.Replace(".xlsx", ".pdf"), ct);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    [Fact]
    public async Task Shore_pass_ect_lists_the_box_required_on_the_day_with_its_weight()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        var required = new DateOnly(2026, 12, 24);
        var url = $"{Reports}/shore-pass-ect.xlsx?branchId={SctLcb01}&dateFrom={required:yyyy-MM-dd}&dateTo={required:yyyy-MM-dd}&agentCode=ONEY&loadingPort=THLCH";
        try
        {
            await ExportGatedInAsync(client, carrierRef, box, ct, required);

            var rows = await SheetAsync(client, url, ct);
            Assert.Contains(rows, r => r[0] == "<<SHORE PASS REPORT>>");
            Assert.Contains(rows, r => r[0] == "DEPOT : THLCH");
            Assert.Contains(rows, r => r[0] == "No." && r[1] == "CONTAINER NO." && r[8] == "PORT");
            var row = Assert.Single(rows, r => r[1] == box);
            Assert.Equal(["20GP", carrierRef, "18000.00"], [row[2], row[6], row[7]]);
            Assert.Equal("EASTERN PIONEER 069S", row[5]);   // FEEDER: vessel name + export voyage
            await PdfAsync(client, url.Replace(".xlsx", ".pdf"), ct);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    [Fact]
    public async Task Shore_passes_1_2_3_list_the_full_gate_in_on_each_terminals_form()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        var day = GateDay;
        string Url(int form) => $"{Reports}/shore-pass-{form}.xlsx?branchId={SctLcb01}&dateFrom={day:yyyy-MM-dd}&dateTo={day:yyyy-MM-dd}&agentCode=MAEU";
        try
        {
            await ImportGatedInAsync(client, carrierRef, box, ct);

            var evergreen = await SheetAsync(client, Url(1), ct);
            Assert.Contains(evergreen, r => r[0].StartsWith("บริษัท เอเวอร์กรีน", StringComparison.Ordinal));
            var one = Assert.Single(evergreen, r => r[1] == box);
            Assert.Equal(["20", "GP"], one[2..4]);
            Assert.Equal($"ZZ-{box[^4..]}", one[5]);
            Assert.Equal(carrierRef, one[12]);

            var lcmt = await SheetAsync(client, Url(2), ct);
            Assert.Contains(lcmt, r => r[0] == "LCMT  Company  Limited");
            var two = Assert.Single(lcmt, r => r[1] == box);
            Assert.Equal([carrierRef, "20", "GP"], two[2..5]);
            Assert.Equal("CREDIT", two[9]);

            var kerry = await SheetAsync(client, Url(3), ct);
            Assert.Contains(kerry, r => r[0] == "KERRY SIAM SEAPORT LIMITED");
            var three = Assert.Single(kerry, r => r[1] == box);
            Assert.Equal(["20", "GP", "E"], three[2..5]);
            Assert.Equal([carrierRef, $"ZZ-{box[^4..]}"], three[7..9]);

            foreach (var form in new[] { 1, 2, 3 }) await PdfAsync(client, Url(form).Replace(".xlsx", ".pdf"), ct);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }
}
