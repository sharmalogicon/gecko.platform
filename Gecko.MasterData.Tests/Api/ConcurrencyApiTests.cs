using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// Optimistic concurrency beyond PUT: a DELETE and a child-set replace (charge
/// variants, order-type steps and charges, ISO mappings) must name the version
/// they saw, or two people editing the same master silently overwrite each other.
///   missing rowVersion → 400 on the field
///   stale rowVersion   → 409, and nothing changed
///   current rowVersion → the change, and a NEW version comes back
///
/// Every row here is a throwaway made by the test: a DELETE that ignored the
/// version would otherwise take a fixture row with it.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class ConcurrencyApiTests(MasterDataApiFactory api)
{
    /// <summary>Well-formed, never current: 8 bytes, like a real SQL Server rowversion.</summary>
    private const string StaleVersion = "AAAAAAAAAAE=";

    private static string NewCode(string prefix, int length = 8) => $"{prefix}{Guid.NewGuid():N}"[..length].ToUpperInvariant();

    // ── DELETE ──────────────────────────────────────────────────────────────

    public static TheoryData<string> Deletable() =>
        ["hold", "grade", "condition", "charge-code", "order-type", "equipment-type", "party", "tax-code", "movement", "service-type"];

    [Theory]
    [MemberData(nameof(Deletable))]
    public async Task A_delete_needs_the_current_row_version(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var (url, rowVersion) = await CreateAsync(sct, kind, ct);
        try
        {
            var missing = await sct.DeleteAsync(url, ct);
            await ExpectAsync(missing, HttpStatusCode.BadRequest, "rowVersion", ct);

            var stale = await sct.DeleteAsync(RowVersions.WithVersion(url, StaleVersion), ct);
            await ExpectAsync(stale, HttpStatusCode.Conflict, "changed since you loaded it", ct);

            Assert.True(await ExistsAsync(sct, kind, url, ct), $"{kind}: a refused delete must leave the row alone");

            var current = await sct.DeleteAsync(RowVersions.WithVersion(url, rowVersion), ct);
            await ExpectAsync(current, HttpStatusCode.NoContent, null, ct);
            Assert.False(await ExistsAsync(sct, kind, url, ct), $"{kind}: still visible after the delete");
            url = null;
        }
        finally
        {
            if (url is not null) await ForceDeleteAsync(sct, kind, url, ct);
        }
    }

    // ── child-set replace ───────────────────────────────────────────────────

    [Fact]
    public async Task Replacing_charge_variants_is_an_edit_of_the_charge_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var (url, v0) = await CreateAsync(sct, "charge-code", ct);
        try
        {
            object Body(string? rowVersion) => new
            {
                rowVersion,
                variants = new[] { new { billTo = "CUSTOMER", paymentTermCode = "CASH", taxCode = "VAT7" } },
            };
            await ReplaceAndExpectVersionAsync(sct, $"{url}/variants", Body, v0, ct);
        }
        finally { await ForceDeleteAsync(sct, "charge-code", url, ct); }
    }

    [Fact]
    public async Task Replacing_order_type_steps_or_charges_is_an_edit_of_the_order_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var (url, v0) = await CreateAsync(sct, "order-type", ct);
        try
        {
            object Steps(string? rowVersion) => new
            {
                rowVersion,
                movements = new[] { new { movementCode = "GIE", sequenceNo = 1 }, new { movementCode = "GOE", sequenceNo = 2 } },
            };
            var v1 = await ReplaceAndExpectVersionAsync(sct, $"{url}/movements", Steps, v0, ct);

            object Charges(string? rowVersion) => new
            {
                rowVersion,
                charges = new[] { new { chargeCode = "LIFTIN", paymentTo = "CUSTOMER", movementCode = "GIE" } },
            };
            // The steps save moved the version on: the charges editor must use the new one.
            var stale = await sct.PutAsJsonAsync($"{url}/charges", Charges(v0), ct);
            await ExpectAsync(stale, HttpStatusCode.Conflict, "changed since you loaded it", ct);
            await ReplaceAndExpectVersionAsync(sct, $"{url}/charges", Charges, v1, ct);
        }
        finally { await ForceDeleteAsync(sct, "order-type", url, ct); }
    }

    [Fact]
    public async Task Replacing_iso_mappings_is_an_edit_of_the_equipment_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var (url, v0) = await CreateAsync(sct, "equipment-type", ct);
        try
        {
            // 22B0 (dry bulk) is a published ISO 6346 code no tenant has mapped, so it is free to take.
            object Body(string? rowVersion) => new { rowVersion, isoCodes = new[] { new { isoCode = "22B0", isDefaultOutbound = true } } };
            await ReplaceAndExpectVersionAsync(sct, $"{url}/iso-codes", Body, v0, ct);
        }
        finally { await ForceDeleteAsync(sct, "equipment-type", url, ct); }
    }

    /// <summary>Missing → 400, current → 200 with a new version, the old one again → 409. Returns the new version.</summary>
    private static async Task<string> ReplaceAndExpectVersionAsync(
        HttpClient client, string url, Func<string?, object> body, string current, CancellationToken ct)
    {
        await ExpectAsync(await client.PutAsJsonAsync(url, body(null), ct), HttpStatusCode.BadRequest, "rowVersion", ct);

        var saved = await client.PutAsJsonAsync(url, body(current), ct);
        var json = await ExpectAsync(saved, HttpStatusCode.OK, null, ct);
        var next = RowVersions.In(json);
        Assert.NotEqual(current, next);

        await ExpectAsync(await client.PutAsJsonAsync(url, body(current), ct), HttpStatusCode.Conflict, "changed since you loaded it", ct);
        return next;
    }

    // ── throwaway rows ──────────────────────────────────────────────────────

    private static async Task<(string Url, string RowVersion)> CreateAsync(HttpClient client, string kind, CancellationToken ct)
    {
        const string root = "/api/master";
        var (listUrl, body, itemUrl) = kind switch
        {
            "hold" => Make($"{root}/holds", code => new
            {
                holdCode = code, descriptionEn = "Concurrency test", holdType = "OPERATIONS",
                blockingScope = "LOAD", releaseAuthority = "DEPOT_OPERATIONS",
            }, NewCode("H", 6)),
            "grade" => Make($"{root}/container-grades", code => new { gradeCode = code, descriptionEn = "Concurrency test", rankOrder = 50 }, NewCode("G", 6)),
            "condition" => Make($"{root}/container-conditions", code => new
            {
                conditionCode = code, descriptionEn = "Concurrency test", severity = 3, isServiceable = true,
            }, NewCode("C", 6)),
            "charge-code" => Make($"{root}/charge-codes", code => new
            {
                chargeCode = code, descriptionEn = "Concurrency test", moduleCode = "TOS", chargeType = "OTHER", billingUnitCode = "PER_CONTAINER",
            }, NewCode("CC")),
            "order-type" => Make($"{root}/order-types", code => new
            {
                orderTypeCode = code, descriptionEn = "Concurrency test", directionCode = "EXPORT", cargoClassCode = "GENERAL",
            }, NewCode("OT")),
            "tax-code" => Make($"{root}/tax-codes", code => new
            {
                taxCode = code, descriptionEn = "Concurrency test", countryCode = "TH", taxType = "VAT", ratePct = 7.0m,
                effectiveFrom = "2026-01-01",
            }, NewCode("TX")),
            "movement" => Make($"{root}/movements", code => new
            {
                movementCode = code, descriptionEn = "Concurrency test", fullEmpty = "EMPTY", direction = "IN", appliesToModule = "TOS",
            }, NewCode("MV")),
            "service-type" => Make($"{root}/service-types", code => new
            {
                serviceCode = code, descriptionEn = "Concurrency test", originForm = "CY", destinationForm = "CFS",
            }, NewCode("SV")),
            "equipment-type" => Make($"{root}/equipment-types", code => new
            {
                typeCode = code, descriptionEn = "Concurrency test", lengthFt = 20, heightClass = "STANDARD", isoGroupCode = "GP", teu = 1.0m,
            }, NewCode("T", 7)),
            "party" => ($"{root}/parties", (object)new { nameEn = "Concurrency Test Co., Ltd." }, (string?)null),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        var response = await client.PostAsJsonAsync(listUrl, body, ct);
        var json = await ExpectAsync(response, response.StatusCode is HttpStatusCode.Created ? HttpStatusCode.Created : HttpStatusCode.OK, null, ct);

        using var doc = JsonDocument.Parse(json);
        var url = kind switch
        {
            "equipment-type" => $"{listUrl}/{RowVersions.Find(doc.RootElement, "equipmentTypeId")}",
            "party" => $"{listUrl}/{Uri.EscapeDataString(RowVersions.Find(doc.RootElement, "partyCode")!)}",
            _ => itemUrl!,
        };
        return (url, RowVersions.In(json));

        static (string, object, string?) Make(string list, Func<string, object> body, string code) =>
            (list, body(code), $"{list}/{Uri.EscapeDataString(code)}");
    }

    /// <summary>Kinds with no GET-one are looked up in their (unpaged) list by the code at the end of the URL.</summary>
    private static async Task<bool> ExistsAsync(HttpClient client, string kind, string url, CancellationToken ct)
    {
        if (kind is "grade" or "condition" or "tax-code" or "movement" or "service-type")
        {
            var list = url[..url.LastIndexOf('/')];
            var code = Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]);
            var body = await client.GetStringAsync($"{list}?includeInactive=true", ct);
            return body.Contains($"\"{code}\"", StringComparison.Ordinal);
        }
        var response = await client.GetAsync(url, ct);
        return response.StatusCode == HttpStatusCode.OK;
    }

    /// <summary>Cleanup that works whatever state the test left: read the current version, then delete with it.</summary>
    private static async Task ForceDeleteAsync(HttpClient client, string kind, string url, CancellationToken ct) =>
        _ = kind is "grade" or "condition" or "tax-code" or "movement" or "service-type"
            ? await RowVersions.DeleteCurrentFromListAsync(client, url, ct)
            : await RowVersions.DeleteCurrentAsync(client, url, ct);

    private static async Task<string> ExpectAsync(HttpResponseMessage response, HttpStatusCode expected, string? contains, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expected,
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}: expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        if (contains is not null) Assert.Contains(contains, body, StringComparison.Ordinal);
        return body;
    }
}
