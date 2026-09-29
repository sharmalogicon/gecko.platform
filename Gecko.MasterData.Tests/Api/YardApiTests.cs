using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// Editing a yard (name, type, capacity). Yards belong to a BRANCH, so the grant
/// that counts is mdm.org.manage at that branch — SCT's LCB ops manager edits LCB
/// yards, not LKR's. The code is not editable and there is no create or delete:
/// gate transactions and visits in gecko_tos point at the yard.
///
/// These tests edit a FIXTURE yard and always put it back as they found it.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class YardApiTests(MasterDataApiFactory api)
{
    private const string Yards = "/api/master/yards";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly Guid SctLkr01 = Guid.Parse("785AE785-33A5-F111-9B0D-00919E4766D5");

    private static async Task<Yard> YardAsync(HttpClient client, Guid branchId, string code, CancellationToken ct) =>
        (await client.GetFromJsonAsync<Page>($"{Yards}?branchId={branchId}&activeOnly=false", ct))!.Items.Single(y => y.YardCode == code);

    private static object Body(Yard y, string? rowVersion, string? name = null, int? capacity = null, string? type = null, string? direction = null) => new
    {
        nameEn = name ?? y.NameEn, nameLocal = y.NameLocal, yardType = type ?? y.YardType, fullEmpty = y.FullEmpty,
        directionCode = direction ?? y.DirectionCode, capacityTeu = capacity ?? y.CapacityTeu, rowVersion,
    };

    /// <summary>Put the fixture back at whatever version it has now.</summary>
    private static async Task RestoreAsync(HttpClient client, Yard original, Guid branchId, CancellationToken ct)
    {
        var now = await YardAsync(client, branchId, original.YardCode, ct);
        if (now == original with { RowVersion = now.RowVersion }) return;
        var restored = await client.PutAsJsonAsync($"{Yards}/{original.YardId}", Body(original, now.RowVersion), ct);
        Assert.True(restored.StatusCode == HttpStatusCode.OK, $"restoring {original.YardCode} returned {(int)restored.StatusCode}");
    }

    private static async Task ExpectFieldAsync(HttpResponseMessage response, string field, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400 on {field}, got {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"expected an error on '{field}': {body}");
    }

    [Fact]
    public async Task A_yard_is_renamed_and_resized_with_its_row_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var original = await YardAsync(sct, SctLcb01, "Y-MNR", ct);
        var url = $"{Yards}/{original.YardId}";
        try
        {
            await ExpectFieldAsync(await sct.PutAsJsonAsync(url, Body(original, null, capacity: 999), ct), "rowVersion", ct);

            var saved = await sct.PutAsJsonAsync(url, Body(original, original.RowVersion, name: "M&R yard (test)", capacity: 321, type: "MIXED", direction: "EXPORT"), ct);
            Assert.True(saved.StatusCode == HttpStatusCode.OK, $"PUT returned {(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync(ct)}");
            var yard = (await saved.Content.ReadFromJsonAsync<Yard>(ct))!;
            Assert.Equal(("M&R yard (test)", 321, "MIXED", "EXPORT", "Y-MNR"), (yard.NameEn, yard.CapacityTeu, yard.YardType, yard.DirectionCode, yard.YardCode));
            Assert.Equal(yard, await YardAsync(sct, SctLcb01, "Y-MNR", ct));

            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync(url, Body(original, original.RowVersion, capacity: 5), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await sct.PutAsJsonAsync($"{Yards}/{Guid.NewGuid()}", Body(original, original.RowVersion), ct)).StatusCode);
        }
        finally { await RestoreAsync(sct, original, SctLcb01, ct); }
    }

    [Fact]
    public async Task Yard_errors_name_their_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var y = await YardAsync(sct, SctLcb01, "Y-MNR", ct);
        var url = $"{Yards}/{y.YardId}";

        await ExpectFieldAsync(await sct.PutAsJsonAsync(url, Body(y, y.RowVersion, name: ""), ct), "nameEn", ct);
        await ExpectFieldAsync(await sct.PutAsJsonAsync(url, Body(y, y.RowVersion, type: "CAR_PARK"), ct), "yardType", ct);
        await ExpectFieldAsync(await sct.PutAsJsonAsync(url, Body(y, y.RowVersion, capacity: -1), ct), "capacityTeu", ct);
        await ExpectFieldAsync(await sct.PutAsJsonAsync(url, Body(y, y.RowVersion, direction: "SIDEWAYS"), ct), "directionCode", ct);
        await ExpectFieldAsync(await sct.PutAsJsonAsync(url, new { nameEn = y.NameEn, yardType = y.YardType, fullEmpty = "HALF", rowVersion = y.RowVersion }, ct), "fullEmpty", ct);
        Assert.Equal(y, await YardAsync(sct, SctLcb01, "Y-MNR", ct));   // nothing was written
    }

    /// <summary>mdm.org.manage AT the yard's branch: LCB's ops manager edits LCB, not LKR; view-only and other tenants cannot.</summary>
    [Fact]
    public async Task A_yard_is_edited_by_a_manager_of_its_own_branch()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var ops = await api.ClientForAsync(MasterDataApiFactory.SctOpsLcb);        // OPS_MANAGER at LCB01 only
        var edi = await api.ClientForAsync(MasterDataApiFactory.SctEdi);           // no mdm.org.manage
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var lcb = await YardAsync(sct, SctLcb01, "Y-MNR", ct);
        var lkr = await YardAsync(sct, SctLkr01, "Y-EXPORT", ct);
        try
        {
            var own = await ops.PutAsJsonAsync($"{Yards}/{lcb.YardId}", Body(lcb, lcb.RowVersion, capacity: 111), ct);
            Assert.True(own.StatusCode == HttpStatusCode.OK, $"ops at LCB returned {(int)own.StatusCode}: {await own.Content.ReadAsStringAsync(ct)}");
            Assert.Equal(HttpStatusCode.Forbidden, (await ops.PutAsJsonAsync($"{Yards}/{lkr.YardId}", Body(lkr, lkr.RowVersion, capacity: 111), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.PutAsJsonAsync($"{Yards}/{lkr.YardId}", Body(lkr, lkr.RowVersion, capacity: 111), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{Yards}/{lkr.YardId}", Body(lkr, lkr.RowVersion, capacity: 111), ct)).StatusCode);
            Assert.Equal(lkr, await YardAsync(sct, SctLkr01, "Y-EXPORT", ct));
        }
        finally
        {
            await RestoreAsync(sct, lcb, SctLcb01, ct);
            await RestoreAsync(sct, lkr, SctLkr01, ct);
        }
    }

    private sealed record Yard(
        Guid YardId, string YardCode, string NameEn, string? NameLocal, string YardType, string FullEmpty,
        string? DirectionCode, int? CapacityTeu, bool IsActive, string RowVersion);
    private sealed record Page(List<Yard> Items);
}
