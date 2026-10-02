using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Gecko.MasterData.Tests.Api.MasterLifecycle;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// Haulier charge terms (gecko_master 22, gate-in-vector-parity.md §3.3): a
/// haulier's CASH / CREDIT override per order type × movement × charge. Written
/// against SCT's fixture haulier HAU-SHT, whose terms these tests own: every row
/// they create is soft-deleted again.
///
/// Fixture shape relied on: ERTN has one step (GIE) and raises GATEFEE at order
/// level; IMP CY/CY raises GATEFEE at FULL_OUT only; MAEU is a line, not a haulier.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class HaulierChargeTermApiTests(MasterDataApiFactory api)
{
    private const string Terms = "/api/master/haulier-charge-terms";
    private const string Haulier = "HAU-SHT";

    private static object Body(string orderType, string movement, string charge, string term = "CREDIT",
        string haulier = Haulier, string? rowVersion = null) =>
        new { haulierCode = haulier, orderTypeCode = orderType, movementCode = movement, chargeCode = charge, paymentTermCode = term, rowVersion };

    private static async Task<Term> Read(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expected, $"expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<Term>(body, JsonSerializerOptions.Web)!;
    }

    private static async Task ClearAsync(HttpClient sct, CancellationToken ct)
    {
        foreach (var t in (await sct.GetFromJsonAsync<List<Term>>($"{Terms}?haulierCode={Haulier}", ct))!)
            Assert.Equal(HttpStatusCode.NoContent,
                (await sct.DeleteAsync(RowVersions.WithVersion($"{Terms}/{t.HaulierChargeTermId}", t.RowVersion), ct)).StatusCode);
    }

    [Fact]
    public async Task A_haulier_term_lives_its_life_and_the_gate_reads_it_by_haulier_and_order_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        await ClearAsync(sct, ct);
        try
        {
            // Order-level charge (movement_id NULL on the order type) and a movement-level one.
            var credit = await Read(await sct.PostAsJsonAsync(Terms, Body("ertn", "gie", "gatefee"), ct), HttpStatusCode.Created, ct);
            Assert.Equal((Haulier, "ERTN", "GIE", "GATEFEE", "CREDIT"),
                (credit.HaulierCode, credit.OrderTypeCode, credit.MovementCode, credit.ChargeCode, credit.PaymentTermCode));
            Assert.False(string.IsNullOrEmpty(credit.HaulierName));
            await Read(await sct.PostAsJsonAsync(Terms, Body("IMP CY/CY", "FULL_OUT", "GATEFEE", "CASH"), ct), HttpStatusCode.Created, ct);

            // The gate's read: by (haulier, order type) only.
            var forErtn = (await sct.GetFromJsonAsync<List<Term>>($"{Terms}?haulierCode={Haulier}&orderTypeCode=ERTN", ct))!;
            Assert.Equal(credit.HaulierChargeTermId, Assert.Single(forErtn).HaulierChargeTermId);
            Assert.Equal(credit.RowVersion, (await Read(await sct.GetAsync($"{Terms}/{credit.HaulierChargeTermId}", ct), HttpStatusCode.OK, ct)).RowVersion);

            // The business key is unique.
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PostAsJsonAsync(Terms, Body("ERTN", "GIE", "GATEFEE", "CASH"), ct)).StatusCode);

            // Optimistic concurrency on update.
            var cash = await Read(await sct.PutAsJsonAsync($"{Terms}/{credit.HaulierChargeTermId}", Body("ERTN", "GIE", "GATEFEE", "CASH", rowVersion: credit.RowVersion), ct), HttpStatusCode.OK, ct);
            Assert.Equal("CASH", cash.PaymentTermCode);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync($"{Terms}/{credit.HaulierChargeTermId}", Body("ERTN", "GIE", "GATEFEE", "CREDIT", rowVersion: credit.RowVersion), ct)).StatusCode);
            await ExpectFieldAsync(await sct.PutAsJsonAsync($"{Terms}/{credit.HaulierChargeTermId}", Body("ERTN", "GIE", "GATEFEE", "CREDIT"), ct), "rowVersion", ct);

            // Delete: needs the version, refuses a stale one, then the row is gone.
            await ExpectFieldAsync(await sct.DeleteAsync($"{Terms}/{cash.HaulierChargeTermId}", ct), "rowVersion", ct);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.DeleteAsync(RowVersions.WithVersion($"{Terms}/{cash.HaulierChargeTermId}", credit.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await sct.DeleteAsync(RowVersions.WithVersion($"{Terms}/{cash.HaulierChargeTermId}", cash.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await sct.GetAsync($"{Terms}/{cash.HaulierChargeTermId}", ct)).StatusCode);
            Assert.Empty((await sct.GetFromJsonAsync<List<Term>>($"{Terms}?haulierCode={Haulier}&orderTypeCode=ERTN", ct))!);
        }
        finally { await ClearAsync(sct, ct); }
    }

    [Fact]
    public async Task Every_soft_reference_is_checked_and_the_400_names_the_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        try
        {
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Terms, Body("ERTN", "GIE", "GATEFEE", haulier: "NO-SUCH-PARTY"), ct), "haulierCode", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Terms, Body("ERTN", "GIE", "GATEFEE", haulier: "MAEU"), ct), "haulierCode", ct);   // a line, not a haulier
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Terms, Body("NO-SUCH-OT", "GIE", "GATEFEE"), ct), "orderTypeCode", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Terms, Body("ERTN", "NO-SUCH-MV", "GATEFEE"), ct), "movementCode", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Terms, Body("ERTN", "FULL_IN", "GATEFEE"), ct), "movementCode", ct);   // not a step of ERTN
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Terms, Body("ERTN", "GIE", "NO-SUCH-CC"), ct), "chargeCode", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Terms, Body("ERTN", "GIE", "LIFTOUT"), ct), "chargeCode", ct);   // ERTN does not raise it
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Terms, Body("IMP CY/CY", "FULL_IN", "GATEFEE"), ct), "chargeCode", ct);   // raised at FULL_OUT only
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Terms, Body("ERTN", "GIE", "GATEFEE", term: "COD"), ct), "paymentTermCode", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Terms, new { orderTypeCode = "ERTN", movementCode = "GIE", chargeCode = "GATEFEE", paymentTermCode = "CASH" }, ct), "haulierCode", ct);
        }
        finally { await ClearAsync(sct, ct); }
    }

    [Fact]
    public async Task Terms_need_the_commercial_permissions_and_stay_inside_the_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var edi = await api.ClientForAsync(MasterDataApiFactory.SctEdi);   // no mdm.commercial.*
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        await ClearAsync(sct, ct);
        try
        {
            var term = await Read(await sct.PostAsJsonAsync(Terms, Body("ERTN", "GIE", "GATEFEE"), ct), HttpStatusCode.Created, ct);
            var url = $"{Terms}/{term.HaulierChargeTermId}";

            Assert.Equal(HttpStatusCode.Forbidden, (await edi.GetAsync($"{Terms}?haulierCode={Haulier}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.GetAsync(url, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.PostAsJsonAsync(Terms, Body("IMP CY/CY", "FULL_OUT", "GATEFEE"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.PutAsJsonAsync(url, Body("ERTN", "GIE", "GATEFEE", "CASH", rowVersion: term.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.DeleteAsync(RowVersions.WithVersion(url, term.RowVersion), ct)).StatusCode);

            // Another tenant: SCT's row does not exist for them, and SCT's haulier is not theirs to use.
            Assert.Empty((await other.GetFromJsonAsync<List<Term>>($"{Terms}?haulierCode={Haulier}", ct))!);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(url, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(url, Body("ERTN", "GIE", "GATEFEE", "CASH", rowVersion: term.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(RowVersions.WithVersion(url, term.RowVersion), ct)).StatusCode);
            await ExpectFieldAsync(await other.PostAsJsonAsync(Terms, Body("ERTN", "GIE", "GATEFEE"), ct), "haulierCode", ct);

            // Still intact for SCT.
            Assert.Equal("CREDIT", (await Read(await sct.GetAsync(url, ct), HttpStatusCode.OK, ct)).PaymentTermCode);
        }
        finally { await ClearAsync(sct, ct); }
    }

    private sealed record Term(
        Guid HaulierChargeTermId, string HaulierCode, string? HaulierName, string OrderTypeCode,
        string MovementCode, string ChargeCode, string PaymentTermCode, string RowVersion);
}
