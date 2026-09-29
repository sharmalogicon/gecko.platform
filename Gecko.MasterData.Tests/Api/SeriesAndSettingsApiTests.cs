using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// Number series and tenant settings as the System parameters screen edits them.
/// A series that has issued numbers cannot change how it counts (that would
/// repeat numbers) nor be deleted; a setting's value at each scope is versioned
/// like every tenant row.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class SeriesAndSettingsApiTests(MasterDataApiFactory api)
{
    private const string Series = "/api/master/number-series";
    private const string Settings = "/api/master/settings";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewKey() => $"ZZ_{Guid.NewGuid():N}"[..14].ToUpperInvariant();

    private static object SeriesBody(string key, string? rowVersion = null, string reset = "YEARLY", string datePart = "YY",
        string? prefix = "ZZ", byte length = 6, long start = 1, bool isActive = true) => new
    {
        seriesKey = key, datePartFormat = datePart, resetPeriod = reset, numberLength = length, prefix, separator = "-",
        startNumber = start, description = "Test series", isActive, rowVersion,
    };

    private static async Task<SeriesRow> ReadSeries(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expected, $"expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<SeriesRow>(body, JsonSerializerOptions.Web)!;
    }

    private static async Task ExpectFieldAsync(HttpResponseMessage response, string field, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400 on {field}, got {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"expected an error on '{field}': {body}");
    }

    private static async Task<SeriesRow?> FindSeriesAsync(HttpClient client, string key, CancellationToken ct) =>
        (await client.GetFromJsonAsync<List<SeriesRow>>($"{Series}?includeInactive=true", ct))!.SingleOrDefault(s => s.SeriesKey == key);

    /// <summary>
    /// Deletes a test series. One that has issued numbers the API rightly refuses
    /// to delete, so its series and counter rows are removed with the owner-level
    /// test login instead — SCT fixture tenant and ZZ_ test keys only.
    /// </summary>
    private static async Task RemoveSeriesAsync(HttpClient client, string key, CancellationToken ct)
    {
        if (await FindSeriesAsync(client, key, ct) is not { } row) return;
        var deleted = await client.DeleteAsync(RowVersions.WithVersion($"{Series}/{row.NumberSeriesId}", row.RowVersion), ct);
        if (deleted.StatusCode != HttpStatusCode.Conflict) return;

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.AdminConnection);
        await connection.OpenAsync(ct);
        await using var purge = connection.CreateCommand();
        purge.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE c FROM config.number_series_counter c
              JOIN config.number_series s ON s.number_series_id = c.number_series_id
             WHERE s.number_series_id = @id AND s.tenant_id = @sct AND s.series_key LIKE 'ZZ[_]%';
            DELETE FROM config.number_series WHERE number_series_id = @id AND tenant_id = @sct AND series_key LIKE 'ZZ[_]%';
            """;
        purge.Parameters.AddWithValue("@id", row.NumberSeriesId);
        purge.Parameters.AddWithValue("@sct", TestDatabase.Sct);
        await purge.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task A_series_is_edited_with_its_row_version_until_it_issues_numbers()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var key = NewKey();
        try
        {
            var created = await ReadSeries(await sct.PostAsJsonAsync(Series, SeriesBody(key), ct), HttpStatusCode.OK, ct);
            var url = $"{Series}/{created.NumberSeriesId}";
            Assert.False((await FindSeriesAsync(sct, key, ct))!.HasIssuedNumbers);

            await ExpectFieldAsync(await sct.PutAsJsonAsync(url, SeriesBody(key), ct), "rowVersion", ct);
            await ExpectFieldAsync(await sct.PutAsJsonAsync(url, SeriesBody(NewKey(), created.RowVersion), ct), "seriesKey", ct);
            await ExpectFieldAsync(await sct.PutAsJsonAsync(url, SeriesBody(key, created.RowVersion, reset: "MONTHLY", datePart: "YY"), ct), "datePartFormat", ct);

            // Not yet used: even how it counts may change.
            var edited = await ReadSeries(await sct.PutAsJsonAsync(url, SeriesBody(key, created.RowVersion, reset: "MONTHLY", datePart: "YYMM", prefix: "zq", start: 100), ct), HttpStatusCode.OK, ct);
            Assert.Equal(("MONTHLY", "YYMM", "ZQ", 100L), (edited.ResetPeriod, edited.DatePartFormat, edited.Prefix, edited.StartNumber));
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync(url, SeriesBody(key, created.RowVersion, prefix: "ZX"), ct)).StatusCode);

            // Issue one number. From here the series counts as it did.
            var next = await sct.PostAsJsonAsync($"{Series}/next", new { seriesKey = key }, ct);
            Assert.True(next.StatusCode == HttpStatusCode.OK, $"next returned {(int)next.StatusCode}: {await next.Content.ReadAsStringAsync(ct)}");
            var used = (await FindSeriesAsync(sct, key, ct))!;
            Assert.True(used.HasIssuedNumbers);

            await ExpectFieldAsync(await sct.PutAsJsonAsync(url, SeriesBody(key, used.RowVersion, reset: "YEARLY", datePart: "YY", prefix: "ZQ", start: 100), ct), "resetPeriod", ct);
            await ExpectFieldAsync(await sct.PutAsJsonAsync(url, SeriesBody(key, used.RowVersion, reset: "MONTHLY", datePart: "YYYYMM", prefix: "ZQ", start: 100), ct), "datePartFormat", ct);
            await ExpectFieldAsync(await sct.PutAsJsonAsync(url, SeriesBody(key, used.RowVersion, reset: "MONTHLY", datePart: "YYMM", prefix: "ZQ", start: 5), ct), "startNumber", ct);
            // The shape of future numbers still may.
            var reshaped = await ReadSeries(await sct.PutAsJsonAsync(url, SeriesBody(key, used.RowVersion, reset: "MONTHLY", datePart: "YYMM", prefix: "ZR", length: 8, start: 100), ct), HttpStatusCode.OK, ct);
            Assert.Equal(("ZR", (byte)8), (reshaped.Prefix, reshaped.NumberLength));

            // And it cannot be deleted — only deactivated.
            Assert.Equal(HttpStatusCode.Conflict, (await sct.DeleteAsync(RowVersions.WithVersion(url, reshaped.RowVersion), ct)).StatusCode);
        }
        finally { await RemoveSeriesAsync(sct, key, ct); }
    }

    [Fact]
    public async Task An_unused_series_is_deleted_and_series_stay_inside_the_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var accounts = await api.ClientForAsync(MasterDataApiFactory.SctAccounts);   // no mdm.config.manage
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var key = NewKey();
        try
        {
            var created = await ReadSeries(await sct.PostAsJsonAsync(Series, SeriesBody(key), ct), HttpStatusCode.OK, ct);
            var url = $"{Series}/{created.NumberSeriesId}";

            Assert.Equal(HttpStatusCode.Forbidden, (await accounts.PutAsJsonAsync(url, SeriesBody(key, created.RowVersion), ct)).StatusCode);
            Assert.Null(await FindSeriesAsync(other, key, ct));
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(url, SeriesBody(key, created.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(RowVersions.WithVersion(url, created.RowVersion), ct)).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await sct.DeleteAsync(RowVersions.WithVersion(url, created.RowVersion), ct)).StatusCode);
            Assert.Null(await FindSeriesAsync(sct, key, ct));
        }
        finally { await RemoveSeriesAsync(sct, key, ct); }
    }

    /// <summary>
    /// depot.free_storage_days is BRANCH-scoped: a tenant value and a branch value,
    /// each its own versioned row. Clearing one falls back to the next layer.
    /// </summary>
    [Fact]
    public async Task A_setting_is_written_per_scope_with_that_scopes_row_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        const string key = "depot.free_storage_days";
        var before = await SettingAsync(sct, key, SctLcb01, ct);
        try
        {
            // Start from no branch value at LCB01, whatever the fixtures hold.
            if (before.BranchRowVersion is { } fixtureVersion)
                await Read(await sct.PutAsJsonAsync(Settings, new { settingKey = key, settingValue = (string?)null, branchId = SctLcb01, rowVersion = fixtureVersion }, ct), HttpStatusCode.OK, ct);

            var set = await Read(await sct.PutAsJsonAsync(Settings, new { settingKey = key, settingValue = "11", branchId = SctLcb01 }, ct), HttpStatusCode.OK, ct);
            Assert.Equal(("11", "11", "BRANCH"), (set.Value, set.BranchValue, set.ResolvedFrom));
            Assert.NotNull(set.BranchRowVersion);
            Assert.Equal(before.TenantValue, set.TenantValue);          // the tenant layer is reported too

            Assert.Equal(HttpStatusCode.BadRequest, (await sct.PutAsJsonAsync(Settings, new { settingKey = key, settingValue = "12", branchId = SctLcb01 }, ct)).StatusCode);
            var changed = await Read(await sct.PutAsJsonAsync(Settings, new { settingKey = key, settingValue = "12", branchId = SctLcb01, rowVersion = set.BranchRowVersion }, ct), HttpStatusCode.OK, ct);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync(Settings, new { settingKey = key, settingValue = "13", branchId = SctLcb01, rowVersion = set.BranchRowVersion }, ct)).StatusCode);

            var cleared = await Read(await sct.PutAsJsonAsync(Settings, new { settingKey = key, settingValue = (string?)null, branchId = SctLcb01, rowVersion = changed.BranchRowVersion }, ct), HttpStatusCode.OK, ct);
            Assert.Null(cleared.BranchValue);
            Assert.NotEqual("BRANCH", cleared.ResolvedFrom);
            // A version for a row that is gone lost a race.
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync(Settings, new { settingKey = key, settingValue = "14", branchId = SctLcb01, rowVersion = changed.BranchRowVersion }, ct)).StatusCode);
        }
        finally
        {
            var now = await SettingAsync(sct, key, SctLcb01, ct);
            if (now.BranchValue != before.BranchValue)
                await sct.PutAsJsonAsync(Settings, new { settingKey = key, settingValue = before.BranchValue, branchId = SctLcb01, rowVersion = now.BranchRowVersion }, ct);
        }
    }

    [Fact]
    public async Task Settings_need_config_manage_and_stay_inside_the_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var accounts = await api.ClientForAsync(MasterDataApiFactory.SctAccounts);
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);

        Assert.Equal(HttpStatusCode.Forbidden, (await accounts.PutAsJsonAsync(Settings, new { settingKey = "depot.free_storage_days", settingValue = "3", branchId = SctLcb01 }, ct)).StatusCode);
        // SIAM-COMMERCIAL resolves its own layers; SCT's branch value is not among them.
        var theirs = await SettingAsync(other, "depot.free_storage_days", SctLcb01, ct);
        Assert.Null(theirs.BranchValue);
        Assert.Null(theirs.BranchRowVersion);
        var ours = await SettingAsync(sct, "depot.free_storage_days", null, ct);
        Assert.Equal(ours.DefaultValue, theirs.DefaultValue);
    }

    private static async Task<Setting> SettingAsync(HttpClient client, string key, Guid? branchId, CancellationToken ct) =>
        (await client.GetFromJsonAsync<List<Setting>>(branchId is null ? Settings : $"{Settings}?branchId={branchId}", ct))!.Single(s => s.SettingKey == key);

    private static async Task<Setting> Read(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expected, $"expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<Setting>(body, JsonSerializerOptions.Web)!;
    }

    private sealed record SeriesRow(
        Guid NumberSeriesId, string SeriesKey, string? Prefix, string DatePartFormat, string ResetPeriod,
        byte NumberLength, long StartNumber, bool IsActive, string RowVersion, bool HasIssuedNumbers);
    private sealed record Setting(
        string SettingKey, string? Value, string? TenantValue, string? BranchValue, string? DefaultValue, string ResolvedFrom,
        string? TenantRowVersion, string? BranchRowVersion);
}
