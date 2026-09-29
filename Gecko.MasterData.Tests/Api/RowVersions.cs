using System.Net;
using System.Text.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// DELETE and child-set PUTs name the version they saw (Phase 0). Tests that
/// only want a row gone — cleanup, mostly — read the current version first and
/// delete with it, so they keep testing what they were written to test.
/// </summary>
internal static class RowVersions
{
    public static string WithVersion(string url, string rowVersion) =>
        $"{url}{(url.Contains('?') ? '&' : '?')}rowVersion={Uri.EscapeDataString(rowVersion)}";

    /// <summary>The first rowVersion in a body, depth-first — detail responses nest it (e.g. { chargeCode: { rowVersion } }).</summary>
    public static string In(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Find(doc.RootElement, "rowVersion") ?? throw new Xunit.Sdk.XunitException($"No rowVersion in: {json}");
    }

    /// <summary>The current version of a resource that has a GET-one; null when it is gone.</summary>
    public static async Task<string?> OfAsync(HttpClient client, string url, CancellationToken ct)
    {
        var response = await client.GetAsync(url, ct);
        return response.StatusCode == HttpStatusCode.OK ? In(await response.Content.ReadAsStringAsync(ct)) : null;
    }

    /// <summary>The current version of a row found in a list by its code (for masters with no GET-one).</summary>
    public static async Task<string?> InListAsync(HttpClient client, string listUrl, string code, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync(listUrl, ct));
        return FindRow(doc.RootElement, code) is { } row ? Find(row, "rowVersion") : null;
    }

    /// <summary>Delete at whatever version the row has now. Returns the response, or null when the row was already gone.</summary>
    public static async Task<HttpResponseMessage?> DeleteCurrentAsync(HttpClient client, string url, CancellationToken ct) =>
        await OfAsync(client, url, ct) is { } version ? await client.DeleteAsync(WithVersion(url, version), ct) : null;

    /// <summary>
    /// Same, for masters with no GET-one: the version comes from the list at the
    /// parent URL (the URL minus its last segment), matched on the code.
    /// </summary>
    public static async Task<HttpResponseMessage?> DeleteCurrentFromListAsync(HttpClient client, string url, CancellationToken ct)
    {
        var list = url[..url.LastIndexOf('/')];
        var code = Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]);
        return await InListAsync(client, $"{list}?includeInactive=true", code, ct) is { } version
            ? await client.DeleteAsync(WithVersion(url, version), ct)
            : null;
    }

    public static string? Find(JsonElement element, string property)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                    if (p.NameEquals(property) && p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number) return p.Value.ToString();
                foreach (var p in element.EnumerateObject())
                    if (Find(p.Value, property) is { } found) return found;
                return null;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (Find(item, property) is { } found) return found;
                return null;
            default:
                return null;
        }
    }

    /// <summary>The object in a list (or a paged { items }) with a string value equal to <paramref name="code"/>.</summary>
    public static JsonElement? FindRow(JsonElement element, string code)
    {
        var rows = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("items", out var items) ? items : element;
        if (rows.ValueKind != JsonValueKind.Array) return null;
        foreach (var row in rows.EnumerateArray())
            foreach (var p in row.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == code) return row;
        return null;
    }
}
