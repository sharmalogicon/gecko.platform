using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// The life every coded master with a GET-one goes through, once, so each Tier 2
/// master's own tests only add its rules: create, duplicate 409, missing
/// rowVersion 400, edit, stale 409, a caller without manage 403, the other
/// tenant (404 on GET / PUT / DELETE), delete with missing / stale / current
/// version, and 404 afterwards.
/// </summary>
internal static class MasterLifecycle
{
    /// <param name="body">(code, rowVersion, name) → a valid request body; the name is what an edit changes.</param>
    public static async Task RunAsync(
        HttpClient owner, HttpClient viewOnly, HttpClient otherTenant,
        string listUrl, string code, Func<string, string?, string, object> body, CancellationToken ct)
    {
        var one = $"{listUrl}/{Uri.EscapeDataString(code)}";
        try
        {
            var created = await owner.PostAsJsonAsync(listUrl, body(code, null, "Created"), ct);
            var createdText = await created.Content.ReadAsStringAsync(ct);
            Assert.True(created.IsSuccessStatusCode, $"POST {listUrl} returned {(int)created.StatusCode}: {createdText}");
            var first = RowVersions.In(createdText);

            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(listUrl, body(code, null, "Again"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(one, ct)).StatusCode);
            await ExpectFieldAsync(await owner.PutAsJsonAsync(one, body(code, null, "No version"), ct), "rowVersion", ct);

            var edited = await owner.PutAsJsonAsync(one, body(code, first, "Edited"), ct);
            var editedText = await edited.Content.ReadAsStringAsync(ct);
            Assert.True(edited.StatusCode == HttpStatusCode.OK, $"PUT {one} returned {(int)edited.StatusCode}: {editedText}");
            var second = RowVersions.In(editedText);
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(one, body(code, first, "Stale"), ct)).StatusCode);

            Assert.Equal(HttpStatusCode.Forbidden, (await viewOnly.PostAsJsonAsync(listUrl, body(code + "V", null, "Nope"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewOnly.PutAsJsonAsync(one, body(code, second, "Nope"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewOnly.DeleteAsync(RowVersions.WithVersion(one, second), ct)).StatusCode);

            Assert.Equal(HttpStatusCode.NotFound, (await otherTenant.GetAsync(one, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await otherTenant.PutAsJsonAsync(one, body(code, second, "Theirs"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await otherTenant.DeleteAsync(RowVersions.WithVersion(one, second), ct)).StatusCode);
            Assert.Null(RowVersions.FindRow(JsonDocument.Parse(await otherTenant.GetStringAsync($"{listUrl}?search={Uri.EscapeDataString(code)}&includeInactive=true", ct)).RootElement, code));

            Assert.Equal(HttpStatusCode.BadRequest, (await owner.DeleteAsync(one, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync(RowVersions.WithVersion(one, first), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(RowVersions.WithVersion(one, second), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(one, ct)).StatusCode);
        }
        finally { await RowVersions.DeleteCurrentAsync(owner, one, ct); }
    }

    public static async Task ExpectFieldAsync(HttpResponseMessage response, string field, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400 on {field}, got {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"expected an error on '{field}': {body}");
    }

    public static string NewCode(string prefix, int length = 8) => $"{prefix}{Guid.NewGuid():N}"[..length].ToUpperInvariant();
}
