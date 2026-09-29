using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// Order types end to end, as the order-type screens use them: the header, the
/// gate steps with their five rules, the charges each step raises — and the
/// refusals each editor must be able to put on the right cell (movements[i].x,
/// charges[i].x). Codes are Vector-shaped phrases with a '/', on purpose.
/// Every order type here is a throwaway.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class OrderTypeApiTests(MasterDataApiFactory api)
{
    private const string OrderTypes = "/api/master/order-types";

    private static string NewCode() => $"ZZ CY/CY {Guid.NewGuid():N}"[..17].ToUpperInvariant();
    private static string Url(string code) => $"{OrderTypes}/{Uri.EscapeDataString(code)}";

    private static object Header(string code, string description = "Order type test", bool isActive = true, string? rowVersion = null) => new
    {
        orderTypeCode = code, descriptionEn = description, descriptionLocal = "ทดสอบ",
        directionCode = "EXPORT", cargoClassCode = "GENERAL", serviceCode = "CY-CY", bookingTypeCode = "EXPORT_BOOKING",
        isActive, rowVersion,
    };

    private static object[] TwoSteps() =>
    [
        new { movementCode = "GOE", sequenceNo = 1, pudoMode = "PICKUP", checkSealNo = false, checkGrossWeight = true },
        new { movementCode = "GIF", sequenceNo = 2, pudoMode = "DROPOFF", checkSealNo = true, checkGrossWeight = true, requireVesselVoyage = true },
    ];

    [Fact]
    public async Task An_order_type_lives_its_whole_life_through_the_api()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = NewCode();
        var url = Url(code);
        try
        {
            var created = await Read<Detail>(await sct.PostAsJsonAsync(OrderTypes, Header(code), ct), HttpStatusCode.Created, ct);
            Assert.Equal((code, "CY-CY", "EXPORT_BOOKING"), (created.OrderType.OrderTypeCode, created.OrderType.ServiceCode, created.OrderType.BookingTypeCode));

            var withSteps = await Read<Detail>(await sct.PutAsJsonAsync($"{url}/movements",
                new { rowVersion = created.OrderType.RowVersion, movements = TwoSteps() }, ct), HttpStatusCode.OK, ct);
            Assert.Equal(["GOE", "GIF"], withSteps.Movements.OrderBy(m => m.SequenceNo).Select(m => m.MovementCode));
            var ladenIn = withSteps.Movements.Single(m => m.MovementCode == "GIF");
            Assert.True(ladenIn is { CheckSealNo: true, RequireVesselVoyage: true, PudoMode: "DROPOFF" });

            var withCharges = await Read<Detail>(await sct.PutAsJsonAsync($"{url}/charges", new
            {
                rowVersion = withSteps.OrderType.RowVersion,
                charges = new object[]
                {
                    new { chargeCode = "LIFTIN", paymentTo = "LINE", movementCode = "GIF", isDefault = true },
                    new { chargeCode = "LIFTIN", paymentTo = "CUSTOMER", paymentTermCode = "CASH", isDefault = false, isOptional = true, isValueAddedService = true },
                },
            }, ct), HttpStatusCode.OK, ct);
            Assert.Equal(2, withCharges.Charges.Count);
            Assert.Contains(withCharges.Charges, c => c is { MovementCode: null, IsValueAddedService: true, PaymentTermCode: "CASH" });

            var renamed = await Read<OrderType>(await sct.PutAsJsonAsync(url, Header(code, "Renamed", rowVersion: withCharges.OrderType.RowVersion), ct), HttpStatusCode.OK, ct);
            Assert.Equal("Renamed", renamed.DescriptionEn);

            // Deactivate is the normal way out: gone from the default list, still readable, steps kept.
            var inactive = await Read<OrderType>(await sct.PutAsJsonAsync(url, Header(code, "Renamed", isActive: false, rowVersion: renamed.RowVersion), ct), HttpStatusCode.OK, ct);
            Assert.False(inactive.IsActive);
            Assert.DoesNotContain(code, await sct.GetStringAsync($"{OrderTypes}?search={Uri.EscapeDataString(code)}", ct));
            Assert.Equal(2, (await sct.GetFromJsonAsync<Detail>(url, ct))!.Movements.Count);

            Assert.Equal(HttpStatusCode.NoContent, (await sct.DeleteAsync(RowVersions.WithVersion(url, inactive.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await sct.GetAsync(url, ct)).StatusCode);
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, url, ct); }
    }

    [Fact]
    public async Task Header_errors_name_their_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        object With(string field, string value)
        {
            var body = new Dictionary<string, object?>
            {
                ["orderTypeCode"] = NewCode(), ["descriptionEn"] = "x", ["directionCode"] = "EXPORT", ["cargoClassCode"] = "GENERAL",
            };
            body[field] = value;
            return body;
        }

        await ExpectFieldAsync(await sct.PostAsJsonAsync(OrderTypes, With("orderTypeCode", "lower case"), ct), "orderTypeCode", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(OrderTypes, With("directionCode", "SIDEWAYS"), ct), "directionCode", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(OrderTypes, With("cargoClassCode", "FEATHERS"), ct), "cargoClassCode", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(OrderTypes, With("serviceCode", "NOPE"), ct), "serviceCode", ct);
        await ExpectFieldAsync(await sct.PostAsJsonAsync(OrderTypes, With("bookingTypeCode", "NOPE"), ct), "bookingTypeCode", ct);
    }

    /// <summary>A step error lands on its row and column, so the editor marks the one bad cell.</summary>
    [Fact]
    public async Task Step_errors_name_their_row_and_column()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = NewCode();
        var url = Url(code);
        try
        {
            var created = await Read<Detail>(await sct.PostAsJsonAsync(OrderTypes, Header(code), ct), HttpStatusCode.Created, ct);
            var version = created.OrderType.RowVersion;
            var first = new { movementCode = "GOE", sequenceNo = 1, pudoMode = (string?)null };

            async Task Expect(object second, string field) =>
                await ExpectFieldAsync(await sct.PutAsJsonAsync($"{url}/movements", new { rowVersion = version, movements = new[] { first, second } }, ct), field, ct);

            await Expect(new { movementCode = "NOPE", sequenceNo = 2, pudoMode = (string?)null }, "movements[1].movementCode");
            await Expect(new { movementCode = "GOE", sequenceNo = 2, pudoMode = (string?)null }, "movements[1].movementCode");     // same movement twice
            await Expect(new { movementCode = "GIF", sequenceNo = 1, pudoMode = (string?)null }, "movements[1].sequenceNo");       // same position twice
            await Expect(new { movementCode = "GIF", sequenceNo = 2, pudoMode = (string?)"TELEPORT" }, "movements[1].pudoMode");
            await Expect(new { movementCode = "GIF", sequenceNo = 3, pudoMode = (string?)null }, "movements");                    // 1, 3: a gap
            // Row-level annotations reach the rows now: sequence 0 is out of [Range(1, 99)].
            await Expect(new { movementCode = "GIF", sequenceNo = 0, pudoMode = (string?)null }, "movements[1].sequenceNo");

            Assert.Empty((await sct.GetFromJsonAsync<Detail>(url, ct))!.Movements);
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, url, ct); }
    }

    [Fact]
    public async Task Charge_errors_name_their_row_and_column()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = NewCode();
        var url = Url(code);
        try
        {
            var created = await Read<Detail>(await sct.PostAsJsonAsync(OrderTypes, Header(code), ct), HttpStatusCode.Created, ct);
            var version = (await Read<Detail>(await sct.PutAsJsonAsync($"{url}/movements",
                new { rowVersion = created.OrderType.RowVersion, movements = TwoSteps() }, ct), HttpStatusCode.OK, ct)).OrderType.RowVersion;
            var ok = new { chargeCode = "LIFTIN", paymentTo = "LINE", movementCode = (string?)"GIF", paymentTermCode = (string?)null, isDefault = true, isOptional = false };

            async Task Expect(object second, string field) =>
                await ExpectFieldAsync(await sct.PutAsJsonAsync($"{url}/charges", new { rowVersion = version, charges = new[] { ok, second } }, ct), field, ct);

            await Expect(ok with { chargeCode = "NOPE" }, "charges[1].chargeCode");
            await Expect(ok with { paymentTo = "SHIPPING" }, "charges[1].paymentTo");
            await Expect(ok with { paymentTo = "CUSTOMER", paymentTermCode = "NET30" }, "charges[1].paymentTermCode");
            // GOF is a real movement, but not a step of this order type: the charge would never be raised.
            await Expect(ok with { paymentTo = "CUSTOMER", movementCode = "GOF" }, "charges[1].movementCode");
            await Expect(ok with { paymentTo = "CUSTOMER", isOptional = true }, "charges[1]");        // default AND optional
            await Expect(ok, "charges[1]");                                                          // the same row twice

            Assert.Empty((await sct.GetFromJsonAsync<Detail>(url, ct))!.Charges);
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, url, ct); }
    }

    /// <summary>Dropping a step would silently strand the charges pinned to it.</summary>
    [Fact]
    public async Task A_step_with_charges_pinned_to_it_cannot_be_dropped()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = NewCode();
        var url = Url(code);
        try
        {
            var created = await Read<Detail>(await sct.PostAsJsonAsync(OrderTypes, Header(code), ct), HttpStatusCode.Created, ct);
            var steps = await Read<Detail>(await sct.PutAsJsonAsync($"{url}/movements",
                new { rowVersion = created.OrderType.RowVersion, movements = TwoSteps() }, ct), HttpStatusCode.OK, ct);
            var charged = await Read<Detail>(await sct.PutAsJsonAsync($"{url}/charges", new
            {
                rowVersion = steps.OrderType.RowVersion,
                charges = new[] { new { chargeCode = "LIFTIN", paymentTo = "LINE", movementCode = "GIF" } },
            }, ct), HttpStatusCode.OK, ct);

            var dropLadenIn = await sct.PutAsJsonAsync($"{url}/movements", new
            {
                rowVersion = charged.OrderType.RowVersion,
                movements = new[] { new { movementCode = "GOE", sequenceNo = 1 } },
            }, ct);
            await ExpectFieldAsync(dropLadenIn, "movements", ct);
            Assert.Contains("GIF still has charges", await dropLadenIn.Content.ReadAsStringAsync(ct));
            Assert.Equal(2, (await sct.GetFromJsonAsync<Detail>(url, ct))!.Movements.Count);
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, url, ct); }
    }

    [Fact]
    public async Task A_stale_header_edit_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var code = NewCode();
        var url = Url(code);
        try
        {
            var created = await Read<Detail>(await sct.PostAsJsonAsync(OrderTypes, Header(code), ct), HttpStatusCode.Created, ct);
            Assert.Equal(HttpStatusCode.OK, (await sct.PutAsJsonAsync(url, Header(code, "First writer", rowVersion: created.OrderType.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync(url, Header(code, "Second writer", rowVersion: created.OrderType.RowVersion), ct)).StatusCode);
            Assert.Equal("First writer", (await sct.GetFromJsonAsync<Detail>(url, ct))!.OrderType.DescriptionEn);
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, url, ct); }
    }

    [Fact]
    public async Task Without_commercial_permissions_nothing_is_readable_or_writable()
    {
        var ct = TestContext.Current.CancellationToken;
        var edi = await api.ClientForAsync(MasterDataApiFactory.SctEdi);
        var url = Url("EXP CY/CY");

        Assert.Equal(HttpStatusCode.Forbidden, (await edi.GetAsync(OrderTypes, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await edi.GetAsync("/api/master/vocabulary/order-types", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await edi.PostAsJsonAsync(OrderTypes, Header(NewCode()), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await edi.PutAsJsonAsync(url, Header("EXP CY/CY"), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await edi.PutAsJsonAsync($"{url}/movements", new { movements = TwoSteps() }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await edi.PutAsJsonAsync($"{url}/charges", new { charges = Array.Empty<object>() }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await edi.DeleteAsync(url, ct)).StatusCode);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_or_touch_an_order_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var code = NewCode();
        var url = Url(code);
        try
        {
            var created = await Read<Detail>(await sct.PostAsJsonAsync(OrderTypes, Header(code), ct), HttpStatusCode.Created, ct);
            var version = created.OrderType.RowVersion;

            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(url, ct)).StatusCode);
            Assert.DoesNotContain(code, await other.GetStringAsync($"{OrderTypes}?search={Uri.EscapeDataString(code)}&includeInactive=true", ct));
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(url, Header(code, "Hijack", rowVersion: version), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{url}/movements", new { rowVersion = version, movements = TwoSteps() }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(RowVersions.WithVersion(url, version), ct)).StatusCode);
        }
        finally { await RowVersions.DeleteCurrentAsync(sct, url, ct); }
    }

    [Fact]
    public async Task The_vocabulary_is_what_the_api_accepts()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var v = (await sct.GetFromJsonAsync<Vocabulary>("/api/master/vocabulary/order-types", ct))!;

        Assert.Contains("EXPORT", v.Directions.Select(d => d.Code));
        Assert.Contains("GENERAL", v.CargoClasses.Select(c => c.Code));
        Assert.Contains("EXPORT_BOOKING", v.BookingTypes.Select(b => b.Code));
        Assert.Contains("CY-CY", v.ServiceTypes.Select(s => s.Code));
        Assert.Equal(["DROPOFF", "NONE", "PICKUP", "PICKUP_DROPOFF"], v.PudoModes.Select(p => p.Code).Order());
        Assert.Contains(v.Movements, m => m is { Code: "GIF", Direction: "IN", FullEmpty: "FULL" });
        Assert.Contains("LIFTIN", v.ChargeCodes.Select(c => c.Code));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expected,
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}: expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web)!;
    }

    private static async Task ExpectFieldAsync(HttpResponseMessage response, string field, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400 on {field}, got {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        var keys = doc.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
        Assert.True(keys.Contains(field), $"expected an error on '{field}', got [{string.Join(", ", keys)}]: {body}");
    }

    private sealed record OrderType(Guid OrderTypeId, string OrderTypeCode, string DescriptionEn, string DirectionCode,
        string? ServiceCode, string CargoClassCode, string? BookingTypeCode, bool IsActive, string RowVersion);
    private sealed record Step(string MovementCode, short SequenceNo, bool CheckSealNo, bool CheckGrossWeight, bool RequireVesselVoyage, string? PudoMode);
    private sealed record Charge(string ChargeCode, string? MovementCode, string PaymentTo, string? PaymentTermCode, bool IsValueAddedService);
    private sealed record Detail(OrderType OrderType, List<Step> Movements, List<Charge> Charges);

    private sealed record Coded(string Code);
    private sealed record MovementRow(string Code, string Direction, string FullEmpty);
    private sealed record Vocabulary(List<Coded> Directions, List<Coded> CargoClasses, List<Coded> BookingTypes, List<Coded> ServiceTypes,
        List<Coded> PudoModes, List<MovementRow> Movements, List<Coded> ChargeCodes);
}
