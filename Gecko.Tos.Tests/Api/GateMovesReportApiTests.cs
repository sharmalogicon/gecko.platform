using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Gate;
using Gecko.Tos.Endpoints.Reports;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// GET /api/tos/reports/gate-moves through the real host: moves counted by depot
/// day, movement, customer, line and type. The fixture depot has other moves, so
/// the test counts before and after gating its own ZZTU boxes (ZZG- carrier ref,
/// removed in finally) and checks the difference.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class GateMovesReportApiTests(TosApiFactory api)
{
    private const string Prefix = "ZZG-";
    private const string Report = "/api/tos/reports/gate-moves";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewRef() => $"{Prefix}{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static string NewBox()
    {
        var ten = $"ZZTU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    // Yesterday to tomorrow in UTC dates covers "an hour ago" in any depot zone.
    private static string Range(Guid branch) =>
        $"?branchId={branch}&from={DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)):yyyy-MM-dd}&to={DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)):yyyy-MM-dd}";

    private static async Task BookAsync(HttpClient client, string carrierRef, string box, string type, CancellationToken ct, Guid? branchId = null)
    {
        var response = await client.PostAsJsonAsync("/api/tos/bookings", new
        {
            branchId = branchId ?? SctLcb01,
            orderTypeCode = "IMP CY/CY",
            lineCode = "MAEU",
            customerCode = "CUS-TAE",
            carrierRef,
            validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            requirements = new object[] { new { equipmentTypeCode = type, qty = 1 } },
            containers = new object[] { new { containerNo = box } },
        }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"booking returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }

    private static async Task<GateTransactionResponse> GateAsync(HttpClient client, string box, string direction, DateTimeOffset at,
        CancellationToken ct, Guid? branchId = null)
    {
        var response = await client.PostAsJsonAsync("/api/tos/gate/transactions", new
        {
            branchId = branchId ?? SctLcb01,
            containerNo = box,
            direction,
            truck = new { plate = "70-4321", driverName = "Somchai P." },
            grossWeightKg = 24100m,
            weightSource = "WEIGHBRIDGE",
            seals = new object[] { new { sealNo = $"ZZ-{box[^4..]}", sealType = "LINE", isIntact = true } },
            transactionAt = at,
        }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"gate {direction} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<GateTransactionResponse>(ct))!;
    }

    private static async Task<GateMovesReportResponse> ReportAsync(HttpClient client, CancellationToken ct)
    {
        var response = await client.GetAsync(Report + Range(SctLcb01), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"report returned {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<GateMovesReportResponse>(body, JsonSerializerOptions.Web)!;
    }

    private static int MovesOf(IEnumerable<GateMovesGroupResponse> groups, string? code) =>
        groups.SingleOrDefault(g => g.Code == code)?.Tally.Moves ?? 0;

    [Fact]
    public async Task Moves_are_counted_by_day_movement_customer_line_and_type_and_a_void_is_not_a_move()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var a = NewBox();
        var b = NewBox();
        try
        {
            var before = await ReportAsync(client, ct);

            await BookAsync(client, carrierRef, a, "20GP", ct);
            await GateAsync(client, a, "IN", DateTimeOffset.UtcNow.AddHours(-1), ct);
            await BookAsync(client, carrierRef, b, "40HC", ct);
            var inB = await GateAsync(client, b, "IN", DateTimeOffset.UtcNow.AddMinutes(-50), ct);
            await GateAsync(client, a, "OUT", DateTimeOffset.UtcNow.AddMinutes(-30), ct);

            // Void b's gate-in: it moved nothing.
            var voidResponse = await client.PostAsJsonAsync($"/api/tos/gate/transactions/{inB.GateTransactionId}/void",
                new { reason = "keyed the wrong box", rowVersion = inB.RowVersion }, ct);
            Assert.True(voidResponse.IsSuccessStatusCode, await voidResponse.Content.ReadAsStringAsync(ct));

            var after = await ReportAsync(client, ct);
            Assert.Equal(("SCT-LCB01", 3), (after.BranchCode, after.Days.Count));

            // a in (FULL), a out (FULL): two moves, one TEU each. b's void is counted apart.
            Assert.Equal(before.Total.Moves + 2, after.Total.Moves);
            Assert.Equal(before.Total.FullIn + 1, after.Total.FullIn);
            Assert.Equal(before.Total.FullOut + 1, after.Total.FullOut);
            Assert.Equal(before.Total.Teu + 2m, after.Total.Teu);
            Assert.Equal(before.Voided + 1, after.Voided);

            Assert.Equal(after.Total.Moves, after.Days.Sum(d => d.Tally.Moves));
            Assert.Equal(after.Total.Moves, after.Movements.Sum(m => m.Moves));
            Assert.Equal(MovesOf(before.Customers, "CUS-TAE") + 2, MovesOf(after.Customers, "CUS-TAE"));
            Assert.False(string.IsNullOrWhiteSpace(after.Customers.Single(c => c.Code == "CUS-TAE").Name));
            Assert.Equal(MovesOf(before.Lines, "MAEU") + 2, MovesOf(after.Lines, "MAEU"));
            Assert.Equal(MovesOf(before.Types, "20GP") + 2, MovesOf(after.Types, "20GP"));
            Assert.Equal(MovesOf(before.Types, "40HC"), MovesOf(after.Types, "40HC"));
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    [Theory]
    [InlineData("?from=2026-09-01&to=2026-09-02", "branchId")]
    [InlineData("?branchId=C558E785-33A5-F111-9B0D-00919E4766D5&to=2026-09-02", "from")]
    [InlineData("?branchId=C558E785-33A5-F111-9B0D-00919E4766D5&from=2026-09-02&to=2026-09-01", "to")]
    [InlineData("?branchId=C558E785-33A5-F111-9B0D-00919E4766D5&from=2025-01-01&to=2026-09-01", "to")]
    public async Task A_bad_range_is_a_400_on_the_field(string query, string field)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var response = await client.GetAsync(Report + query, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, text);
        using var body = JsonDocument.Parse(text);
        Assert.Contains(body.RootElement.GetProperty("errors").EnumerateObject(),
            p => string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_report_stays_inside_the_callers_depots_and_tenant()
    {
        var ct = TestContext.Current.CancellationToken;

        var clerk = await api.ClientForAsync(TosApiFactory.SctGateLcb);
        Assert.Equal(HttpStatusCode.OK, (await clerk.GetAsync(Report + Range(SctLcb01), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync(Report + Range(TestDatabase.SctBkk01), ct)).StatusCode);

        var other = await api.ClientForAsync(TosApiFactory.SssOwner);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(Report + Range(SctLcb01), ct)).StatusCode);
    }
}
