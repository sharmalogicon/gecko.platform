using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// Charge codes end to end, as the charge-code screens use them: create, read,
/// edit, replace the billing variants, deactivate, delete — and the refusals the
/// screens must be able to put on the right input (a field name per error,
/// variants[i].x for a row of the matrix).
/// Every row is a throwaway; LIFTIN is only read.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class ChargeCodeApiTests(MasterDataApiFactory api)
{
    private const string Charges = "/api/master/charge-codes";

    private static string NewCode() => $"CT{Guid.NewGuid():N}"[..10].ToUpperInvariant();

    private static object Base(string code, string description = "Charge code test", bool isActive = true, string? rowVersion = null) => new
    {
        chargeCode = code, descriptionEn = description, descriptionLocal = "ทดสอบ",
        moduleCode = "TOS", chargeType = "LIFT", chargeCategory = "EMPTY", billingUnitCode = "PER_CONTAINER",
        isActive, rowVersion,
    };

    [Fact]
    public async Task A_charge_code_lives_its_whole_life_through_the_api()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = NewCode();
        var url = $"{Charges}/{code}";
        try
        {
            var created = await Read<Detail>(await sct.PostAsJsonAsync(Charges, Base(code), ct), HttpStatusCode.Created, ct);
            Assert.Equal((code, "EMPTY", "ทดสอบ"), (created.Charge.ChargeCode, created.Charge.ChargeCategory, created.Charge.DescriptionLocal));
            Assert.Empty(created.Variants);

            var updated = await Read<Charge>(await sct.PutAsJsonAsync(url, Base(code, "Renamed", rowVersion: created.Charge.RowVersion), ct), HttpStatusCode.OK, ct);
            Assert.Equal("Renamed", updated.DescriptionEn);

            // Cash and credit to the same payer are two variants of ONE charge — the matrix Vector hid in the code string.
            var withVariants = await Read<Detail>(await sct.PutAsJsonAsync($"{url}/variants", new
            {
                rowVersion = updated.RowVersion,
                variants = new object[]
                {
                    new { billTo = "customer", paymentTermCode = "cash", taxCode = "vat7" },
                    new { billTo = "CUSTOMER", paymentTermCode = "CREDIT", taxCode = "VAT7", creditTermDays = 30, revenueGl = "4100", legacyChargeCode = "CT-CR" },
                },
            }, ct), HttpStatusCode.OK, ct);
            Assert.Equal(2, withVariants.Variants.Count);
            var credit = Assert.Single(withVariants.Variants, v => v.PaymentTermCode == "CREDIT");
            Assert.Equal(("CUSTOMER", "VAT7", (short?)30, "4100", "CT-CR"), (credit.BillTo, credit.TaxCode, credit.CreditTermDays, credit.RevenueGl, credit.LegacyChargeCode));

            var read = await sct.GetFromJsonAsync<Detail>(url, ct);
            Assert.Equal(withVariants.Charge.RowVersion, read!.Charge.RowVersion);

            // Deactivate is the normal way out: gone from the default list, still readable, still in "all".
            var inactive = await Read<Charge>(await sct.PutAsJsonAsync(url, Base(code, "Renamed", isActive: false, rowVersion: read.Charge.RowVersion), ct), HttpStatusCode.OK, ct);
            Assert.False(inactive.IsActive);
            Assert.DoesNotContain(code, await sct.GetStringAsync($"{Charges}?search={code}", ct));
            Assert.Contains(code, await sct.GetStringAsync($"{Charges}?search={code}&includeInactive=true", ct));

            var deleted = await sct.DeleteAsync(RowVersions.WithVersion(url, inactive.RowVersion), ct);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await sct.GetAsync(url, ct)).StatusCode);
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, url, ct); }
    }

    [Fact]
    public async Task Header_errors_name_their_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        await ExpectFieldAsync(await sct.PostAsJsonAsync(Charges, Base("lower case"), ct), "chargeCode", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Charges, new
        {
            chargeCode = NewCode(), descriptionEn = "x", moduleCode = "NOPE", chargeType = "LIFT", billingUnitCode = "PER_CONTAINER",
        }, ct), "moduleCode", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Charges, new
        {
            chargeCode = NewCode(), descriptionEn = "x", moduleCode = "TOS", chargeType = "LIFT", billingUnitCode = "PER_FORTNIGHT",
        }, ct), "billingUnitCode", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(Charges, new
        {
            chargeCode = NewCode(), descriptionEn = "x", moduleCode = "TOS", chargeType = "LOLO", billingUnitCode = "PER_CONTAINER",
        }, ct), "chargeType", ct);

        var taken = await sct.PostAsJsonAsync(Charges, Base("LIFTIN"), ct);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
    }

    /// <summary>A matrix error lands on its row and column, so the editor can mark the one bad cell.</summary>
    [Fact]
    public async Task Variant_errors_name_their_row_and_column()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = NewCode();
        var url = $"{Charges}/{code}";
        try
        {
            var created = await Read<Detail>(await sct.PostAsJsonAsync(Charges, Base(code), ct), HttpStatusCode.Created, ct);
            var version = created.Charge.RowVersion;
            var ok = new { billTo = "CUSTOMER", paymentTermCode = "CASH", taxCode = "VAT7" };

            async Task Expect(object bad, string field) =>
                await ExpectFieldAsync(await sct.PutAsJsonAsync($"{url}/variants", new { rowVersion = version, variants = new[] { ok, bad } }, ct), field, ct);

            await Expect(new { billTo = "FWD", paymentTermCode = "CREDIT" }, "variants[1].billTo");
            await Expect(new { billTo = "LINE", paymentTermCode = "NET30" }, "variants[1].paymentTermCode");
            await Expect(new { billTo = "LINE", paymentTermCode = "CREDIT", taxCode = "NOPE" }, "variants[1].taxCode");
            await Expect(new { billTo = "LINE", paymentTermCode = "CREDIT", withholdingTaxCode = "VAT7" }, "variants[1].withholdingTaxCode");
            await Expect(new { billTo = "customer", paymentTermCode = "cash" }, "variants[1]");                      // same pair as row 0
            // DataAnnotations on the ROW, not just the body: a 999-day credit term never reached validation before.
            await Expect(new { billTo = "LINE", paymentTermCode = "CREDIT", creditTermDays = 999 }, "variants[1].creditTermDays");

            // Nothing was replaced by any of the refusals.
            Assert.Empty((await sct.GetFromJsonAsync<Detail>(url, ct))!.Variants);
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, url, ct); }
    }

    [Fact]
    public async Task A_stale_edit_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = NewCode();
        var url = $"{Charges}/{code}";
        try
        {
            var created = await Read<Detail>(await sct.PostAsJsonAsync(Charges, Base(code), ct), HttpStatusCode.Created, ct);
            var first = await sct.PutAsJsonAsync(url, Base(code, "First writer", rowVersion: created.Charge.RowVersion), ct);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            var second = await sct.PutAsJsonAsync(url, Base(code, "Second writer", rowVersion: created.Charge.RowVersion), ct);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal("First writer", (await sct.GetFromJsonAsync<Detail>(url, ct))!.Charge.DescriptionEn);

            await ExpectFieldAsync(await sct.PutAsJsonAsync(url, Base(code, "No version"), ct), "rowVersion", ct);
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, url, ct); }
    }

    /// <summary>EDI_COORDINATOR holds no mdm.commercial.* — it cannot even read the billing vocabulary.</summary>
    [Fact]
    public async Task Without_commercial_permissions_nothing_is_readable_or_writable()
    {
        var ct = TestContext.Current.CancellationToken;
        var edi = await api.ClientForAsync(MasterDataApiFactory.SctEdi);
        const string url = $"{Charges}/LIFTIN";

        Assert.Equal(HttpStatusCode.Forbidden, (await edi.GetAsync(Charges, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await edi.GetAsync("/api/master/vocabulary/commercial", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await edi.PostAsJsonAsync(Charges, Base(NewCode()), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await edi.PutAsJsonAsync(url, Base("LIFTIN"), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await edi.PutAsJsonAsync($"{url}/variants", new { variants = Array.Empty<object>() }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await edi.DeleteAsync(url, ct)).StatusCode);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_or_touch_a_charge_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var code = NewCode();
        var url = $"{Charges}/{code}";
        try
        {
            var created = await Read<Detail>(await sct.PostAsJsonAsync(Charges, Base(code), ct), HttpStatusCode.Created, ct);

            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(url, ct)).StatusCode);
            Assert.DoesNotContain(code, await other.GetStringAsync($"{Charges}?search={code}&includeInactive=true", ct));
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(url, Base(code, "Hijack", rowVersion: created.Charge.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(RowVersions.WithVersion(url, created.Charge.RowVersion), ct)).StatusCode);

            // And the same code is free for the other tenant — codes are unique per tenant, not globally.
            var theirs = await other.PostAsJsonAsync(Charges, Base(code), ct);
            Assert.Equal(HttpStatusCode.Created, theirs.StatusCode);
            await RowVersions.DeleteCurrentAsync(other, url, ct);
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, url, ct); }
    }

    /// <summary>Everything the editor offers comes from here, so a screen never hard-codes a list the API would refuse.</summary>
    [Fact]
    public async Task The_vocabulary_is_what_the_api_accepts()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var vocabulary = (await sct.GetFromJsonAsync<Vocabulary>("/api/master/vocabulary/commercial", ct))!;

        Assert.Contains("TOS", vocabulary.Modules.Select(m => m.Code));
        Assert.DoesNotContain("REVENUE", vocabulary.Modules.Select(m => m.Code));        // does no depot work
        Assert.Contains("LIFT", vocabulary.ChargeTypes);
        Assert.Contains("LADEN", vocabulary.ChargeCategories);
        Assert.Contains("PER_CONTAINER", vocabulary.BillingUnits.Select(u => u.Code));
        Assert.Contains("CUSTOMER", vocabulary.BillToRoles.Select(r => r.Code));
        Assert.DoesNotContain("FWD", vocabulary.BillToRoles.Select(r => r.Code));
        Assert.Contains("CREDIT", vocabulary.PaymentTerms.Select(p => p.Code));
        Assert.Contains(vocabulary.TaxCodes, t => t is { Code: "VAT7", TaxType: "VAT" });
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expected,
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}: expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web)!;
    }

    /// <summary>400, and the error is filed under exactly <paramref name="field"/> (the key the UI looks up).</summary>
    private static async Task ExpectFieldAsync(HttpResponseMessage response, string field, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400 on {field}, got {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        var keys = doc.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
        Assert.True(keys.Contains(field), $"expected an error on '{field}', got [{string.Join(", ", keys)}]: {body}");
    }

    private sealed record Charge(Guid ChargeCodeId, string ChargeCode, string DescriptionEn, string? DescriptionLocal,
        string ModuleCode, string ChargeType, string ChargeCategory, string BillingUnitCode, bool IsByService, bool IsActive, string RowVersion);
    private sealed record Variant(string BillTo, string PaymentTermCode, string? TaxCode, string? WithholdingTaxCode,
        short? CreditTermDays, string? RevenueGl, string? CostGl, string? LegacyChargeCode);
    private sealed record Detail(Charge Charge, List<Variant> Variants);

    private sealed record Coded(string Code);
    private sealed record TaxRow(string Code, string TaxType);
    private sealed record Vocabulary(List<Coded> Modules, List<string> ChargeTypes, List<string> ChargeCategories,
        List<Coded> BillingUnits, List<Coded> BillToRoles, List<Coded> PaymentTerms, List<TaxRow> TaxCodes);
}
