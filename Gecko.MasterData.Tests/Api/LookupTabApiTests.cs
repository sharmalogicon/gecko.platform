using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// The five small masters the Lookups screen edits as tabs (decision B): grades,
/// conditions, movements, service types, tax codes. Each goes through the same
/// life — create, edit, a stale edit, a bad field, a caller who may only look,
/// the other tenant, delete — because the screen treats them the same way.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class LookupTabApiTests(MasterDataApiFactory api)
{
    public sealed record Tab(string Name, string Url, string CodeField, Func<string, string?, string, object> Body, object BadBody, string BadField);

    private static readonly Dictionary<string, Tab> Tabs = new[]
    {
        new Tab("grade", "/api/master/container-grades", "gradeCode",
            (code, rv, text) => new { gradeCode = code, descriptionEn = text, rankOrder = 50, isReleasable = true, rowVersion = rv },
            new { gradeCode = "G1", descriptionEn = "X", rankOrder = 500 }, "rankOrder"),
        new Tab("condition", "/api/master/container-conditions", "conditionCode",
            (code, rv, text) => new { conditionCode = code, descriptionEn = text, severity = 3, rowVersion = rv },
            new { conditionCode = "C1", descriptionEn = "X", severity = 0 }, "severity"),
        new Tab("movement", "/api/master/movements", "movementCode",
            (code, rv, text) => new { movementCode = code, descriptionEn = text, fullEmpty = "EMPTY", direction = "IN", appliesToModule = "TOS", rowVersion = rv },
            new { movementCode = "M1", descriptionEn = "X", fullEmpty = "HALF", direction = "IN", appliesToModule = "TOS" }, "fullEmpty"),
        new Tab("service-type", "/api/master/service-types", "serviceCode",
            (code, rv, text) => new { serviceCode = code, descriptionEn = text, originForm = "CY", destinationForm = "CY", rowVersion = rv },
            new { serviceCode = "S1", descriptionEn = "X", originForm = "MOON", destinationForm = "CY" }, "originForm"),
        new Tab("tax-code", "/api/master/tax-codes", "taxCode",
            (code, rv, text) => new { taxCode = code, descriptionEn = text, countryCode = "TH", taxType = "VAT", ratePct = 7m, effectiveFrom = "2026-01-01", rowVersion = rv },
            new { taxCode = "T1", descriptionEn = "X", countryCode = "TH", taxType = "VAT", ratePct = 7m, effectiveFrom = "2026-01-01", effectiveTo = "2025-01-01" }, "effectiveTo"),
    }.ToDictionary(t => t.Name);

    public static TheoryData<string> TabNames => new(Tabs.Keys);

    private static string NewCode(string prefix) => $"Z{prefix}{Guid.NewGuid():N}"[..8].ToUpperInvariant();

    /// <summary>These masters have no GET-one: the row is found in the list, inactive rows included.</summary>
    private static async Task<JsonElement?> RowAsync(HttpClient client, Tab tab, string code, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"{tab.Url}?includeInactive=true", ct));
        foreach (var row in doc.RootElement.EnumerateArray())
            if (row.GetProperty(tab.CodeField).GetString() == code) return row.Clone();
        return null;
    }

    private static async Task ExpectFieldAsync(HttpResponseMessage response, string field, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400 on {field}, got {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"expected an error on '{field}': {body}");
    }

    [Theory]
    [MemberData(nameof(TabNames))]
    public async Task A_lookup_row_lives_its_whole_life_through_the_api(string name)
    {
        var tab = Tabs[name];
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var viewOnly = await api.ClientForAsync(MasterDataApiFactory.SctEdi);   // neither equipment nor commercial manage
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var code = NewCode(name[..1].ToUpperInvariant());
        var one = $"{tab.Url}/{Uri.EscapeDataString(code)}";
        try
        {
            var created = await sct.PostAsJsonAsync(tab.Url, tab.Body(code, null, "Created"), ct);
            Assert.True(created.IsSuccessStatusCode, $"POST {tab.Url} returned {(int)created.StatusCode}: {await created.Content.ReadAsStringAsync(ct)}");
            var first = RowVersions.In(await created.Content.ReadAsStringAsync(ct));

            Assert.Equal(HttpStatusCode.Conflict, (await sct.PostAsJsonAsync(tab.Url, tab.Body(code, null, "Created"), ct)).StatusCode);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(tab.Url, tab.BadBody, ct), tab.BadField, ct);
            await ExpectFieldAsync(await sct.PutAsJsonAsync(one, tab.Body(code, null, "Created"), ct), "rowVersion", ct);

            var edited = await sct.PutAsJsonAsync(one, tab.Body(code, first, "Edited"), ct);
            Assert.True(edited.StatusCode == HttpStatusCode.OK, $"PUT returned {(int)edited.StatusCode}: {await edited.Content.ReadAsStringAsync(ct)}");
            var second = RowVersions.In(await edited.Content.ReadAsStringAsync(ct));
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync(one, tab.Body(code, first, "Stale"), ct)).StatusCode);

            Assert.Equal(HttpStatusCode.Forbidden, (await viewOnly.PostAsJsonAsync(tab.Url, tab.Body(NewCode("V"), null, "Nope"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewOnly.PutAsJsonAsync(one, tab.Body(code, second, "Other"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewOnly.DeleteAsync(RowVersions.WithVersion(one, second), ct)).StatusCode);

            Assert.Null(await RowAsync(other, tab, code, ct));
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(one, tab.Body(code, second, "Other"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(RowVersions.WithVersion(one, second), ct)).StatusCode);

            Assert.Equal(HttpStatusCode.BadRequest, (await sct.DeleteAsync(one, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.DeleteAsync(RowVersions.WithVersion(one, first), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await sct.DeleteAsync(RowVersions.WithVersion(one, second), ct)).StatusCode);
            Assert.Null(await RowAsync(sct, tab, code, ct));
        }
        finally
        {
            if (await RowAsync(sct, tab, code, ct) is { } left)
                await sct.DeleteAsync(RowVersions.WithVersion(one, left.GetProperty("rowVersion").GetString()!), ct);
        }
    }

    /// <summary>
    /// Movement and service codes may hold '/' (a CY/CY service). The route
    /// carries it as %2F, which ASP.NET Core leaves encoded — FromRouteCode undoes
    /// it, or the row could be created and never edited or deleted.
    /// </summary>
    [Theory]
    [InlineData("movement")]
    [InlineData("service-type")]
    public async Task A_code_with_a_slash_can_be_edited_and_deleted(string name)
    {
        var tab = Tabs[name];
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = $"Z{Guid.NewGuid():N}"[..4].ToUpperInvariant() + "/CY";
        var one = $"{tab.Url}/{Uri.EscapeDataString(code)}";
        try
        {
            var created = await sct.PostAsJsonAsync(tab.Url, tab.Body(code, null, "Created"), ct);
            Assert.True(created.IsSuccessStatusCode, $"POST returned {(int)created.StatusCode}: {await created.Content.ReadAsStringAsync(ct)}");
            var edited = await sct.PutAsJsonAsync(one, tab.Body(code, RowVersions.In(await created.Content.ReadAsStringAsync(ct)), "Edited"), ct);
            Assert.True(edited.StatusCode == HttpStatusCode.OK, $"PUT {one} returned {(int)edited.StatusCode}");
            var version = RowVersions.In(await edited.Content.ReadAsStringAsync(ct));
            Assert.Equal(HttpStatusCode.NoContent, (await sct.DeleteAsync(RowVersions.WithVersion(one, version), ct)).StatusCode);
        }
        finally
        {
            if (await RowAsync(sct, tab, code, ct) is { } left)
                await sct.DeleteAsync(RowVersions.WithVersion(one, left.GetProperty("rowVersion").GetString()!), ct);
        }
    }
}
