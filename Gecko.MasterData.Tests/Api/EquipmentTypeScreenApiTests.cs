using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// What the container-type screens add on top of EquipmentTypeApiTests: ISO
/// mapping errors on the picker's row, the vocabulary the editor offers, and
/// ACCOUNTS (who prices per container type) reading but not editing.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class EquipmentTypeScreenApiTests(MasterDataApiFactory api)
{
    private const string Base = "/api/master/equipment-types";

    [Fact]
    public async Task Iso_mapping_errors_name_their_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = $"T{Guid.NewGuid():N}"[..7].ToUpperInvariant();
        var created = await sct.PostAsJsonAsync(Base, new
        {
            typeCode = code, descriptionEn = "Screen test", lengthFt = 20, heightClass = "STANDARD", isoGroupCode = "GP", teu = 1.0m,
        }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct));
        var id = doc.RootElement.GetProperty("type").GetProperty("equipmentTypeId").GetString();
        var version = doc.RootElement.GetProperty("type").GetProperty("rowVersion").GetString();
        var url = $"{Base}/{id}";
        try
        {
            async Task Expect(object[] isoCodes, string field) =>
                await ExpectFieldAsync(await sct.PutAsJsonAsync($"{url}/iso-codes", new { rowVersion = version, isoCodes }, ct), field, ct);

            await Expect([new { isoCode = "22B1", isDefaultOutbound = true }, new { isoCode = "9Z9Z", isDefaultOutbound = false }], "isoCodes[1].isoCode");
            await Expect([new { isoCode = "22B1", isDefaultOutbound = true }, new { isoCode = "22B1", isDefaultOutbound = false }], "isoCodes[1].isoCode");
            await Expect([new { isoCode = "22B1", isDefaultOutbound = false }], "isoCodes");                     // no default outbound
            await Expect([new { isoCode = "22b1", isDefaultOutbound = true }], "isoCodes[0].isoCode");            // the shape is upper-case
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, url, ct); }
    }

    [Fact]
    public async Task The_vocabulary_is_what_the_api_accepts()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var v = (await sct.GetFromJsonAsync<Vocabulary>("/api/master/vocabulary/equipment", ct))!;

        Assert.Subset(v.Lengths.ToHashSet(), new HashSet<int> { 20, 40, 45 });
        Assert.Equal(["HALF", "HIGH_CUBE", "STANDARD"], v.HeightClasses.Order());
        Assert.Contains(v.IsoGroups, g => g is { Code: "RT", IsReefer: true });
        Assert.Contains(v.IsoGroups, g => g.Code == "GP");
    }

    /// <summary>Decision D: ACCOUNTS prices per container type, so it reads the vocabulary — and still cannot change it.</summary>
    [Fact]
    public async Task Accounts_reads_container_types_but_cannot_change_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var accounts = await api.ClientForAsync(MasterDataApiFactory.SctAccounts);

        Assert.Equal(HttpStatusCode.OK, (await accounts.GetAsync(Base, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accounts.GetAsync("/api/master/vocabulary/equipment", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accounts.GetAsync("/api/master/iso-codes?search=22G1", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await accounts.PostAsJsonAsync(Base, new
        {
            typeCode = "NOPE", descriptionEn = "x", lengthFt = 20, heightClass = "STANDARD", isoGroupCode = "GP", teu = 1.0m,
        }, ct)).StatusCode);
    }

    /// <summary>Counted in SQL over the whole registry, and equal to what the containers list itself reports per type.</summary>
    [Fact]
    public async Task Container_counts_cover_the_whole_registry()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var counts = (await sct.GetFromJsonAsync<List<Count>>($"{Base}/container-counts", ct))!;
        using var page = JsonDocument.Parse(await sct.GetStringAsync("/api/master/containers?pageSize=1", ct));

        Assert.NotEmpty(counts);
        Assert.True(counts.Sum(c => c.Containers) <= page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.All(counts, c => Assert.True(c.Containers > 0));
    }

    private sealed record Count(Guid EquipmentTypeId, int Containers);

    private static async Task ExpectFieldAsync(HttpResponseMessage response, string field, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400 on {field}, got {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        var keys = doc.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
        Assert.True(keys.Contains(field), $"expected an error on '{field}', got [{string.Join(", ", keys)}]: {body}");
    }

    private sealed record Group(string Code, bool IsReefer);
    private sealed record Vocabulary(List<int> Lengths, List<string> HeightClasses, List<Group> IsoGroups);
}
