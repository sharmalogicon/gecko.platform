using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Gecko.MasterData.Tests.Api.MasterLifecycle;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// Yard layout (Tier 3): blocks in a yard, rows in a block. Throwaway ZZ… blocks
/// in SCT's fixture yards; the fixture blocks (with their 416 slots) are only read.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class YardLayoutApiTests(MasterDataApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly Guid SctLkr01 = Guid.Parse("785AE785-33A5-F111-9B0D-00919E4766D5");

    private static async Task<Guid> YardAsync(HttpClient client, Guid branch, string code, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/master/yards?branchId={branch}&activeOnly=false", ct));
        return doc.RootElement.GetProperty("items").EnumerateArray().Single(y => y.GetProperty("yardCode").GetString() == code).GetProperty("yardId").GetGuid();
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expected, $"expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web)!;
    }

    private static async Task RemoveBlockAsync(HttpClient client, Guid yardId, string code, CancellationToken ct)
    {
        var block = (await client.GetFromJsonAsync<List<Block>>($"/api/master/yards/{yardId}/blocks", ct))!.SingleOrDefault(b => b.BlockCode == code);
        if (block is null) return;
        foreach (var r in (await client.GetFromJsonAsync<List<Row>>($"/api/master/yard-blocks/{block.YardBlockId}/rows", ct))!)
            await client.DeleteAsync(RowVersions.WithVersion($"/api/master/yard-rows/{r.YardRowId}", r.RowVersion), ct);
        var now = (await client.GetFromJsonAsync<List<Block>>($"/api/master/yards/{yardId}/blocks", ct))!.Single(b => b.BlockCode == code);
        await client.DeleteAsync(RowVersions.WithVersion($"/api/master/yard-blocks/{now.YardBlockId}", now.RowVersion), ct);
    }

    [Fact]
    public async Task A_block_and_its_rows_live_their_life_through_the_api()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var yard = await YardAsync(sct, SctLcb01, "Y-MNR", ct);
        var code = NewCode("ZB", 6);
        var blocks = $"/api/master/yards/{yard}/blocks";
        try
        {
            var block = await Read<Block>(await sct.PostAsJsonAsync(blocks, new { blockCode = code.ToLowerInvariant(), maxRows = 2, maxBays = 10, maxTiers = 4, isReeferBlock = true, displayColorHex = "#00aa55" }, ct), HttpStatusCode.Created, ct);
            Assert.Equal((code, 0, 0), (block.BlockCode, block.RowCount, block.SlotCount));
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PostAsJsonAsync(blocks, new { blockCode = code }, ct)).StatusCode);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(blocks, new { blockCode = NewCode("ZB", 6), allocatedSizeFt = 30 }, ct), "allocatedSizeFt", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(blocks, new { blockCode = NewCode("ZB", 6), displayColorHex = "green" }, ct), "displayColorHex", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(blocks, new { blockCode = NewCode("ZB", 6), allocatedToPartyCode = "NO-SUCH-PARTY" }, ct), "allocatedToPartyCode", ct);

            var rows = $"/api/master/yard-blocks/{block.YardBlockId}/rows";
            var r1 = await Read<Row>(await sct.PostAsJsonAsync(rows, new { rowLabel = "a", isReeferRow = true, reeferPlugCount = 8 }, ct), HttpStatusCode.Created, ct);
            Assert.Equal("A", r1.RowLabel);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PostAsJsonAsync(rows, new { rowLabel = "A" }, ct)).StatusCode);
            await Read<Row>(await sct.PostAsJsonAsync(rows, new { rowLabel = "B" }, ct), HttpStatusCode.Created, ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(rows, new { rowLabel = "C" }, ct), "rowLabel", ct);   // the block holds 2

            var current = (await sct.GetFromJsonAsync<List<Block>>(blocks, ct))!.Single(b => b.BlockCode == code);
            Assert.Equal(2, current.RowCount);
            await ExpectFieldAsync(await sct.PutAsJsonAsync($"/api/master/yard-blocks/{block.YardBlockId}", new { blockCode = code, maxRows = 1, rowVersion = current.RowVersion }, ct), "maxRows", ct);
            var edited = await Read<Block>(await sct.PutAsJsonAsync($"/api/master/yard-blocks/{block.YardBlockId}", new { blockCode = code, maxRows = 3, rowVersion = current.RowVersion }, ct), HttpStatusCode.OK, ct);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync($"/api/master/yard-blocks/{block.YardBlockId}", new { blockCode = code, maxRows = 4, rowVersion = current.RowVersion }, ct)).StatusCode);

            var renamed = await Read<Row>(await sct.PutAsJsonAsync($"/api/master/yard-rows/{r1.YardRowId}", new { rowLabel = "A1", isBlocked = true, rowVersion = r1.RowVersion }, ct), HttpStatusCode.OK, ct);
            Assert.Equal(("A1", true), (renamed.RowLabel, renamed.IsBlocked));
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync($"/api/master/yard-rows/{r1.YardRowId}", new { rowLabel = "A2", rowVersion = r1.RowVersion }, ct)).StatusCode);

            // A block with rows stays until its rows go.
            Assert.Equal(HttpStatusCode.Conflict, (await sct.DeleteAsync(RowVersions.WithVersion($"/api/master/yard-blocks/{block.YardBlockId}", edited.RowVersion), ct)).StatusCode);
        }
        finally { await RemoveBlockAsync(sct, yard, code, ct); }
        Assert.DoesNotContain((await sct.GetFromJsonAsync<List<Block>>(blocks, ct))!, b => b.BlockCode == code);
    }

    [Fact]
    public async Task A_fixture_block_with_slots_cannot_be_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var yard = await YardAsync(sct, SctLcb01, "Y-EMPTY", ct);
        var a = (await sct.GetFromJsonAsync<List<Block>>($"/api/master/yards/{yard}/blocks", ct))!.Single(b => b.BlockCode == "A");
        Assert.True(a.SlotCount > 0);
        Assert.Equal(HttpStatusCode.Conflict, (await sct.DeleteAsync(RowVersions.WithVersion($"/api/master/yard-blocks/{a.YardBlockId}", a.RowVersion), ct)).StatusCode);
        var row = (await sct.GetFromJsonAsync<List<Row>>($"/api/master/yard-blocks/{a.YardBlockId}/rows", ct))!.First(r => r.SlotCount > 0);
        Assert.Equal(HttpStatusCode.Conflict, (await sct.DeleteAsync(RowVersions.WithVersion($"/api/master/yard-rows/{row.YardRowId}", row.RowVersion), ct)).StatusCode);
        await ExpectFieldAsync(await sct.PutAsJsonAsync($"/api/master/yard-rows/{row.YardRowId}", new { rowLabel = row.RowLabel + "X", rowVersion = row.RowVersion }, ct), "rowLabel", ct);
    }

    [Fact]
    public async Task A_layout_is_edited_by_a_manager_of_its_depot_and_stays_inside_the_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var ops = await api.ClientForAsync(MasterDataApiFactory.SctOpsLcb);
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var lcb = await YardAsync(sct, SctLcb01, "Y-MNR", ct);
        var lkr = await YardAsync(sct, SctLkr01, "Y-EXPORT", ct);
        var code = NewCode("ZB", 6);
        try
        {
            Assert.Equal(HttpStatusCode.Created, (await ops.PostAsJsonAsync($"/api/master/yards/{lcb}/blocks", new { blockCode = code }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ops.PostAsJsonAsync($"/api/master/yards/{lkr}/blocks", new { blockCode = code }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/master/yards/{lcb}/blocks", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/master/yards/{lcb}/blocks", new { blockCode = code }, ct)).StatusCode);
        }
        finally
        {
            await RemoveBlockAsync(sct, lcb, code, ct);
            await RemoveBlockAsync(sct, lkr, code, ct);
        }
    }

    private sealed record Block(Guid YardBlockId, string BlockCode, int RowCount, int SlotCount, string RowVersion);
    private sealed record Row(Guid YardRowId, string RowLabel, bool IsBlocked, int SlotCount, string RowVersion);
}
