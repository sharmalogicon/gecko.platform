using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// The equipment vocabulary and the ISO 6346 mapping, through the real host.
///
/// The test that matters most is <see cref="Iso_code_resolves_to_a_different_local_type_per_tenant"/>:
/// it is the whole MDM design in one assertion. If ISO codes were stored per
/// tenant, or local types were global, that test is the one that breaks.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class EquipmentTypeApiTests(MasterDataApiFactory api)
{
    private const string Base = "/api/master/equipment-types";

    // dev_03_master_equipment.sql: SCT says 20GP, SIAM-COMMERCIAL says 20DV, for the same box.
    private const string SharedIsoCode = "22G1";

    // Real ISO 6346 codes that NO fixture tenant maps, so a test may claim and
    // release them without depending on what dev_03/dev_04 happened to seed.
    private const string FreeIsoCodeA = "22G2";
    private const string FreeIsoCodeB = "22G3";

    // Valid ISO, mapped by nobody — the "ask an admin, do not guess" case.
    private const string UnmappedIsoCode = "42G2";

    [Fact]
    public async Task Each_tenant_sees_only_its_own_vocabulary()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);

        var sctCodes = (await sct.GetFromJsonAsync<Paged<EquipmentType>>($"{Base}?pageSize=200", ct))!.Items.Select(t => t.TypeCode).ToList();
        var otherCodes = (await other.GetFromJsonAsync<Paged<EquipmentType>>($"{Base}?pageSize=200", ct))!.Items.Select(t => t.TypeCode).ToList();

        Assert.Contains("20GP", sctCodes);
        Assert.DoesNotContain("20DV", sctCodes);

        Assert.Contains("20DV", otherCodes);
        Assert.DoesNotContain("20GP", otherCodes);
    }

    /// <summary>
    /// One global ISO code, two tenants, two different local answers. This is why
    /// lookup.iso_container_code has no tenant_id and equipment_type does.
    /// </summary>
    [Fact]
    public async Task Iso_code_resolves_to_a_different_local_type_per_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);

        var forSct = await sct.GetFromJsonAsync<Resolution>($"{Base}/resolve/{SharedIsoCode}", ct);
        var forOther = await other.GetFromJsonAsync<Resolution>($"{Base}/resolve/{SharedIsoCode}", ct);

        Assert.Equal("20GP", forSct!.TypeCode);
        Assert.Equal("20DV", forOther!.TypeCode);
        Assert.NotEqual(forSct.EquipmentTypeId, forOther.EquipmentTypeId);
        Assert.True(forSct.KnownToStandard);
    }

    /// <summary>
    /// A real ISO code this tenant has not mapped must NOT look the same as rubbish.
    /// One means "ask an admin to map it", the other means "reject the message".
    /// </summary>
    [Fact]
    public async Task Unmapped_but_valid_iso_code_is_distinguished_from_an_invalid_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        // A genuine ISO 6346 code that no fixture tenant maps. It used to be 25R1
        // until dev_04 brought in Vector's real vocabulary — SCT's 20RQ claimed it,
        // which is exactly the kind of drift a realistic fixture causes and the
        // reason this constant is named rather than inline.
        var unmapped = await sct.GetFromJsonAsync<Resolution>($"{Base}/resolve/{UnmappedIsoCode}", ct);
        Assert.True(unmapped!.KnownToStandard);
        Assert.Null(unmapped.TypeCode);
        Assert.Contains("not mapped", unmapped.Note!, StringComparison.OrdinalIgnoreCase);

        var rubbish = await sct.GetFromJsonAsync<Resolution>($"{Base}/resolve/ZZZZ", ct);
        Assert.False(rubbish!.KnownToStandard);
        Assert.Null(rubbish.TypeCode);
    }

    [Fact]
    public async Task Superseded_1984_code_still_resolves_and_reports_its_successor()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        // 2200 is ISO 1984. Partners still send it; the depot must accept it and
        // know what to send back instead.
        var resolved = await sct.GetFromJsonAsync<Resolution>($"{Base}/resolve/2200", ct);

        Assert.Equal("20GP", resolved!.TypeCode);
        Assert.False(string.IsNullOrEmpty(resolved.SupersededBy));
    }

    [Fact]
    public async Task Create_read_update_and_delete_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var created = await CreateTypeAsync(sct, NewTypeCode(), ct);
        try
        {
            Assert.Empty(created.IsoCodes);

            var fetched = await sct.GetFromJsonAsync<Detail>($"{Base}/{created.Type.EquipmentTypeId}", ct);
            Assert.Equal(created.Type.TypeCode, fetched!.Type.TypeCode);

            var update = await sct.PutAsJsonAsync($"{Base}/{created.Type.EquipmentTypeId}",
                UpdateBody(fetched.Type.RowVersion, "Updated by test"), ct);
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);

            var updated = (await update.Content.ReadFromJsonAsync<EquipmentType>(ct))!;
            Assert.Equal("Updated by test", updated.DescriptionEn);
            Assert.NotEqual(fetched.Type.RowVersion, updated.RowVersion);
        }
        finally
        {
            var deleted = await RowVersions.DeleteCurrentAsync(sct, $"{Base}/{created.Type.EquipmentTypeId}", ct);
            Assert.Equal(HttpStatusCode.NoContent, deleted!.StatusCode);
        }
    }

    /// <summary>
    /// gecko_master puts ROWVERSION on every tenant table. If the endpoint ignored
    /// it, two admins editing the same equipment type would silently overwrite each
    /// other and the column would be decoration.
    /// </summary>
    [Fact]
    public async Task Stale_row_version_loses_the_race()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var created = await CreateTypeAsync(sct, NewTypeCode(), ct);

        try
        {
            var stale = created.Type.RowVersion;

            var first = await sct.PutAsJsonAsync($"{Base}/{created.Type.EquipmentTypeId}", UpdateBody(stale, "First writer"), ct);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            var second = await sct.PutAsJsonAsync($"{Base}/{created.Type.EquipmentTypeId}", UpdateBody(stale, "Second writer"), ct);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

            var current = await sct.GetFromJsonAsync<Detail>($"{Base}/{created.Type.EquipmentTypeId}", ct);
            Assert.Equal("First writer", current!.Type.DescriptionEn);
        }
        finally
        {
            await RowVersions.DeleteCurrentAsync(sct, $"{Base}/{created.Type.EquipmentTypeId}", ct);
        }
    }

    [Fact]
    public async Task Duplicate_type_code_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var response = await sct.PostAsJsonAsync(Base, CreateBody("20GP"), ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_iso_type_group_is_rejected_as_a_field_error()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var body = CreateBody(NewTypeCode()) with { IsoGroupCode = "ZZ" };
        var response = await sct.PostAsJsonAsync(Base, body, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("isoGroupCode", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Iso_mapping_demands_exactly_one_default_outbound_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var created = await CreateTypeAsync(sct, NewTypeCode(), ct);

        try
        {
            var url = $"{Base}/{created.Type.EquipmentTypeId}/iso-codes";

            var none = await sct.PutAsJsonAsync(url, new { isoCodes = new[] { new { isoCode = FreeIsoCodeA, isDefaultOutbound = false } } }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);

            var two = await sct.PutAsJsonAsync(url, new
            {
                isoCodes = new[]
                {
                    new { isoCode = FreeIsoCodeA, isDefaultOutbound = true },
                    new { isoCode = FreeIsoCodeB, isDefaultOutbound = true },
                },
            }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, two.StatusCode);
        }
        finally
        {
            await RowVersions.DeleteCurrentAsync(sct, $"{Base}/{created.Type.EquipmentTypeId}", ct);
        }
    }

    [Fact]
    public async Task Iso_code_already_mapped_to_another_type_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var created = await CreateTypeAsync(sct, NewTypeCode(), ct);

        try
        {
            // 22G1 belongs to SCT's 20GP. One ISO code, one local type — that is
            // uq_equipment_type_iso__iso, and the endpoint must say so clearly
            // rather than let SQL raise a duplicate-key error.
            var response = await sct.PutAsJsonAsync($"{Base}/{created.Type.EquipmentTypeId}/iso-codes",
                new { isoCodes = new[] { new { isoCode = SharedIsoCode, isDefaultOutbound = true } } }, ct);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("20GP", await response.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            await RowVersions.DeleteCurrentAsync(sct, $"{Base}/{created.Type.EquipmentTypeId}", ct);
        }
    }

    [Fact]
    public async Task Iso_code_outside_the_standard_cannot_be_mapped()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var created = await CreateTypeAsync(sct, NewTypeCode(), ct);

        try
        {
            var response = await sct.PutAsJsonAsync($"{Base}/{created.Type.EquipmentTypeId}/iso-codes",
                new { isoCodes = new[] { new { isoCode = "9Z9Z", isDefaultOutbound = true } } }, ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await RowVersions.DeleteCurrentAsync(sct, $"{Base}/{created.Type.EquipmentTypeId}", ct);
        }
    }

    [Fact]
    public async Task Iso_mapping_can_be_replaced_wholesale()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var created = await CreateTypeAsync(sct, NewTypeCode(), ct);
        var url = $"{Base}/{created.Type.EquipmentTypeId}/iso-codes";

        try
        {
            var first = await sct.PutAsJsonAsync(url, new
            {
                rowVersion = created.Type.RowVersion,
                isoCodes = new[]
                {
                    new { isoCode = FreeIsoCodeA, isDefaultOutbound = false },
                    new { isoCode = FreeIsoCodeB, isDefaultOutbound = true },
                },
            }, ct);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            // Replacing the mapping is an edit of the type: the next write needs the version it moved to.
            var afterFirst = (await first.Content.ReadFromJsonAsync<Detail>(ct))!;
            Assert.NotEqual(created.Type.RowVersion, afterFirst.Type.RowVersion);

            // Drop one, keep one, and move the default. The dropped row has to be
            // soft-deleted and FLUSHED before the survivors are re-saved, or the
            // filtered unique index rejects the batch.
            var second = await sct.PutAsJsonAsync(url, new
            {
                rowVersion = afterFirst.Type.RowVersion,
                isoCodes = new[] { new { isoCode = FreeIsoCodeA, isDefaultOutbound = true } },
            }, ct);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            var mapping = (await second.Content.ReadFromJsonAsync<Detail>(ct))!.IsoCodes;
            Assert.Single(mapping);
            Assert.Equal(FreeIsoCodeA, mapping[0].IsoCode);
            Assert.True(mapping[0].IsDefaultOutbound);

            // And the released code is free for another type to claim.
            var resolved = await sct.GetFromJsonAsync<Resolution>($"{Base}/resolve/{FreeIsoCodeB}", ct);
            Assert.Null(resolved!.TypeCode);
            Assert.True(resolved.KnownToStandard);
        }
        finally
        {
            await RowVersions.DeleteCurrentAsync(sct, $"{Base}/{created.Type.EquipmentTypeId}", ct);
        }
    }

    [Fact]
    public async Task Equipment_type_still_used_by_containers_cannot_be_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        var types = (await sct.GetFromJsonAsync<Paged<EquipmentType>>($"{Base}?pageSize=200", ct))!.Items;
        var inUse = types.Single(t => t.TypeCode == "20GP");

        // At its current version, so the refusal is about the containers using it.
        var response = (await RowVersions.DeleteCurrentAsync(sct, $"{Base}/{inUse.EquipmentTypeId}", ct))!;

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("container", await response.Content.ReadAsStringAsync(ct), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>mdm.equipment.view is not mdm.equipment.manage. EDI_COORDINATOR holds the first only.</summary>
    [Fact]
    public async Task View_permission_does_not_grant_manage()
    {
        var ct = TestContext.Current.CancellationToken;
        var edi = await api.ClientForAsync(MasterDataApiFactory.SctEdi);

        var read = await edi.GetAsync($"{Base}?pageSize=5", ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var write = await edi.PostAsJsonAsync(Base, CreateBody(NewTypeCode()), ct);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    /// <summary>
    /// A branch-scoped role grants no tenant-wide permission (ClaimsBuilder only
    /// puts tenant-wide role permissions in `prm`), so master data is closed to it.
    /// </summary>
    [Fact]
    public async Task Branch_scoped_user_cannot_read_master_data()
    {
        var ct = TestContext.Current.CancellationToken;
        var ops = await api.ClientForAsync(MasterDataApiFactory.SctOpsLcb);

        var response = await ops.GetAsync($"{Base}?pageSize=5", ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_callers_are_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var anonymous = api.CreateClient();

        var response = await anonymous.GetAsync(Base, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- helpers -------------------------------------------------------

    /// <summary>
    /// A fresh code per run, so the test does not depend on whether an earlier run
    /// managed to clean up. Deletes are soft, so the tombstones accumulate in dev —
    /// they are invisible to every query through the soft-delete filter.
    /// </summary>
    private static string NewTypeCode() => $"T{Guid.NewGuid():N}"[..7].ToUpperInvariant();

    private static async Task<Detail> CreateTypeAsync(HttpClient client, string code, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(Base, CreateBody(code), ct);
        // Carry the body into the failure message: a bare "expected Created, got 500"
        // sends you to the server logs for something ProblemDetails already said.
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"POST {Base} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        var created = (await response.Content.ReadFromJsonAsync<Detail>(ct))!;
        Assert.Equal(code, created.Type.TypeCode);
        return created;
    }

    private static CreateBodyRecord CreateBody(string code) => new(
        code, "Created by test", 20, "STANDARD", "GP", 1.0m, 2200, 28280, 30480);

    private static object UpdateBody(string rowVersion, string description) => new
    {
        rowVersion,
        descriptionEn = description,
        lengthFt = 20,
        heightClass = "STANDARD",
        isoGroupCode = "GP",
        teu = 1.0m,
        isReefer = false,
        isOog = false,
        isTank = false,
        isActive = true,
    };

    private sealed record CreateBodyRecord(
        [property: JsonPropertyName("typeCode")] string TypeCode,
        [property: JsonPropertyName("descriptionEn")] string DescriptionEn,
        [property: JsonPropertyName("lengthFt")] int LengthFt,
        [property: JsonPropertyName("heightClass")] string HeightClass,
        [property: JsonPropertyName("isoGroupCode")] string IsoGroupCode,
        [property: JsonPropertyName("teu")] decimal Teu,
        [property: JsonPropertyName("tareWeightKg")] decimal TareWeightKg,
        [property: JsonPropertyName("maxPayloadKg")] decimal MaxPayloadKg,
        [property: JsonPropertyName("maxGrossKg")] decimal MaxGrossKg);

    private sealed record Paged<T>(List<T> Items, int Page, int PageSize, int TotalCount);

    private sealed record EquipmentType(Guid EquipmentTypeId, string TypeCode, string DescriptionEn, bool IsActive, string RowVersion);

    private sealed record Detail(EquipmentType Type, List<IsoMapping> IsoCodes);

    private sealed record IsoMapping(string IsoCode, bool IsDefaultOutbound, string? IsoDescription);

    private sealed record Resolution(
        string IsoCode, bool KnownToStandard, Guid? EquipmentTypeId, string? TypeCode,
        string? DescriptionEn, string? SupersededBy, string? Note);
}
