using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// Shipping lines and their agents through the API (the Lines screen). A line
/// is a party with the SHIPPING_LINE role; every one here is a throwaway, and
/// agents are deleted before their principal.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class ShippingLineApiTests(MasterDataApiFactory api)
{
    private const string Lines = "/api/master/shipping-lines";
    private const string Parties = "/api/master/parties";

    private static string LineUrl(string code) => $"{Lines}/{Uri.EscapeDataString(code)}";
    private static string PartyUrl(string code) => $"{Parties}/{Uri.EscapeDataString(code)}";

    /// <summary>A SCAC no fixture holds: Q + three random letters.</summary>
    private static string NewScac() => "Q" + new string(Enumerable.Range(0, 3).Select(_ => (char)('A' + Random.Shared.Next(26))).ToArray());

    private static async Task<Line> NewLineAsync(HttpClient client, object body, CancellationToken ct) =>
        (await Read<Detail>(await client.PostAsJsonAsync(Lines, body, ct), HttpStatusCode.Created, ct)).Line;

    /// <summary>Deletes the parties in order — agents first — at whatever version each has now.</summary>
    private static async Task CleanupAsync(HttpClient client, CancellationToken ct, params string?[] codes)
    {
        foreach (var code in codes)
            if (code is not null) await RowVersions.DeleteCurrentAsync(client, PartyUrl(code), ct);
    }

    [Fact]
    public async Task A_line_and_its_agent_live_their_whole_life_through_the_api()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var scac = NewScac();
        string? lineCode = null, agentCode = null;
        try
        {
            var line = await NewLineAsync(sct, new
            {
                nameEn = "Test Ocean Line", shortName = "TOL", scacCode = scac.ToLowerInvariant(), smdgCode = "tol",
                ediSupportsCodeco = true, brandColorHex = "#1a2b3c",
            }, ct);
            lineCode = line.PartyCode;
            Assert.Equal(("LINE", scac, "TOL", "#1A2B3C", true, true), (line.LineRole, line.ScacCode, line.SmdgCode, line.BrandColorHex, line.EdiSupportsCodeco, line.IsActive));

            var agent = await NewLineAsync(sct, new { nameEn = "Test Ocean Line Agency", lineRole = "AGENT", principalLineCode = lineCode }, ct);
            agentCode = agent.PartyCode;
            Assert.Equal(("AGENT", lineCode, "Test Ocean Line"), (agent.LineRole, agent.PrincipalLineCode, agent.PrincipalLineName));

            // The line is also a party with the SHIPPING_LINE role.
            using (var party = JsonDocument.Parse(await sct.GetStringAsync(PartyUrl(lineCode), ct)))
                Assert.Contains("SHIPPING_LINE", party.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

            // Search by SCAC finds the line; the agent is found by its principal's code, listed after it.
            var found = (await sct.GetFromJsonAsync<Page>($"{Lines}?search={lineCode}", ct))!.Items.Select(i => i.PartyCode).ToList();
            Assert.Equal([lineCode, agentCode], found.Where(c => c == lineCode || c == agentCode));
            Assert.Equal(lineCode, Assert.Single((await sct.GetFromJsonAsync<Page>($"{Lines}?search={scac}", ct))!.Items).PartyCode);
            Assert.DoesNotContain((await sct.GetFromJsonAsync<Page>($"{Lines}?lineRole=AGENT&pageSize=200", ct))!.Items, i => i.PartyCode == lineCode);

            var detail = (await sct.GetFromJsonAsync<Detail>(LineUrl(lineCode), ct))!;
            Assert.Equal(agentCode, Assert.Single(detail.Agents).PartyCode);

            var saved = await Read<Detail>(await sct.PutAsJsonAsync(LineUrl(lineCode), new
            {
                lineRole = "LINE", scacCode = scac, ediSupportsCoparn = true, ediSupportsBaplie = true, allianceCode = "gemini",
                rowVersion = detail.Line.RowVersion,
            }, ct), HttpStatusCode.OK, ct);
            // A PUT replaces the line's columns: what was not sent is cleared.
            Assert.Equal((true, false, true, "GEMINI", (string?)null, (string?)null),
                (saved.Line.EdiSupportsCoparn, saved.Line.EdiSupportsCodeco, saved.Line.EdiSupportsBaplie, saved.Line.AllianceCode, saved.Line.SmdgCode, saved.Line.BrandColorHex));

            var stale = await sct.PutAsJsonAsync(LineUrl(lineCode), new { lineRole = "LINE", rowVersion = detail.Line.RowVersion }, ct);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            await ExpectFieldAsync(await sct.PutAsJsonAsync(LineUrl(lineCode), new { lineRole = "LINE" }, ct), "rowVersion", ct);

            Assert.Equal(HttpStatusCode.NotFound, (await sct.GetAsync(LineUrl("NO-SUCH-LINE"), ct)).StatusCode);
        }
        finally { await CleanupAsync(sct, ct, agentCode, lineCode); }
    }

    [Fact]
    public async Task Line_errors_name_their_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var scac = NewScac();
        string? lineCode = null, agentCode = null;
        try
        {
            lineCode = (await NewLineAsync(sct, new { nameEn = "Test Field Line", scacCode = scac }, ct)).PartyCode;
            agentCode = (await NewLineAsync(sct, new { nameEn = "Test Field Agency", lineRole = "AGENT", principalLineCode = lineCode }, ct)).PartyCode;

            await ExpectFieldAsync(await sct.PostAsJsonAsync(Lines, new { nameEn = "X", scacCode = "MS1" }, ct), "scacCode", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Lines, new { nameEn = "X", brandColorHex = "red" }, ct), "brandColorHex", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Lines, new { nameEn = "X", lineRole = "OWNER" }, ct), "lineRole", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Lines, new { nameEn = "" }, ct), "nameEn", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Lines, new { nameEn = "X", scacCode = scac }, ct), "scacCode", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Lines, new { nameEn = "X", lineRole = "AGENT" }, ct), "principalLineCode", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Lines, new { nameEn = "X", lineRole = "LINE", principalLineCode = lineCode }, ct), "principalLineCode", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Lines, new { nameEn = "X", lineRole = "AGENT", principalLineCode = "NO-SUCH-LINE" }, ct), "principalLineCode", ct);
            // An agent acts for a line, never for another agent.
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Lines, new { nameEn = "X", lineRole = "AGENT", principalLineCode = agentCode }, ct), "principalLineCode", ct);
            await ExpectFieldAsync(await sct.GetAsync($"{Lines}?lineRole=OWNER", ct), "lineRole", ct);

            var agentVersion = (await sct.GetFromJsonAsync<Detail>(LineUrl(agentCode), ct))!.Line.RowVersion;
            await ExpectFieldAsync(await sct.PutAsJsonAsync(LineUrl(agentCode), new { lineRole = "AGENT", principalLineCode = agentCode, rowVersion = agentVersion }, ct), "principalLineCode", ct);
        }
        finally { await CleanupAsync(sct, ct, agentCode, lineCode); }
    }

    /// <summary>A principal with a live agent cannot be deleted, lose the line role or become an agent itself.</summary>
    [Fact]
    public async Task A_principal_line_is_protected_while_an_agent_acts_for_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        string? lineCode = null, otherCode = null, agentCode = null;
        try
        {
            var line = await NewLineAsync(sct, new { nameEn = "Test Principal Line" }, ct);
            lineCode = line.PartyCode;
            otherCode = (await NewLineAsync(sct, new { nameEn = "Test Other Line" }, ct)).PartyCode;
            agentCode = (await NewLineAsync(sct, new { nameEn = "Test Principal Agency", lineRole = "AGENT", principalLineCode = lineCode }, ct)).PartyCode;

            var partyVersion = (await RowVersions.OfAsync(sct, PartyUrl(lineCode), ct))!;
            Assert.Equal(HttpStatusCode.Conflict, (await sct.DeleteAsync(RowVersions.WithVersion(PartyUrl(lineCode), partyVersion), ct)).StatusCode);
            await ExpectFieldAsync(await sct.PutAsJsonAsync(PartyUrl(lineCode), new { nameEn = "Test Principal Line", roles = new[] { "CUSTOMER" }, rowVersion = partyVersion }, ct), "roles", ct);
            await ExpectFieldAsync(await sct.PutAsJsonAsync(LineUrl(lineCode), new { lineRole = "AGENT", principalLineCode = otherCode, rowVersion = line.RowVersion }, ct), "lineRole", ct);

            // With the agent gone, the line can go too.
            Assert.Equal(HttpStatusCode.NoContent, (await RowVersions.DeleteCurrentAsync(sct, PartyUrl(agentCode), ct))!.StatusCode);
            agentCode = null;
            Assert.Equal(HttpStatusCode.NoContent, (await RowVersions.DeleteCurrentAsync(sct, PartyUrl(lineCode), ct))!.StatusCode);
            lineCode = null;
        }
        finally { await CleanupAsync(sct, ct, agentCode, lineCode, otherCode); }
    }

    [Fact]
    public async Task Lines_need_party_manage_to_change_and_stay_inside_the_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var edi = await api.ClientForAsync(MasterDataApiFactory.SctEdi);          // mdm.party.view, not manage
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var scac = NewScac();
        string? lineCode = null;
        try
        {
            var line = await NewLineAsync(sct, new { nameEn = "Test Tenant Line", scacCode = scac }, ct);
            lineCode = line.PartyCode;
            var body = new { lineRole = "LINE", scacCode = scac, rowVersion = line.RowVersion };

            Assert.Equal(HttpStatusCode.OK, (await edi.GetAsync(LineUrl(lineCode), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.PostAsJsonAsync(Lines, new { nameEn = "X" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.PutAsJsonAsync(LineUrl(lineCode), body, ct)).StatusCode);

            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(LineUrl(lineCode), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(LineUrl(lineCode), body, ct)).StatusCode);
            Assert.Empty((await other.GetFromJsonAsync<Page>($"{Lines}?search={scac}", ct))!.Items);
            // The SCAC is unique per tenant, not across tenants.
            var theirs = await other.PostAsJsonAsync(Lines, new { nameEn = "Test Tenant Line (theirs)", scacCode = scac }, ct);
            Assert.Equal(HttpStatusCode.Created, theirs.StatusCode);
            await CleanupAsync(other, ct, (await theirs.Content.ReadFromJsonAsync<Detail>(ct))!.Line.PartyCode);
        }
        finally { await CleanupAsync(sct, ct, lineCode); }
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

    private sealed record Line(
        string PartyCode, string NameEn, bool IsActive, string LineRole, string? PrincipalLineCode, string? PrincipalLineName,
        string? ScacCode, string? SmdgCode, string? AllianceCode, bool EdiSupportsCoparn, bool EdiSupportsCodeco, bool EdiSupportsBaplie,
        string? BrandColorHex, string RowVersion);
    private sealed record Agent(string PartyCode);
    private sealed record Detail(Line Line, List<Agent> Agents);
    private sealed record Page(List<Line> Items);
}
