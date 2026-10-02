using System.Net;
using System.Net.Http.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// The container registry through the real host, and the gate's validate call —
/// which has to answer for numbers the depot has never seen.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class ContainerApiTests(MasterDataApiFactory api)
{
    private const string Base = "/api/master/containers";

    // Real numbers seeded by dev_04 from Vector's registry.
    private const string KnownContainer = "AMFU8539517";   // 40HC
    private const string KnownReefer = "AMCU9295585";      // 40RH

    [Fact]
    public async Task Registry_lists_the_real_fleet_mix()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var page = await sct.GetFromJsonAsync<Paged<ContainerRow>>($"{Base}?pageSize=200", ct);

        Assert.NotNull(page);
        Assert.Contains(page!.Items, c => c.ContainerNo == KnownContainer);
        // 40HC and 20GP dominate a real depot; the fixture keeps that shape.
        Assert.True(page.Items.Count(c => c.TypeCode == "40HC") >= 3);
    }

    [Fact]
    public async Task A_container_is_addressed_by_its_number_not_a_guid()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var container = await sct.GetFromJsonAsync<ContainerRow>($"{Base}/{KnownContainer}", ct);

        Assert.Equal(KnownContainer, container!.ContainerNo);
        Assert.Equal("AMFU", container.Prefix);
        Assert.Equal("40HC", container.TypeCode);
        Assert.True(container.IsCheckDigitValid);
    }

    [Fact]
    public async Task Lower_case_and_spaced_input_resolves_to_the_same_container()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var container = await sct.GetFromJsonAsync<ContainerRow>($"{Base}/amfu8539517", ct);

        Assert.Equal(KnownContainer, container!.ContainerNo);
    }

    // ── the gate's question ─────────────────────────────────────────────────

    [Fact]
    public async Task Validate_recognises_a_registered_container()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var result = await sct.GetFromJsonAsync<Validation>($"{Base}/validate/{KnownReefer}", ct);

        Assert.True(result!.IsWellFormed);
        Assert.True(result.IsCheckDigitValid);
        Assert.True(result.IsKnown);
        Assert.Equal("40RH", result.TypeCode);
        Assert.True(result.WouldBeAcceptedAtGate);
        Assert.Null(result.Note);
    }

    /// <summary>
    /// The four outcomes have to stay distinguishable. A gate that treats "we have
    /// never seen this box" the same as "you typed it wrong" either turns away real
    /// traffic or accepts rubbish.
    /// </summary>
    [Fact]
    public async Task Validate_separates_unknown_from_misread_from_malformed()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        // Valid number, never seen here — registerable on the fly.
        var unknown = await sct.GetFromJsonAsync<Validation>($"{Base}/validate/CSQU3054383", ct);
        Assert.True(unknown!.IsWellFormed);
        Assert.True(unknown.IsCheckDigitValid);
        Assert.False(unknown.IsKnown);
        Assert.True(unknown.WouldBeAcceptedAtGate);
        Assert.Contains("not yet in the registry", unknown.Note!, StringComparison.OrdinalIgnoreCase);

        // Well formed but the check digit is wrong — an OCR misread, not a new box.
        var misread = await sct.GetFromJsonAsync<Validation>($"{Base}/validate/CSQU3054384", ct);
        Assert.True(misread!.IsWellFormed);
        Assert.False(misread.IsCheckDigitValid);
        Assert.Equal(3, misread.ExpectedCheckDigit);
        Assert.False(misread.WouldBeAcceptedAtGate);
        Assert.Contains("re-scan", misread.Note!, StringComparison.OrdinalIgnoreCase);

        // Not a container number at all.
        var malformed = await sct.GetFromJsonAsync<Validation>($"{Base}/validate/NOTACONTAINER", ct);
        Assert.False(malformed!.IsWellFormed);
        Assert.False(malformed.WouldBeAcceptedAtGate);
    }

    [Fact]
    public async Task Validate_reports_whether_the_prefix_belongs_to_a_known_party()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        // MRKU is registered to Maersk in dev_02.
        var registered = await sct.GetFromJsonAsync<Validation>($"{Base}/validate/MRKU4122336", ct);
        Assert.Equal("MRKU", registered!.Prefix);
        Assert.True(registered.PrefixIsRegistered);
        Assert.Equal("MAEU", registered.PrefixOwnerCode);

        // CSQU belongs to nobody here — reported, but not fatal, because SCT does
        // not enforce prefixes (mdm.container_prefix_enforced defaults to false).
        var unregistered = await sct.GetFromJsonAsync<Validation>($"{Base}/validate/CSQU3054383", ct);
        Assert.False(unregistered!.PrefixIsRegistered);
        Assert.True(unregistered.WouldBeAcceptedAtGate);
    }

    // ── writes ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Register_read_update_and_delete_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var number = await NewValidContainerNoAsync(sct, ct);

        var created = await CreateAsync(sct, number, ct);
        try
        {
            Assert.Equal("IN_SERVICE", created.Status);
            Assert.True(created.IsCheckDigitValid);

            var fetched = await sct.GetFromJsonAsync<ContainerRow>($"{Base}/{number}", ct);

            var update = await sct.PutAsJsonAsync($"{Base}/{number}", new
            {
                rowVersion = fetched!.RowVersion,
                ownershipType = "LINE_OWNED",
                status = "OFF_HIRED",
                manufacturer = "Updated by test",
            }, ct);
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);

            var updated = (await update.Content.ReadFromJsonAsync<ContainerRow>(ct))!;
            Assert.Equal("OFF_HIRED", updated.Status);
            // status_changed_at answers "when did this box leave the fleet", so it
            // must move when the status moves.
            Assert.NotNull(updated.StatusChangedAt);
        }
        finally
        {
            var deleted = await RowVersions.DeleteCurrentAsync(sct, $"{Base}/{number}", ct);
            Assert.Equal(HttpStatusCode.NoContent, deleted!.StatusCode);
        }
    }

    [Fact]
    public async Task A_containers_fixed_ports_are_known_ports_stored_as_sent_and_cleared_by_sending_none()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var number = await NewValidContainerNoAsync(sct, ct);

        var created = await CreateAsync(sct, number, ct);
        try
        {
            Assert.Empty(created.FixedPortCodes!);   // a new box may go to any port

            object Put(string rowVersion, string[]? fixedPortCodes) => new { rowVersion, ownershipType = "LINE_OWNED", status = "IN_SERVICE", fixedPortCodes };
            var unknown = await sct.PutAsJsonAsync($"{Base}/{number}", Put(created.RowVersion, ["SGSIN", "ZZNOPE"]), ct);
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            Assert.Contains("fixedPortCodes", await unknown.Content.ReadAsStringAsync(ct));

            var set = await sct.PutAsJsonAsync($"{Base}/{number}", Put(created.RowVersion, ["sgsin", "THLCH", "SGSIN"]), ct);
            Assert.True(set.StatusCode == HttpStatusCode.OK, await set.Content.ReadAsStringAsync(ct));
            var designated = (await set.Content.ReadFromJsonAsync<ContainerRow>(ct))!;
            Assert.Equal(["SGSIN", "THLCH"], designated.FixedPortCodes);
            Assert.Equal(["SGSIN", "THLCH"], (await sct.GetFromJsonAsync<ContainerRow>($"{Base}/{number}", ct))!.FixedPortCodes);

            var cleared = await sct.PutAsJsonAsync($"{Base}/{number}", Put(designated.RowVersion, null), ct);
            Assert.Empty((await cleared.Content.ReadFromJsonAsync<ContainerRow>(ct))!.FixedPortCodes!);
        }
        finally
        {
            await RowVersions.DeleteCurrentAsync(sct, $"{Base}/{number}", ct);
        }
    }

    [Fact]
    public async Task A_wrong_check_digit_is_refused_when_the_tenant_enforces_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var response = await sct.PostAsJsonAsync(Base, new
        {
            containerNo = "CSQU3054384",     // correct digit is 3
            ownershipType = "LINE_OWNED",
        }, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"got {(int)response.StatusCode}: {body}");
        Assert.True(body.Contains("expected 3"), $"body was: {body}");
        // The error names the setting, so an admin can find the lever rather than
        // guessing why the depot refuses a box it can see.
        Assert.Contains("gate.enforce_check_digit", body);
    }

    [Fact]
    public async Task A_leased_container_must_name_its_lessor()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var number = await NewValidContainerNoAsync(sct, ct);

        var missing = await sct.PostAsJsonAsync(Base, new { containerNo = number, ownershipType = "LEASED" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("lessor", await missing.Content.ReadAsStringAsync(ct), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_owned_container_must_not_name_a_lessor()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var number = await NewValidContainerNoAsync(sct, ct);
        var lessorId = (await sct.GetFromJsonAsync<ContainerRow>($"{Base}/{KnownContainer}", ct))!.LessorPartyId;

        var response = await sct.PostAsJsonAsync(Base, new
        {
            containerNo = number,
            ownershipType = "LINE_OWNED",
            lessorPartyId = lessorId,
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The box's ISO code must be one its own equipment type answers to, or the
    /// outbound CODECO describes a different box than the registry holds.
    /// </summary>
    [Fact]
    public async Task An_iso_code_the_equipment_type_does_not_map_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var number = await NewValidContainerNoAsync(sct, ct);
        var fortyFootHighCube = (await sct.GetFromJsonAsync<ContainerRow>($"{Base}/{KnownContainer}", ct))!.EquipmentTypeId;

        var response = await sct.PostAsJsonAsync(Base, new
        {
            containerNo = number,
            ownershipType = "LINE_OWNED",
            equipmentTypeId = fortyFootHighCube,
            isoCode = "22G1",                 // a 20ft code on a 40HC
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not mapped", await response.Content.ReadAsStringAsync(ct), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Registering_the_same_container_twice_is_a_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var response = await sct.PostAsJsonAsync(Base, new
        {
            containerNo = KnownContainer,
            ownershipType = "LINE_OWNED",
        }, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Stale_row_version_loses_the_race()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var number = await NewValidContainerNoAsync(sct, ct);
        var created = await CreateAsync(sct, number, ct);

        try
        {
            // Each PUT must actually change something: EF issues no UPDATE for a
            // no-op, so the row version is never compared and a stale one "wins"
            // by not racing anybody. That is correct, and it is why both writes
            // below set a different manufacturer.
            object Body(string manufacturer) => new
            {
                rowVersion = created.RowVersion,
                ownershipType = "LINE_OWNED",
                status = "IN_SERVICE",
                manufacturer,
            };

            Assert.Equal(HttpStatusCode.OK, (await sct.PutAsJsonAsync($"{Base}/{number}", Body("First writer"), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync($"{Base}/{number}", Body("Second writer"), ct)).StatusCode);
        }
        finally
        {
            await RowVersions.DeleteCurrentAsync(sct, $"{Base}/{number}", ct);
        }
    }

    [Fact]
    public async Task View_permission_does_not_grant_manage()
    {
        var ct = TestContext.Current.CancellationToken;
        var edi = await api.ClientForAsync(MasterDataApiFactory.SctEdi);

        Assert.Equal(HttpStatusCode.OK, (await edi.GetAsync($"{Base}?pageSize=5", ct)).StatusCode);

        var write = await edi.PostAsJsonAsync(Base, new { containerNo = "CSQU3054383", ownershipType = "LINE_OWNED" }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_this_tenants_containers()
    {
        var ct = TestContext.Current.CancellationToken;
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);

        var response = await other.GetAsync($"{Base}/{KnownContainer}", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A well-formed number with a correct check digit that is not already in the
    /// registry. Built by asking the API to validate candidates, so the test never
    /// depends on a hard-coded number staying unregistered.
    /// </summary>
    private static async Task<string> NewValidContainerNoAsync(HttpClient client, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var serial = Random.Shared.Next(100000, 999999);
            for (var digit = 0; digit <= 9; digit++)
            {
                var candidate = $"TSTU{serial}{digit}";
                var check = await client.GetFromJsonAsync<Validation>($"{Base}/validate/{candidate}", ct);
                if (check is { IsCheckDigitValid: true, IsKnown: false }) return candidate;
            }
        }

        throw new InvalidOperationException("Could not generate an unused valid container number.");
    }

    private static async Task<ContainerRow> CreateAsync(HttpClient client, string number, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(Base, new { containerNo = number, ownershipType = "LINE_OWNED" }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"POST {Base} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<ContainerRow>(ct))!;
    }

    private sealed record Paged<T>(List<T> Items, int Page, int PageSize, int TotalCount);

    private sealed record ContainerRow(
        Guid ContainerId, string ContainerNo, string Prefix, Guid? EquipmentTypeId, string? TypeCode,
        string? IsoCode, Guid? OwnerPartyId, string? OwnerCode, Guid? LessorPartyId, string? LessorCode,
        string OwnershipType, string Status, DateTimeOffset? StatusChangedAt, bool IsCheckDigitValid, string RowVersion,
        List<string>? FixedPortCodes = null);

    private sealed record Validation(
        string ContainerNo, bool IsWellFormed, bool IsCheckDigitValid, int? ExpectedCheckDigit,
        string? Prefix, bool PrefixIsRegistered, string? PrefixOwnerCode,
        bool IsKnown, Guid? ContainerId, string? TypeCode, string? Status,
        bool WouldBeAcceptedAtGate, string? Note);
}
