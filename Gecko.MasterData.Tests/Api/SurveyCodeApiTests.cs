using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Gecko.MasterData.Tests.Api.MasterLifecycle;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// The survey vocabularies (Tier 3): damage codes, repair codes, damage locations,
/// components. A code is unique per standard (CEDEX / IICL / LOCAL), so the same
/// letters may exist in two standards, and rows are addressed by id.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class SurveyCodeApiTests(MasterDataApiFactory api)
{
    public sealed record Kind(string Url, Func<string, string, string?, string, object> Body, object Bad, string BadField);

    private static readonly Dictionary<string, Kind> Kinds = new()
    {
        ["damage"] = new("/api/master/damage-codes", (code, std, rv, d) => new { code, codeStandard = std, descriptionEn = d, severity = 6, makesUnserviceable = true, rowVersion = rv },
            new { code = "ZX", descriptionEn = "x", severity = 12 }, "severity"),
        ["repair"] = new("/api/master/repair-codes", (code, std, rv, d) => new { code, codeStandard = std, descriptionEn = d, repairMode = "WELD", repairGroup = "STRUCTURAL", rowVersion = rv },
            new { code = "ZX", descriptionEn = "x", repairMode = "MAGIC" }, "repairMode"),
        ["location"] = new("/api/master/damage-locations", (code, std, rv, d) => new { code, codeStandard = std, descriptionEn = d, containerFace = "DOOR", rowVersion = rv },
            new { code = "ZX", descriptionEn = "x", containerFace = "SKY" }, "containerFace"),
        ["component"] = new("/api/master/components", (code, std, rv, d) => new { code, codeStandard = std, descriptionEn = d, componentGroup = "DOOR", isOwnPart = true, rowVersion = rv },
            new { code = "ZX", descriptionEn = "x", componentGroup = "WINGS" }, "componentGroup"),
    };

    public static TheoryData<string> Names => new(Kinds.Keys);

    private static async Task<Row> Read(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expected, $"expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<Row>(body, JsonSerializerOptions.Web)!;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task A_survey_code_lives_its_whole_life_through_the_api(string name)
    {
        var k = Kinds[name];
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var viewOnly = await api.ClientForAsync(MasterDataApiFactory.SctEdi);          // mdm.equipment.view only
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var code = NewCode("Z", 6);
        var ids = new List<Guid>();
        try
        {
            var created = await Read(await sct.PostAsJsonAsync(k.Url, k.Body(code.ToLowerInvariant(), "LOCAL", null, "Created"), ct), HttpStatusCode.Created, ct);
            ids.Add(created.Id);
            Assert.Equal(code, created.Code);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PostAsJsonAsync(k.Url, k.Body(code, "LOCAL", null, "Again"), ct)).StatusCode);
            // The same letters in another standard are another code.
            ids.Add((await Read(await sct.PostAsJsonAsync(k.Url, k.Body(code, "IICL", null, "IICL one"), ct), HttpStatusCode.Created, ct)).Id);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(k.Url, k.Bad, ct), k.BadField, ct);

            var one = $"{k.Url}/{created.Id}";
            await ExpectFieldAsync(await sct.PutAsJsonAsync(one, k.Body(code, "LOCAL", null, "No version"), ct), "rowVersion", ct);
            var edited = await Read(await sct.PutAsJsonAsync(one, k.Body(code, "LOCAL", created.RowVersion, "Edited"), ct), HttpStatusCode.OK, ct);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync(one, k.Body(code, "LOCAL", created.RowVersion, "Stale"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync(one, k.Body(code, "IICL", edited.RowVersion, "Clash"), ct)).StatusCode);   // moving onto the IICL row

            Assert.Equal(HttpStatusCode.OK, (await viewOnly.GetAsync(k.Url, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewOnly.PutAsJsonAsync(one, k.Body(code, "LOCAL", edited.RowVersion, "Nope"), ct)).StatusCode);
            Assert.DoesNotContain((await other.GetFromJsonAsync<List<Row>>($"{k.Url}?includeInactive=true", ct))!, r => r.Id == created.Id);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(one, k.Body(code, "LOCAL", edited.RowVersion, "Theirs"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(RowVersions.WithVersion(one, edited.RowVersion), ct)).StatusCode);

            Assert.Equal(HttpStatusCode.BadRequest, (await sct.DeleteAsync(one, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.DeleteAsync(RowVersions.WithVersion(one, created.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await sct.DeleteAsync(RowVersions.WithVersion(one, edited.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await sct.DeleteAsync(RowVersions.WithVersion(one, edited.RowVersion), ct)).StatusCode);
        }
        finally
        {
            foreach (var r in (await sct.GetFromJsonAsync<List<Row>>($"{k.Url}?includeInactive=true", ct))!.Where(r => ids.Contains(r.Id)))
                await sct.DeleteAsync(RowVersions.WithVersion($"{k.Url}/{r.Id}", r.RowVersion), ct);
        }
    }

    private sealed record Row(Guid Id, string Code, string CodeStandard, string RowVersion);
}
