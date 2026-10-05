using System.Net;
using System.Net.Http.Json;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// The gate's VAS panel (GATE_IN_COMPLETION_PLAN A3): the clerk sees an order type's gate VAS without the
/// commercial master-data permission. SCT's GATE TEST (gecko_master dev_09) offers VASWASH and VASSEAL.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class GateVasApiTests(TosApiFactory api)
{
    [Fact]
    public async Task A_gate_clerk_reads_an_order_types_gate_VAS()
    {
        var ct = TestContext.Current.CancellationToken;
        var clerk = await api.ClientForAsync(TosApiFactory.SctGateLcb);

        // The commercial screen is not the clerk's …
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync("/api/master/order-types/GATE%20TEST", ct)).StatusCode);

        // … the gate's own list is.
        var response = await clerk.GetAsync("/api/tos/gate/vas?orderTypeCode=gate%20test", ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        var vas = (await response.Content.ReadFromJsonAsync<List<GateVasResponse>>(ct))!;
        Assert.Equal(new[] { "VASSEAL", "VASWASH" }, vas.Select(v => v.ChargeCode).Distinct().Order());
        Assert.All(vas, v =>
        {
            Assert.False(string.IsNullOrWhiteSpace(v.Description));
            Assert.False(string.IsNullOrWhiteSpace(v.BillTo));
            Assert.Contains("EMPTY drop-off", v.OfferedOn);
        });
    }

    /// <summary>The damage panel: the master-data code lists are not the clerk's either (mdm.equipment.view is tenant-wide).</summary>
    [Fact]
    public async Task A_gate_clerk_reads_the_damage_codes_for_the_damage_panel()
    {
        var ct = TestContext.Current.CancellationToken;
        var clerk = await api.ClientForAsync(TosApiFactory.SctGateLcb);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync("/api/master/damage-codes", ct)).StatusCode);

        var response = await clerk.GetAsync("/api/tos/gate/damage-codes", ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        var codes = (await response.Content.ReadFromJsonAsync<GateDamageCodesResponse>(ct))!;
        Assert.True(Assert.Single(codes.DamageCodes, d => d.DamageCode == "HO").MakesUnserviceable);
        Assert.False(Assert.Single(codes.DamageCodes, d => d.DamageCode == "SC").MakesUnserviceable);
        Assert.Contains("DRR", codes.Locations);
        Assert.Contains("DRG", codes.Components);
    }

    [Fact]
    public async Task An_unknown_order_type_is_a_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var clerk = await api.ClientForAsync(TosApiFactory.SctGateLcb);
        Assert.Equal(HttpStatusCode.BadRequest, (await clerk.GetAsync("/api/tos/gate/vas?orderTypeCode=NO%20SUCH%20TYPE", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await clerk.GetAsync("/api/tos/gate/vas", ct)).StatusCode);
    }
}
