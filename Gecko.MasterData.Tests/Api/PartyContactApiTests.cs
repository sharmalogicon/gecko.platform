using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// A party's contacts through the API (the customer screen's Contacts section),
/// and the duplicate hint on the party detail. Every party here is a throwaway.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class PartyContactApiTests(MasterDataApiFactory api)
{
    private const string Parties = "/api/master/parties";

    private static async Task<string> NewPartyAsync(HttpClient client, CancellationToken ct)
    {
        var created = await client.PostAsJsonAsync(Parties, new { nameEn = "Contact Test Co., Ltd." }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("partyCode").GetString()!;
    }

    private static string PartyUrl(string code) => $"{Parties}/{Uri.EscapeDataString(code)}";

    [Fact]
    public async Task Contacts_live_their_whole_life_through_the_api_with_one_default_per_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = await NewPartyAsync(sct, ct);
        var contacts = $"{PartyUrl(code)}/contacts";
        try
        {
            var first = await Read<Contact>(await sct.PostAsJsonAsync(contacts, new
            {
                contactType = "BILLING", contactPerson = "Khun Somchai", phone = "+66-38-000-001", email = "billing@example.co.th",
                address1 = "1 Test Road", city = "Si Racha", postcode = "20110", isDefault = true,
            }, ct), HttpStatusCode.Created, ct);
            Assert.True(first is { IsDefault: true, Role: "BILLING", Address1: "1 Test Road" });

            // A second default BILLING contact takes the default over instead of failing on uq_contact__default_party.
            var second = await Read<Contact>(await sct.PostAsJsonAsync(contacts, new { contactType = "BILLING", contactPerson = "Khun Malee", isDefault = true }, ct), HttpStatusCode.Created, ct);
            var detail = (await sct.GetFromJsonAsync<Detail>(PartyUrl(code), ct))!;
            Assert.Equal(second.ContactId, Assert.Single(detail.Contacts, c => c.IsDefault).ContactId);

            var firstNow = detail.Contacts.Single(c => c.ContactId == first.ContactId);
            var renamed = await Read<Contact>(await sct.PutAsJsonAsync($"{contacts}/{first.ContactId}", new
            {
                contactType = "OPERATIONS", contactPerson = "Khun Somchai K.", rowVersion = firstNow.RowVersion,
            }, ct), HttpStatusCode.OK, ct);
            Assert.Equal(("OPERATIONS", "Khun Somchai K."), (renamed.Role, renamed.Name));

            // Stale: the version before the rename.
            var stale = await sct.PutAsJsonAsync($"{contacts}/{first.ContactId}", new { contactType = "OTHER", rowVersion = firstNow.RowVersion }, ct);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

            Assert.Equal(HttpStatusCode.BadRequest, (await sct.DeleteAsync($"{contacts}/{first.ContactId}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await sct.DeleteAsync(RowVersions.WithVersion($"{contacts}/{first.ContactId}", renamed.RowVersion), ct)).StatusCode);
            Assert.DoesNotContain((await sct.GetFromJsonAsync<Detail>(PartyUrl(code), ct))!.Contacts, c => c.ContactId == first.ContactId);
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, PartyUrl(code), ct); }
    }

    [Fact]
    public async Task Contact_errors_name_their_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = await NewPartyAsync(sct, ct);
        var contacts = $"{PartyUrl(code)}/contacts";
        try
        {
            await ExpectFieldAsync(await sct.PostAsJsonAsync(contacts, new { contactType = "FRIEND" }, ct), "contactType", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(contacts, new { contactType = "BILLING", email = "not-an-email" }, ct), "email", ct);
            Assert.Equal(HttpStatusCode.NotFound, (await sct.PostAsJsonAsync($"{Parties}/NO-SUCH-PARTY/contacts", new { contactType = "BILLING" }, ct)).StatusCode);
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, PartyUrl(code), ct); }
    }

    [Fact]
    public async Task Contacts_need_party_manage_and_stay_inside_the_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var edi = await api.ClientForAsync(MasterDataApiFactory.SctEdi);          // mdm.party.view, not manage
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var code = await NewPartyAsync(sct, ct);
        var contacts = $"{PartyUrl(code)}/contacts";
        try
        {
            var contact = await Read<Contact>(await sct.PostAsJsonAsync(contacts, new { contactType = "GATE", contactPerson = "Gate desk" }, ct), HttpStatusCode.Created, ct);

            Assert.Equal(HttpStatusCode.Forbidden, (await edi.PostAsJsonAsync(contacts, new { contactType = "GATE" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.PutAsJsonAsync($"{contacts}/{contact.ContactId}", new { contactType = "GATE", rowVersion = contact.RowVersion }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(contacts, new { contactType = "GATE" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{contacts}/{contact.ContactId}", new { contactType = "GATE", rowVersion = contact.RowVersion }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(RowVersions.WithVersion($"{contacts}/{contact.ContactId}", contact.RowVersion), ct)).StatusCode);
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, PartyUrl(code), ct); }
    }

    /// <summary>A party with a tax id nobody else holds has no duplicates; the list is never null.</summary>
    [Fact]
    public async Task A_unique_party_has_no_duplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var taxId = "9" + Random.Shared.NextInt64(0, 1_000_000_000_000).ToString("D12");
        var created = await sct.PostAsJsonAsync(Parties, new { nameEn = "Unique Tax Co., Ltd.", taxId, branchNo = "00000" }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var code = (await created.Content.ReadFromJsonAsync<Detail>(ct))!.PartyCode;
        try
        {
            Assert.Empty((await sct.GetFromJsonAsync<Detail>(PartyUrl(code), ct))!.Duplicates!);
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, PartyUrl(code), ct); }
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expected, $"expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web)!;
    }

    private static async Task ExpectFieldAsync(HttpResponseMessage response, string field, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400 on {field}, got {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"expected an error on '{field}': {body}");
    }

    private sealed record Contact(Guid ContactId, string? Name, string Role, bool IsDefault, string? Address1, string RowVersion);
    private sealed record Dup(string PartyCode);
    private sealed record Detail(string PartyCode, List<Contact> Contacts, List<Dup>? Duplicates);
}
