using System.Net;
using System.Net.Http.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// Code lists as the Lookups screen edits them: the categories come from the API,
/// and a tenant's own value (added, or an override of a global one) is versioned
/// like every other tenant row — a stale edit or delete is 409, never
/// last-write-wins.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class CodeListApiTests(MasterDataApiFactory api)
{
    private const string CodeLists = "/api/master/code-lists";

    private static string NewCode() => $"Z{Guid.NewGuid():N}"[..8].ToUpperInvariant();

    private static object Body(string category, string code, string description, string? rowVersion = null, bool isActive = true) =>
        new { categoryCode = category, code, descriptionEn = description, rowVersion, isActive };

    [Fact]
    public async Task The_categories_come_from_the_api_with_whether_a_tenant_may_add_to_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var categories = (await sct.GetFromJsonAsync<List<Category>>(CodeLists, ct))!;

        var holdEvent = Assert.Single(categories, c => c.CategoryCode == "HOLD_EVENT");
        Assert.False(holdEvent.AllowsTenantValues);          // the platform raises those events
        Assert.True(holdEvent.ValueCount > 0);
        Assert.True(Assert.Single(categories, c => c.CategoryCode == "TRUCK_CATEGORY").AllowsTenantValues);
    }

    [Fact]
    public async Task A_tenant_value_is_versioned_like_every_tenant_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = NewCode();
        var url = $"{CodeLists}/TRUCK_CATEGORY/{code}";
        try
        {
            var created = await Read(await sct.PutAsJsonAsync(url, Body("TRUCK_CATEGORY", code, "Test truck"), ct), HttpStatusCode.OK, ct);
            Assert.True(created.IsTenantDefined);
            Assert.NotNull(created.RowVersion);
            var listed = Assert.Single((await sct.GetFromJsonAsync<List<Value>>($"{CodeLists}/TRUCK_CATEGORY", ct))!, v => v.Code == code);
            Assert.Equal(created.RowVersion, listed.RowVersion);

            // An edit of the tenant's own row needs the version it was read at.
            Assert.Equal(HttpStatusCode.BadRequest, (await sct.PutAsJsonAsync(url, Body("TRUCK_CATEGORY", code, "No version"), ct)).StatusCode);
            var edited = await Read(await sct.PutAsJsonAsync(url, Body("TRUCK_CATEGORY", code, "Edited", created.RowVersion), ct), HttpStatusCode.OK, ct);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync(url, Body("TRUCK_CATEGORY", code, "Stale", created.RowVersion), ct)).StatusCode);

            Assert.Equal(HttpStatusCode.BadRequest, (await sct.DeleteAsync(url, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.DeleteAsync(RowVersions.WithVersion(url, created.RowVersion!), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await sct.DeleteAsync(RowVersions.WithVersion(url, edited.RowVersion!), ct)).StatusCode);

            // Someone who still holds a version of the removed row lost a race: 409, not a silent re-create.
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync(url, Body("TRUCK_CATEGORY", code, "Late", edited.RowVersion), ct)).StatusCode);
        }
        finally { await RowVersions.DeleteCurrentFromListAsync(sct, url, ct); }
    }

    /// <summary>A tenant may rename or hide a GLOBAL value; removing its override brings the global one back.</summary>
    [Fact]
    public async Task Overriding_a_global_value_and_removing_the_override()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var global = (await sct.GetFromJsonAsync<List<Value>>($"{CodeLists}/SURVEY_TYPE", ct))!
            .First(v => !v.IsTenantDefined && v.RowVersion is null);
        var url = $"{CodeLists}/SURVEY_TYPE/{global.Code}";
        try
        {
            var overridden = await Read(await sct.PutAsJsonAsync(url, Body("SURVEY_TYPE", global.Code, "Renamed by SCT"), ct), HttpStatusCode.OK, ct);
            Assert.False(overridden.IsTenantDefined);    // still a global value …
            Assert.NotNull(overridden.RowVersion);        // … with this tenant's own row over it
            var listed = Assert.Single((await sct.GetFromJsonAsync<List<Value>>($"{CodeLists}/SURVEY_TYPE", ct))!, v => v.Code == global.Code);
            Assert.Equal("Renamed by SCT", listed.DescriptionEn);

            Assert.Equal(HttpStatusCode.NoContent, (await sct.DeleteAsync(RowVersions.WithVersion(url, overridden.RowVersion!), ct)).StatusCode);
            var back = Assert.Single((await sct.GetFromJsonAsync<List<Value>>($"{CodeLists}/SURVEY_TYPE", ct))!, v => v.Code == global.Code);
            Assert.Equal((global.DescriptionEn, (string?)null), (back.DescriptionEn, back.RowVersion));
        }
        finally { await RowVersions.DeleteCurrentFromListAsync(sct, url, ct); }
    }

    [Fact]
    public async Task Code_lists_need_config_manage_to_change_and_stay_inside_the_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var accounts = await api.ClientForAsync(MasterDataApiFactory.SctAccounts);   // no mdm.config.manage
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var code = NewCode();
        var url = $"{CodeLists}/TRUCK_CATEGORY/{code}";
        try
        {
            var created = await Read(await sct.PutAsJsonAsync(url, Body("TRUCK_CATEGORY", code, "Test truck"), ct), HttpStatusCode.OK, ct);

            Assert.Equal(HttpStatusCode.Forbidden, (await accounts.PutAsJsonAsync(url, Body("TRUCK_CATEGORY", code, "Nope", created.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await accounts.DeleteAsync(RowVersions.WithVersion(url, created.RowVersion!), ct)).StatusCode);

            Assert.DoesNotContain((await other.GetFromJsonAsync<List<Value>>($"{CodeLists}/TRUCK_CATEGORY?includeInactive=true", ct))!, v => v.Code == code);
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(RowVersions.WithVersion(url, created.RowVersion!), ct)).StatusCode);
        }
        finally { await RowVersions.DeleteCurrentFromListAsync(sct, url, ct); }
    }

    private static async Task<Value> Read(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expected, $"expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return System.Text.Json.JsonSerializer.Deserialize<Value>(body, System.Text.Json.JsonSerializerOptions.Web)!;
    }

    private sealed record Category(string CategoryCode, bool AllowsTenantValues, int ValueCount);
    private sealed record Value(string Code, string DescriptionEn, bool IsTenantDefined, string? RowVersion);
}
