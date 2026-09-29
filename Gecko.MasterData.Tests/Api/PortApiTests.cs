using System.Net;
using System.Net.Http.Json;
using static Gecko.MasterData.Tests.Api.MasterLifecycle;

namespace Gecko.MasterData.Tests.Api;

/// <summary>Ports (Tier 2). Every port here is a throwaway ZZ… code.</summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class PortApiTests(MasterDataApiFactory api)
{
    private const string Ports = "/api/master/ports";

    private static object Body(string code, string? rowVersion, string name, string? locode = null, string country = "TH",
        decimal? latitude = null, decimal? longitude = null, string? timezone = "Asia/Bangkok", string type = "SEAPORT") => new
    {
        portCode = code, portNameEn = name, portType = type, countryCode = country, tradeMode = "INTERNATIONAL",
        unLocode = locode, latitude, longitude, timezone, rowVersion,
    };

    [Fact]
    public async Task A_port_lives_its_whole_life_through_the_api()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunAsync(
            await api.ClientForAsync(MasterDataApiFactory.SctAdmin),
            await api.ClientForAsync(MasterDataApiFactory.SctAccounts),          // no mdm.logistics.*
            await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin),
            Ports, NewCode("ZP"), (code, rv, name) => Body(code, rv, name, latitude: 13.08m, longitude: 100.88m), ct);
    }

    [Fact]
    public async Task Port_errors_name_their_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        await ExpectFieldAsync(await sct.PostAsJsonAsync(Ports, Body(NewCode("ZP"), null, "X", country: "XX"), ct), "countryCode", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Ports, Body(NewCode("ZP"), null, "X", locode: "TH1CH"), ct), "unLocode", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Ports, Body(NewCode("ZP"), null, "X", locode: "SGSIN"), ct), "unLocode", ct);   // a Singapore code on a Thai port
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Ports, Body(NewCode("ZP"), null, "X", latitude: 13.1m), ct), "longitude", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Ports, Body(NewCode("ZP"), null, "X", latitude: 95m, longitude: 100m), ct), "latitude", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Ports, Body(NewCode("ZP"), null, "X", timezone: "Mars/Olympus"), ct), "timezone", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Ports, Body(NewCode("ZP"), null, "X", type: "SPACEPORT"), ct), "portType", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Ports, Body("", null, "X"), ct), "portCode", ct);
    }

    /// <summary>One port per UN/LOCODE (uq_port__locode) — a second is a 409 naming the first, not a 500.</summary>
    [Fact]
    public async Task A_un_locode_belongs_to_one_port()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var locode = "TH" + NewCode("", 3).Replace('0', 'Q').Replace('1', 'R');
        var first = NewCode("ZP");
        var second = NewCode("ZP");
        try
        {
            Assert.Equal(HttpStatusCode.Created, (await sct.PostAsJsonAsync(Ports, Body(first, null, "First", locode: locode), ct)).StatusCode);
            var clash = await sct.PostAsJsonAsync(Ports, Body(second, null, "Second", locode: locode), ct);
            Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
            Assert.Contains(first, await clash.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

            var found = await sct.GetFromJsonAsync<Page>($"{Ports}?search={locode}", ct);
            Assert.Equal(first, Assert.Single(found!.Items).PortCode);
        }
        finally
        {
            await RowVersions.DeleteCurrentAsync(sct, $"{Ports}/{first}", ct);
            await RowVersions.DeleteCurrentAsync(sct, $"{Ports}/{second}", ct);
        }
    }

    private sealed record PortRow(string PortCode, string? UnLocode);
    private sealed record Page(List<PortRow> Items);
}
