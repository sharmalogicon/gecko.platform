using System.Net;
using System.Net.Http.Json;
using static Gecko.MasterData.Tests.Api.MasterLifecycle;

namespace Gecko.MasterData.Tests.Api;

/// <summary>Vessels, commodities and locations (Tier 2). Every row here is a throwaway Z… code.</summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class LogisticsApiTests(MasterDataApiFactory api)
{
    private const string Vessels = "/api/master/vessels";
    private const string Commodities = "/api/master/commodities";
    private const string Locations = "/api/master/locations";

    /// <summary>A 7-digit number with a correct IMO check digit, starting 99 so no real ship has it.</summary>
    private static string NewImo()
    {
        var six = "99" + Random.Shared.Next(0, 10_000).ToString("D4");
        var sum = 0;
        for (var i = 0; i < 6; i++) sum += (six[i] - '0') * (7 - i);
        return six + (sum % 10);
    }

    private Task<HttpClient> Owner() => api.ClientForAsync(MasterDataApiFactory.SctAdmin);

    // ── vessels ─────────────────────────────────────────────────────────────

    private static object Vessel(string code, string? rv, string name, string? imo = null, string? op = null, string? flag = "TH", string? mmsi = null) =>
        new { vesselCode = code, vesselName = name, imoNumber = imo, operatorPartyCode = op, flagCountryCode = flag, mmsi, vesselType = "FEEDER", teuCapacity = 1100, rowVersion = rv };

    [Fact]
    public async Task A_vessel_lives_its_whole_life_through_the_api()
    {
        var ct = TestContext.Current.CancellationToken;
        var imo = NewImo();
        await RunAsync(await Owner(), await api.ClientForAsync(MasterDataApiFactory.SctAccounts), await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin),
            Vessels, NewCode("ZV"), (code, rv, name) => Vessel(code, rv, name, imo), ct);
    }

    [Fact]
    public async Task Vessel_errors_name_their_field_and_an_imo_is_one_vessel()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await Owner();
        var bad = NewImo();
        bad = bad[..6] + (char)('0' + (bad[6] - '0' + 1) % 10);   // wrong check digit

        await ExpectFieldAsync(await sct.PostAsJsonAsync(Vessels, Vessel(NewCode("ZV"), null, "X", bad), ct), "imoNumber", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Vessels, Vessel(NewCode("ZV"), null, "X", "12345"), ct), "imoNumber", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Vessels, Vessel(NewCode("ZV"), null, "X", mmsi: "12"), ct), "mmsi", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Vessels, Vessel(NewCode("ZV"), null, "X", flag: "XX"), ct), "flagCountryCode", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Vessels, Vessel(NewCode("ZV"), null, "X", op: "NO-SUCH-PARTY"), ct), "operatorPartyCode", ct);

        var imo = NewImo();
        var first = NewCode("ZV");
        var second = NewCode("ZV");
        try
        {
            Assert.Equal(HttpStatusCode.Created, (await sct.PostAsJsonAsync(Vessels, Vessel(first, null, "First", imo), ct)).StatusCode);
            var clash = await sct.PostAsJsonAsync(Vessels, Vessel(second, null, "Second", imo), ct);
            Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
            Assert.Contains(first, await clash.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }
        finally
        {
            await RowVersions.DeleteCurrentAsync(sct, $"{Vessels}/{first}", ct);
            await RowVersions.DeleteCurrentAsync(sct, $"{Vessels}/{second}", ct);
        }
    }

    /// <summary>The operator must be a shipping line; a party that operates a vessel cannot be deleted under it.</summary>
    [Fact]
    public async Task A_vessels_operator_is_a_line_that_stays_while_it_operates()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await Owner();
        var customer = await sct.PostAsJsonAsync("/api/master/parties", new { nameEn = "Not A Line Co., Ltd." }, ct);
        var customerCode = (await customer.Content.ReadFromJsonAsync<Code>(ct))!.PartyCode;
        var line = await sct.PostAsJsonAsync("/api/master/shipping-lines", new { nameEn = "Test Operator Line" }, ct);
        var lineCode = (await line.Content.ReadFromJsonAsync<LineDetail>(ct))!.Line.PartyCode;
        var agent = await sct.PostAsJsonAsync("/api/master/shipping-lines", new { nameEn = "Test Operator Agency", lineRole = "AGENT", principalLineCode = lineCode }, ct);
        var agentCode = (await agent.Content.ReadFromJsonAsync<LineDetail>(ct))!.Line.PartyCode;
        var vessel = NewCode("ZV");
        try
        {
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Vessels, Vessel(vessel, null, "X", op: customerCode), ct), "operatorPartyCode", ct);
            // An agent holds the shipping-line role too, but acts for a line; it does not operate the vessel.
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Vessels, Vessel(vessel, null, "X", op: agentCode), ct), "operatorPartyCode", ct);
            var created = await sct.PostAsJsonAsync(Vessels, Vessel(vessel, null, "Operated", op: lineCode.ToLowerInvariant()), ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var row = (await created.Content.ReadFromJsonAsync<VesselRow>(ct))!;
            Assert.Equal((lineCode, "Test Operator Line"), (row.OperatorPartyCode, row.OperatorName));

            var partyVersion = (await RowVersions.OfAsync(sct, $"/api/master/parties/{Uri.EscapeDataString(lineCode)}", ct))!;
            Assert.Equal(HttpStatusCode.Conflict, (await sct.DeleteAsync(RowVersions.WithVersion($"/api/master/parties/{Uri.EscapeDataString(lineCode)}", partyVersion), ct)).StatusCode);
        }
        finally
        {
            await RowVersions.DeleteCurrentAsync(sct, $"{Vessels}/{vessel}", ct);
            await RowVersions.DeleteCurrentAsync(sct, $"/api/master/parties/{Uri.EscapeDataString(agentCode)}", ct);
            await RowVersions.DeleteCurrentAsync(sct, $"/api/master/parties/{Uri.EscapeDataString(lineCode)}", ct);
            await RowVersions.DeleteCurrentAsync(sct, $"/api/master/parties/{Uri.EscapeDataString(customerCode)}", ct);
        }
    }

    // ── commodities ─────────────────────────────────────────────────────────

    private static object Commodity(string code, string? rv, string name, bool dg = false, string? imdg = null, string? un = null,
        string? packing = null, string? hs = "940360", bool reefer = false, decimal? min = null, decimal? max = null) =>
        new { commodityCode = code, descriptionEn = name, hsCode = hs, isDangerous = dg, imdgClassCode = imdg, unNumber = un, packingGroup = packing,
              isTemperatureControlled = reefer, defaultMinTempC = min, defaultMaxTempC = max, rowVersion = rv };

    [Fact]
    public async Task A_commodity_lives_its_whole_life_through_the_api()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunAsync(await Owner(), await api.ClientForAsync(MasterDataApiFactory.SctAccounts), await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin),
            Commodities, NewCode("ZC"), (code, rv, name) => Commodity(code, rv, name, dg: true, imdg: "3", un: "1263", packing: "II"), ct);
    }

    [Fact]
    public async Task Commodity_errors_name_their_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await Owner();
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Commodities, Commodity(NewCode("ZC"), null, "X", hs: "12345"), ct), "hsCode", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Commodities, Commodity(NewCode("ZC"), null, "X", un: "1263"), ct), "unNumber", ct);            // not dangerous
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Commodities, Commodity(NewCode("ZC"), null, "X", dg: true), ct), "imdgClassCode", ct);        // dangerous, no class
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Commodities, Commodity(NewCode("ZC"), null, "X", dg: true, imdg: "10"), ct), "imdgClassCode", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Commodities, Commodity(NewCode("ZC"), null, "X", dg: true, imdg: "3", packing: "IV"), ct), "packingGroup", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Commodities, Commodity(NewCode("ZC"), null, "X", reefer: true, min: 5, max: -18), ct), "defaultMaxTempC", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Commodities, Commodity(NewCode("ZC"), null, "X", min: -18, max: -15), ct), "defaultMinTempC", ct);   // not a reefer
    }

    // ── locations ───────────────────────────────────────────────────────────

    private static object Location(string code, string? rv, string name, string type = "FACTORY", string? party = null, string? country = "TH",
        decimal? lat = null, decimal? lng = null) =>
        new { locationCode = code, locationNameEn = name, locationType = type, partyCode = party, countryCode = country, city = "Si Racha", latitude = lat, longitude = lng, rowVersion = rv };

    [Fact]
    public async Task A_location_lives_its_whole_life_through_the_api()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunAsync(await Owner(), await api.ClientForAsync(MasterDataApiFactory.SctAccounts), await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin),
            Locations, NewCode("ZL"), (code, rv, name) => Location(code, rv, name, "TERMINAL", lat: 13.08m, lng: 100.9m), ct);
    }

    [Fact]
    public async Task Location_errors_name_their_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await Owner();
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Locations, Location(NewCode("ZL"), null, "X", type: "BEACH"), ct), "locationType", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Locations, Location(NewCode("ZL"), null, "X", party: "NO-SUCH-PARTY"), ct), "partyCode", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Locations, Location(NewCode("ZL"), null, "X", country: "XX"), ct), "countryCode", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Locations, Location(NewCode("ZL"), null, "X", lat: 13m), ct), "longitude", ct);
    }

    private sealed record Code(string PartyCode);
    private sealed record LineRow(string PartyCode);
    private sealed record LineDetail(LineRow Line);
    private sealed record VesselRow(string VesselCode, string? OperatorPartyCode, string? OperatorName);
}
