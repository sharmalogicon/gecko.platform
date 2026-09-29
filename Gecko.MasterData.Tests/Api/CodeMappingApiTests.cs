using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// Partner / legacy code mappings through the API (the Lookups screen's Mappings
/// tab). KORAKIT's are all Vector aliases (LEGACY_VECTOR, "MTY IN" → MTY_IN), so
/// what matters is that a mapping points at a code that exists and that one
/// external code maps to exactly one internal code.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class CodeMappingApiTests(MasterDataApiFactory api)
{
    private const string Mappings = "/api/master/code-mappings";

    private static string NewExternal() => $"ZZ{Guid.NewGuid():N}"[..10].ToUpperInvariant();

    private static object Body(string external, string internalCode = "20GP", string type = "EQUIPMENT_TYPE",
        string? category = null, string? rowVersion = null, string? description = null) => new
    {
        mappingType = type, codeListCategory = category, channel = "LEGACY_VECTOR", direction = "INBOUND",
        externalCode = external, internalCode, description, rowVersion,
    };

    private static async Task<Mapping> Read(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expected, $"expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<Mapping>(body, JsonSerializerOptions.Web)!;
    }

    private static async Task ExpectFieldAsync(HttpResponseMessage response, string field, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400 on {field}, got {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"expected an error on '{field}': {body}");
    }

    private static async Task RemoveAsync(HttpClient client, Guid? id, CancellationToken ct)
    {
        if (id is null) return;
        var page = (await client.GetFromJsonAsync<Page>($"{Mappings}?pageSize=200&channel=LEGACY_VECTOR&search=ZZ", ct))!;
        if (page.Items.FirstOrDefault(m => m.CodeMappingId == id) is { } row)
            await client.DeleteAsync(RowVersions.WithVersion($"{Mappings}/{id}", row.RowVersion), ct);
    }

    [Fact]
    public async Task A_mapping_lives_its_whole_life_through_the_api_and_resolves()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var external = NewExternal();
        Guid? id = null;
        try
        {
            var created = await Read(await sct.PostAsJsonAsync(Mappings, Body(external.ToLowerInvariant()), ct), HttpStatusCode.OK, ct);
            id = created.CodeMappingId;
            Assert.Equal((external, "20GP"), (created.ExternalCode, created.InternalCode));

            var resolved = (await sct.GetFromJsonAsync<Resolution>($"{Mappings}/resolve?mappingType=EQUIPMENT_TYPE&externalCode={external}&channel=LEGACY_VECTOR", ct))!;
            Assert.Equal(("20GP", "TENANT_MAPPING"), (resolved.InternalCode, resolved.ResolvedBy));

            var url = $"{Mappings}/{id}";
            await ExpectFieldAsync(await sct.PutAsJsonAsync(url, Body(external, "40HC"), ct), "rowVersion", ct);
            var edited = await Read(await sct.PutAsJsonAsync(url, Body(external, "40HC", rowVersion: created.RowVersion), ct), HttpStatusCode.OK, ct);
            Assert.Equal("40HC", edited.InternalCode);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync(url, Body(external, "20GP", rowVersion: created.RowVersion), ct)).StatusCode);

            Assert.Equal(HttpStatusCode.BadRequest, (await sct.DeleteAsync(url, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.DeleteAsync(RowVersions.WithVersion(url, created.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await sct.DeleteAsync(RowVersions.WithVersion(url, edited.RowVersion), ct)).StatusCode);
            id = null;
        }
        finally { await RemoveAsync(sct, id, ct); }
    }

    [Fact]
    public async Task A_mapping_must_point_at_a_code_that_exists_and_map_an_external_code_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var external = NewExternal();
        Guid? id = null;
        try
        {
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Mappings, Body(NewExternal(), "NO-SUCH-TYPE"), ct), "internalCode", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Mappings, Body(NewExternal(), "NO_SUCH_MOVE", type: "MOVEMENT"), ct), "internalCode", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Mappings, Body(NewExternal(), "X", type: "CODE_LIST"), ct), "codeListCategory", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Mappings, Body(NewExternal(), "NOT_A_TRUCK", type: "CODE_LIST", category: "TRUCK_CATEGORY"), ct), "internalCode", ct);

            id = (await Read(await sct.PostAsJsonAsync(Mappings, Body(external), ct), HttpStatusCode.OK, ct)).CodeMappingId;

            // The same external code again is a 409 that names the mapping already there — not a 500 from the index.
            var twice = await sct.PostAsJsonAsync(Mappings, Body(external, "40HC"), ct);
            Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
            Assert.Contains("20GP", await twice.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }
        finally { await RemoveAsync(sct, id, ct); }
    }

    [Fact]
    public async Task Mappings_need_config_manage_to_change_and_stay_inside_the_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var accounts = await api.ClientForAsync(MasterDataApiFactory.SctAccounts);   // no mdm.config.manage
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var external = NewExternal();
        Guid? id = null;
        try
        {
            var created = await Read(await sct.PostAsJsonAsync(Mappings, Body(external), ct), HttpStatusCode.OK, ct);
            id = created.CodeMappingId;
            var url = $"{Mappings}/{id}";

            Assert.Equal(HttpStatusCode.Forbidden, (await accounts.PostAsJsonAsync(Mappings, Body(NewExternal()), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await accounts.PutAsJsonAsync(url, Body(external, rowVersion: created.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await accounts.DeleteAsync(RowVersions.WithVersion(url, created.RowVersion), ct)).StatusCode);

            Assert.Empty((await other.GetFromJsonAsync<Page>($"{Mappings}?search={external}", ct))!.Items);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(url, Body(external, rowVersion: created.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(RowVersions.WithVersion(url, created.RowVersion), ct)).StatusCode);
        }
        finally { await RemoveAsync(sct, id, ct); }
    }

    private sealed record Mapping(Guid CodeMappingId, string ExternalCode, string InternalCode, string RowVersion);
    private sealed record Page(List<Mapping> Items);
    private sealed record Resolution(string? InternalCode, string ResolvedBy);
}
