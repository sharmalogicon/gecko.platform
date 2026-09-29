using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Gecko.MasterData.Tests.Api.MasterLifecycle;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// Seal series, countries and public holidays (Tier 2) — the branch-scoped ones.
/// A seal range or a depot's holiday is edited by a manager AT that depot; a
/// holiday for every depot needs the tenant-wide grant.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class SealAndCalendarApiTests(MasterDataApiFactory api)
{
    private const string Seals = "/api/master/seal-ranges";
    private const string Holidays = "/api/master/public-holidays";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly Guid SctLkr01 = Guid.Parse("785AE785-33A5-F111-9B0D-00919E4766D5");

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expected, $"expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web)!;
    }

    /// <summary>A throwaway shipping line to own the test ranges; deleted (after its ranges) in finally.</summary>
    private static async Task<string> NewLineAsync(HttpClient client, CancellationToken ct) =>
        (await Read<LineDetail>(await client.PostAsJsonAsync("/api/master/shipping-lines", new { nameEn = "Test Seal Line" }, ct), HttpStatusCode.Created, ct)).Line.PartyCode;

    private static object Range(string line, string prefix, long start, long end, string? rv = null, Guid? branch = null, long? issued = null) =>
        new { branchId = branch ?? SctLcb01, partyCode = line, sealPrefix = prefix, seriesStart = start, seriesEnd = end, lastIssuedNumber = issued, rowVersion = rv };

    /// <summary>Deletes the line's unused ranges, then the line. Run after PurgeRangesAsync has taken out the used ones.</summary>
    private static async Task RemoveRangesAsync(HttpClient client, string line, CancellationToken ct)
    {
        foreach (var r in (await client.GetFromJsonAsync<List<SealRow>>($"{Seals}?partyCode={Uri.EscapeDataString(line)}&includeInactive=true", ct))!)
            await client.DeleteAsync(RowVersions.WithVersion($"{Seals}/{r.SealRangeId}", r.RowVersion), ct);
        await RowVersions.DeleteCurrentAsync(client, $"/api/master/parties/{Uri.EscapeDataString(line)}", ct);
    }

    [Fact]
    public async Task A_seal_range_lives_its_life_and_ranges_never_overlap()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var line = await NewLineAsync(sct, ct);
        var prefix = NewCode("ZS", 6);
        try
        {
            var range = await Read<SealRow>(await sct.PostAsJsonAsync(Seals, Range(line, prefix.ToLowerInvariant(), 1000, 1999), ct), HttpStatusCode.Created, ct);
            Assert.Equal((prefix, 1000L, 1999L, 1000L, line), (range.SealPrefix, range.SeriesStart, range.SeriesEnd, range.Remaining, range.PartyCode));

            // Overlapping — at this depot or another — is a 400 that names the range it hits; touching is fine.
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Seals, Range(line, prefix, 1500, 2500), ct), "seriesStart", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Seals, Range(line, prefix, 500, 1000, branch: SctLkr01), ct), "seriesStart", ct);
            var next = await Read<SealRow>(await sct.PostAsJsonAsync(Seals, Range(line, prefix, 2000, 2999), ct), HttpStatusCode.Created, ct);

            await ExpectFieldAsync(await sct.PostAsJsonAsync(Seals, Range(line, prefix, 5000, 4000), ct), "seriesEnd", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Seals, Range("NO-SUCH-PARTY", prefix, 7000, 7999), ct), "partyCode", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Seals, Range(line, prefix, 8000, 8999, issued: 9500), ct), "lastIssuedNumber", ct);

            var url = $"{Seals}/{range.SealRangeId}";
            await ExpectFieldAsync(await sct.PutAsJsonAsync(url, Range(line, prefix, 1000, 1999), ct), "rowVersion", ct);
            await ExpectFieldAsync(await sct.PutAsJsonAsync(url, Range(line, prefix, 1000, 1999, range.RowVersion, SctLkr01), ct), "branchId", ct);
            var issued = await Read<SealRow>(await sct.PutAsJsonAsync(url, Range(line, prefix, 1000, 1999, range.RowVersion, issued: 1009), ct), HttpStatusCode.OK, ct);
            Assert.Equal(990L, issued.Remaining);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync(url, Range(line, prefix, 1000, 1899, range.RowVersion), ct)).StatusCode);
            await ExpectFieldAsync(await sct.PutAsJsonAsync(url, Range(line, prefix, 1000, 1999, issued.RowVersion, issued: 1005), ct), "lastIssuedNumber", ct);

            // A range seals were issued from is kept; an unused one can go.
            Assert.Equal(HttpStatusCode.Conflict, (await sct.DeleteAsync(RowVersions.WithVersion(url, issued.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await sct.DeleteAsync(RowVersions.WithVersion($"{Seals}/{next.SealRangeId}", next.RowVersion), ct)).StatusCode);

            // Deactivated, the used range no longer blocks its numbers from a new range, and it no longer lists by default.
            var inactive = await Read<SealRow>(await sct.PutAsJsonAsync(url, new { branchId = SctLcb01, partyCode = line, sealPrefix = prefix, seriesStart = 1000, seriesEnd = 1999, lastIssuedNumber = 1009, isActive = false, rowVersion = issued.RowVersion }, ct), HttpStatusCode.OK, ct);
            Assert.False(inactive.IsActive);
            Assert.DoesNotContain((await sct.GetFromJsonAsync<List<SealRow>>($"{Seals}?partyCode={line}", ct))!, r => r.SealRangeId == range.SealRangeId);
        }
        finally { await PurgeRangesAsync(line, ct); await RemoveRangesAsync(sct, line, ct); }
    }

    [Fact]
    public async Task Seal_ranges_are_managed_at_their_depot_and_stay_inside_the_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var edi = await api.ClientForAsync(MasterDataApiFactory.SctEdi);            // mdm.party.view, not manage
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var line = await NewLineAsync(sct, ct);
        var prefix = NewCode("ZS", 6);
        try
        {
            var range = await Read<SealRow>(await sct.PostAsJsonAsync(Seals, Range(line, prefix, 1, 100), ct), HttpStatusCode.Created, ct);
            var url = $"{Seals}/{range.SealRangeId}";

            Assert.Contains((await edi.GetFromJsonAsync<List<SealRow>>($"{Seals}?partyCode={line}", ct))!, r => r.SealRangeId == range.SealRangeId);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.PostAsJsonAsync(Seals, Range(line, prefix, 200, 300), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.PutAsJsonAsync(url, Range(line, prefix, 1, 100, range.RowVersion), ct)).StatusCode);

            Assert.DoesNotContain((await other.GetFromJsonAsync<List<SealRow>>($"{Seals}?includeInactive=true", ct))!, r => r.SealRangeId == range.SealRangeId);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(url, Range(line, prefix, 1, 100, range.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(RowVersions.WithVersion(url, range.RowVersion), ct)).StatusCode);

            // The line cannot be deleted from under its range.
            var partyVersion = (await RowVersions.OfAsync(sct, $"/api/master/parties/{Uri.EscapeDataString(line)}", ct))!;
            Assert.Equal(HttpStatusCode.Conflict, (await sct.DeleteAsync(RowVersions.WithVersion($"/api/master/parties/{Uri.EscapeDataString(line)}", partyVersion), ct)).StatusCode);
        }
        finally { await PurgeRangesAsync(line, ct); await RemoveRangesAsync(sct, line, ct); }
    }

    /// <summary>A range seals were issued from cannot be deleted through the API (by design); tests take theirs out with the owner login — SCT, ZS prefixes only.</summary>
    private static async Task PurgeRangesAsync(string line, CancellationToken ct)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.AdminConnection);
        await connection.OpenAsync(ct);
        await using var purge = connection.CreateCommand();
        purge.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE r FROM party.seal_range r JOIN party.party p ON p.party_id = r.party_id
             WHERE p.party_code = @line AND r.tenant_id = @sct AND r.seal_prefix LIKE 'ZS%' AND r.last_issued_number IS NOT NULL;
            """;
        purge.Parameters.AddWithValue("@line", line);
        purge.Parameters.AddWithValue("@sct", TestDatabase.Sct);
        await purge.ExecuteNonQueryAsync(ct);
    }

    // ── countries ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Countries_are_the_global_iso_list_for_every_signed_in_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var ops = await api.ClientForAsync(MasterDataApiFactory.SctOpsLcb);       // no tenant-wide mdm.* at all
        var countries = (await ops.GetFromJsonAsync<List<CountryRow>>("/api/master/countries", ct))!;
        var th = Assert.Single(countries, c => c.CountryCode == "TH");
        Assert.Equal(("THA", "764", "THB"), (th.Iso3Code, th.NumericCode, th.DefaultCurrency));
        Assert.True(countries.Count > 200);
        Assert.Equal("SG", Assert.Single((await ops.GetFromJsonAsync<List<CountryRow>>("/api/master/countries?search=SGP", ct))!).CountryCode);
        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/master/countries", ct)).StatusCode);
    }

    // ── public holidays ─────────────────────────────────────────────────────

    /// <summary>A date in 2031 no fixture uses, unique per test run.</summary>
    private static DateOnly NewDate() => new DateOnly(2031, 1, 1).AddDays(Random.Shared.Next(0, 360));

    [Fact]
    public async Task A_holiday_lives_its_life_one_per_date_per_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var date = NewDate();
        var ids = new List<(Guid Id, string Version)>();
        try
        {
            var all = await Read<HolidayRow>(await sct.PostAsJsonAsync(Holidays, new { holidayDate = date, nameEn = "Test Day" }, ct), HttpStatusCode.Created, ct);
            ids.Add((all.PublicHolidayId, all.RowVersion));
            Assert.Null(all.BranchId);

            Assert.Equal(HttpStatusCode.Conflict, (await sct.PostAsJsonAsync(Holidays, new { holidayDate = date, nameEn = "Again" }, ct)).StatusCode);
            // The same date for one depot is its own row.
            var depot = await Read<HolidayRow>(await sct.PostAsJsonAsync(Holidays, new { holidayDate = date, nameEn = "Depot Day", branchId = SctLcb01, isHalfDay = true }, ct), HttpStatusCode.Created, ct);
            ids.Add((depot.PublicHolidayId, depot.RowVersion));

            var year = (await sct.GetFromJsonAsync<List<HolidayRow>>($"{Holidays}?year={date.Year}&branchId={SctLcb01}", ct))!;
            Assert.Equal(2, year.Count(h => h.HolidayDate == date));
            Assert.Single((await sct.GetFromJsonAsync<List<HolidayRow>>($"{Holidays}?year={date.Year}&branchId={SctLkr01}", ct))!, h => h.HolidayDate == date);

            var url = $"{Holidays}/{all.PublicHolidayId}";
            await ExpectFieldAsync(await sct.PutAsJsonAsync(url, new { holidayDate = date, nameEn = "x" }, ct), "rowVersion", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Holidays, new { nameEn = "No date" }, ct), "holidayDate", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Holidays, new { holidayDate = date.AddDays(1), nameEn = "" }, ct), "nameEn", ct);
            var renamed = await Read<HolidayRow>(await sct.PutAsJsonAsync(url, new { holidayDate = date, nameEn = "Renamed Day", rowVersion = all.RowVersion }, ct), HttpStatusCode.OK, ct);
            ids[0] = (renamed.PublicHolidayId, renamed.RowVersion);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync(url, new { holidayDate = date, nameEn = "Stale", rowVersion = all.RowVersion }, ct)).StatusCode);
        }
        finally
        {
            foreach (var (id, _) in ids)
                if ((await sct.GetFromJsonAsync<List<HolidayRow>>($"{Holidays}?year={date.Year}&branchId={SctLcb01}", ct))!.FirstOrDefault(h => h.PublicHolidayId == id) is { } h)
                    await sct.DeleteAsync(RowVersions.WithVersion($"{Holidays}/{id}", h.RowVersion), ct);
        }
    }

    /// <summary>Every depot = tenant-wide mdm.org.manage; one depot = that depot. LCB's ops manager adds LCB's day, not the tenant's or LKR's.</summary>
    [Fact]
    public async Task A_depot_manager_sets_its_own_depots_holidays_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var ops = await api.ClientForAsync(MasterDataApiFactory.SctOpsLcb);
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var date = NewDate();
        HolidayRow? own = null;
        try
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await ops.PostAsJsonAsync(Holidays, new { holidayDate = date, nameEn = "Everyone" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ops.PostAsJsonAsync(Holidays, new { holidayDate = date, nameEn = "LKR", branchId = SctLkr01 }, ct)).StatusCode);
            own = await Read<HolidayRow>(await ops.PostAsJsonAsync(Holidays, new { holidayDate = date, nameEn = "LCB day", branchId = SctLcb01 }, ct), HttpStatusCode.Created, ct);
            // Moving it to every depot needs the tenant-wide grant.
            Assert.Equal(HttpStatusCode.Forbidden, (await ops.PutAsJsonAsync($"{Holidays}/{own.PublicHolidayId}", new { holidayDate = date, nameEn = "LCB day", rowVersion = own.RowVersion }, ct)).StatusCode);

            Assert.DoesNotContain((await other.GetFromJsonAsync<List<HolidayRow>>($"{Holidays}?year={date.Year}&branchId={SctLcb01}", ct))!, h => h.PublicHolidayId == own.PublicHolidayId);
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(RowVersions.WithVersion($"{Holidays}/{own.PublicHolidayId}", own.RowVersion), ct)).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await ops.DeleteAsync(RowVersions.WithVersion($"{Holidays}/{own.PublicHolidayId}", own.RowVersion), ct)).StatusCode);
            own = null;
        }
        finally
        {
            if (own is not null) await sct.DeleteAsync(RowVersions.WithVersion($"{Holidays}/{own.PublicHolidayId}", own.RowVersion), ct);
        }
    }

    private sealed record LineRow(string PartyCode);
    private sealed record LineDetail(LineRow Line);
    private sealed record SealRow(Guid SealRangeId, Guid BranchId, string PartyCode, string SealPrefix, long SeriesStart, long SeriesEnd, long Remaining, bool IsActive, string RowVersion);
    private sealed record CountryRow(string CountryCode, string Iso3Code, string NumericCode, string NameEn, string? DefaultCurrency);
    private sealed record HolidayRow(Guid PublicHolidayId, Guid? BranchId, DateOnly HolidayDate, string NameEn, bool IsHalfDay, string RowVersion);
}
