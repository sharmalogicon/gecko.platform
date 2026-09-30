using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Gecko.MasterData.Tests.Api.MasterLifecycle;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// Gate hours (TIER3 §1): weekly windows and one-off dates per depot, and the
/// open / shut answer the barrier reads. Everything is created at SCT's two
/// depots and soft-deleted again; the dates are in 2027, clear of the 2026
/// fixture holidays. 1 March 2027 is a Monday.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class GateHoursApiTests(MasterDataApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly Guid SctLkr01 = Guid.Parse("785AE785-33A5-F111-9B0D-00919E4766D5");
    private const string Windows = "/api/master/gate-hours/windows";
    private const string Exceptions = "/api/master/gate-hours/exceptions";

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expected, $"expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web)!;
    }

    private static Task<Status> StatusAsync(HttpClient client, Guid branch, string localAt, CancellationToken ct) =>
        client.GetFromJsonAsync<Status>($"/api/master/gate-hours/status?branchId={branch}&at={Uri.EscapeDataString(localAt + "+07:00")}", ct)!;

    /// <summary>Soft-deletes every window and 2027 date the tests may have left at a depot.</summary>
    private static async Task ClearAsync(HttpClient client, Guid branch, CancellationToken ct)
    {
        foreach (var w in (await client.GetFromJsonAsync<List<Window>>($"{Windows}?branchId={branch}", ct))!)
            await client.DeleteAsync(RowVersions.WithVersion($"{Windows}/{w.GateHoursWindowId}", w.RowVersion), ct);
        foreach (var e in (await client.GetFromJsonAsync<List<DateRow>>($"{Exceptions}?branchId={branch}&year=2027", ct))!)
            await client.DeleteAsync(RowVersions.WithVersion($"{Exceptions}/{e.GateHoursExceptionId}", e.RowVersion), ct);
    }

    [Fact]
    public async Task Weekly_windows_live_their_life_and_overlaps_are_refused_across_midnight()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        await ClearAsync(sct, SctLcb01, ct);
        try
        {
            Assert.Equal("NOT_CONFIGURED", (await StatusAsync(sct, SctLcb01, "2027-03-01T10:00:00", ct)).State);

            var morning = await Read<Window>(await sct.PostAsJsonAsync(Windows, new { branchId = SctLcb01, isoWeekday = 1, opensAt = "08:00", closesAt = "12:00" }, ct), HttpStatusCode.Created, ct);
            Assert.Equal(("08:00", "12:00", false, 240), (morning.OpensAt, morning.ClosesAt, morning.IsOvernight, morning.Minutes));
            var night = await Read<Window>(await sct.PostAsJsonAsync(Windows, new { branchId = SctLcb01, isoWeekday = 1, opensAt = "22:00", closesAt = "06:00" }, ct), HttpStatusCode.Created, ct);
            Assert.Equal((true, 480), (night.IsOvernight, night.Minutes));
            // A lunch break is two windows: 13:00 touches nothing.
            await Read<Window>(await sct.PostAsJsonAsync(Windows, new { branchId = SctLcb01, isoWeekday = 1, opensAt = "13:00", closesAt = "17:00" }, ct), HttpStatusCode.Created, ct);

            // Field-named 400s: overlap on the day, overlap with Monday night's tail, equal ends, bad time, bad weekday, unknown depot.
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Windows, new { branchId = SctLcb01, isoWeekday = 1, opensAt = "11:00", closesAt = "14:00" }, ct), "opensAt", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Windows, new { branchId = SctLcb01, isoWeekday = 2, opensAt = "05:00", closesAt = "09:00" }, ct), "opensAt", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Windows, new { branchId = SctLcb01, isoWeekday = 3, opensAt = "08:00", closesAt = "08:00" }, ct), "closesAt", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Windows, new { branchId = SctLcb01, isoWeekday = 3, opensAt = "8am", closesAt = "17:00" }, ct), "opensAt", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Windows, new { branchId = SctLcb01, isoWeekday = 8, opensAt = "08:00", closesAt = "17:00" }, ct), "isoWeekday", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Windows, new { branchId = Guid.NewGuid(), isoWeekday = 3, opensAt = "08:00", closesAt = "17:00" }, ct), "branchId", ct);

            var list = (await sct.GetFromJsonAsync<List<Window>>($"{Windows}?branchId={SctLcb01}", ct))!;
            Assert.Equal(["08:00", "13:00", "22:00"], list.Select(w => w.OpensAt));

            // The status the barrier reads: open in a window, in Monday night's tail on Tuesday, shut between.
            var open = await StatusAsync(sct, SctLcb01, "2027-03-01T09:00:00", ct);
            Assert.Equal(("OPEN", true), (open.State, open.IsOpen));
            Assert.Equal(new DateTimeOffset(2027, 3, 1, 12, 0, 0, TimeSpan.FromHours(7)), open.OpenUntil);
            Assert.True((await StatusAsync(sct, SctLcb01, "2027-03-02T05:30:00", ct)).IsOpen);
            var lunch = await StatusAsync(sct, SctLcb01, "2027-03-01T12:30:00", ct);
            Assert.Equal(("OUTSIDE_HOURS", false), (lunch.State, lunch.IsOpen));
            Assert.Equal(new DateTimeOffset(2027, 3, 1, 13, 0, 0, TimeSpan.FromHours(7)), lunch.NextOpensAt);
            var tuesday = await StatusAsync(sct, SctLcb01, "2027-03-02T07:00:00", ct);
            Assert.Equal(new DateTimeOffset(2027, 3, 8, 8, 0, 0, TimeSpan.FromHours(7)), tuesday.NextOpensAt);

            // Optimistic concurrency.
            var moved = await Read<Window>(await sct.PutAsJsonAsync($"{Windows}/{morning.GateHoursWindowId}", new { branchId = SctLcb01, isoWeekday = 1, opensAt = "07:30", closesAt = "12:00", rowVersion = morning.RowVersion }, ct), HttpStatusCode.OK, ct);
            Assert.Equal("07:30", moved.OpensAt);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync($"{Windows}/{morning.GateHoursWindowId}", new { branchId = SctLcb01, isoWeekday = 1, opensAt = "07:00", closesAt = "12:00", rowVersion = morning.RowVersion }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await sct.PutAsJsonAsync($"{Windows}/{morning.GateHoursWindowId}", new { branchId = SctLcb01, isoWeekday = 1, opensAt = "07:00", closesAt = "12:00" }, ct)).StatusCode);
            await ExpectFieldAsync(await sct.PutAsJsonAsync($"{Windows}/{morning.GateHoursWindowId}", new { branchId = SctLkr01, isoWeekday = 1, opensAt = "07:30", closesAt = "12:00", rowVersion = moved.RowVersion }, ct), "branchId", ct);
            // Moving onto itself is not an overlap.
            await Read<Window>(await sct.PutAsJsonAsync($"{Windows}/{morning.GateHoursWindowId}", new { branchId = SctLcb01, isoWeekday = 1, opensAt = "07:30", closesAt = "12:30", rowVersion = moved.RowVersion }, ct), HttpStatusCode.OK, ct);

            Assert.Equal(HttpStatusCode.BadRequest, (await sct.DeleteAsync($"{Windows}/{night.GateHoursWindowId}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.DeleteAsync(RowVersions.WithVersion($"{Windows}/{morning.GateHoursWindowId}", morning.RowVersion), ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await sct.DeleteAsync(RowVersions.WithVersion($"{Windows}/{night.GateHoursWindowId}", night.RowVersion), ct)).StatusCode);
            Assert.DoesNotContain((await sct.GetFromJsonAsync<List<Window>>($"{Windows}?branchId={SctLcb01}", ct))!, w => w.GateHoursWindowId == night.GateHoursWindowId);
        }
        finally { await ClearAsync(sct, SctLcb01, ct); }
    }

    [Fact]
    public async Task One_off_dates_and_holidays_shut_the_gate_and_a_half_day_keeps_the_morning()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        await ClearAsync(sct, SctLcb01, ct);
        var holidays = new List<(Guid Id, string RowVersion)>();
        try
        {
            for (byte d = 1; d <= 5; d++)
                await Read<Window>(await sct.PostAsJsonAsync(Windows, new { branchId = SctLcb01, isoWeekday = d, opensAt = "08:00", closesAt = "17:00" }, ct), HttpStatusCode.Created, ct);

            // Wednesday 3 March: a depot holiday. Thursday 4 March: a half day for every depot.
            foreach (var (branch, date, half) in new[] { ((Guid?)SctLcb01, "2027-03-03", false), (null, "2027-03-04", true) })
            {
                var h = await Read<Holiday>(await sct.PostAsJsonAsync("/api/master/public-holidays", new { holidayDate = date, nameEn = "ZZ gate-hours test", isHalfDay = half, branchId = branch }, ct), HttpStatusCode.Created, ct);
                holidays.Add((h.PublicHolidayId, h.RowVersion));
            }

            var holiday = await StatusAsync(sct, SctLcb01, "2027-03-03T10:00:00", ct);
            Assert.Equal(("HOLIDAY", "ZZ gate-hours test"), (holiday.State, holiday.Note));
            Assert.Equal(new DateTimeOffset(2027, 3, 4, 8, 0, 0, TimeSpan.FromHours(7)), holiday.NextOpensAt);
            Assert.True((await StatusAsync(sct, SctLcb01, "2027-03-04T11:00:00", ct)).IsOpen);
            Assert.Equal("HOLIDAY", (await StatusAsync(sct, SctLcb01, "2027-03-04T13:00:00", ct)).State);
            // The other depot has no gate hours: nothing to say.
            Assert.Equal("NOT_CONFIGURED", (await StatusAsync(sct, SctLkr01, "2027-03-03T10:00:00", ct)).State);

            // A one-off date outranks the holiday and the weekday.
            var special = await Read<DateRow>(await sct.PostAsJsonAsync(Exceptions, new { branchId = SctLcb01, exceptionDate = "2027-03-03", isClosed = false, opensAt = "09:00", closesAt = "11:00", reason = "Holiday shift" }, ct), HttpStatusCode.Created, ct);
            Assert.True((await StatusAsync(sct, SctLcb01, "2027-03-03T10:00:00", ct)).IsOpen);
            var stocktake = await Read<DateRow>(await sct.PostAsJsonAsync(Exceptions, new { branchId = SctLcb01, exceptionDate = "2027-03-05", isClosed = true, reason = "Stocktake" }, ct), HttpStatusCode.Created, ct);
            var shut = await StatusAsync(sct, SctLcb01, "2027-03-05T10:00:00", ct);
            Assert.Equal(("CLOSED_DATE", "Stocktake"), (shut.State, shut.Note));
            Assert.Equal(new DateTimeOffset(2027, 3, 8, 8, 0, 0, TimeSpan.FromHours(7)), shut.NextOpensAt);

            Assert.Equal(HttpStatusCode.Conflict, (await sct.PostAsJsonAsync(Exceptions, new { branchId = SctLcb01, exceptionDate = "2027-03-05", isClosed = true, reason = "Again" }, ct)).StatusCode);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Exceptions, new { branchId = SctLcb01, exceptionDate = "2027-03-09", isClosed = false, closesAt = "11:00", reason = "x" }, ct), "opensAt", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Exceptions, new { branchId = SctLcb01, exceptionDate = "2027-03-09", isClosed = true, opensAt = "09:00", reason = "x" }, ct), "opensAt", ct);
            await ExpectFieldAsync(await sct.PostAsJsonAsync(Exceptions, new { branchId = SctLcb01, exceptionDate = "2027-03-09", isClosed = true }, ct), "reason", ct);

            var edited = await Read<DateRow>(await sct.PutAsJsonAsync($"{Exceptions}/{special.GateHoursExceptionId}", new { branchId = SctLcb01, exceptionDate = "2027-03-03", isClosed = false, opensAt = "20:00", closesAt = "02:00", reason = "Night shift", rowVersion = special.RowVersion }, ct), HttpStatusCode.OK, ct);
            Assert.Equal(("20:00", "02:00"), (edited.OpensAt, edited.ClosesAt));
            Assert.True((await StatusAsync(sct, SctLcb01, "2027-03-04T01:00:00", ct)).IsOpen);
            Assert.Equal(HttpStatusCode.Conflict, (await sct.PutAsJsonAsync($"{Exceptions}/{special.GateHoursExceptionId}", new { branchId = SctLcb01, exceptionDate = "2027-03-03", isClosed = true, reason = "x", rowVersion = special.RowVersion }, ct)).StatusCode);

            var year = (await sct.GetFromJsonAsync<List<DateRow>>($"{Exceptions}?branchId={SctLcb01}&year=2027", ct))!;
            Assert.Equal(["2027-03-03", "2027-03-05"], year.Select(e => e.ExceptionDate));
            Assert.Equal(HttpStatusCode.NoContent, (await sct.DeleteAsync(RowVersions.WithVersion($"{Exceptions}/{stocktake.GateHoursExceptionId}", stocktake.RowVersion), ct)).StatusCode);
        }
        finally
        {
            await ClearAsync(sct, SctLcb01, ct);
            foreach (var (id, rv) in holidays)
                await sct.DeleteAsync(RowVersions.WithVersion($"/api/master/public-holidays/{id}", rv), ct);
        }
    }

    [Fact]
    public async Task Gate_hours_are_set_by_a_manager_of_the_depot_and_stay_inside_the_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(MasterDataApiFactory.SctAdmin);
        var ops = await api.ClientForAsync(MasterDataApiFactory.SctOpsLcb);
        var other = await api.ClientForAsync(MasterDataApiFactory.SiamCommercialAdmin);
        await ClearAsync(sct, SctLcb01, ct);
        await ClearAsync(sct, SctLkr01, ct);
        try
        {
            var mine = await Read<Window>(await ops.PostAsJsonAsync(Windows, new { branchId = SctLcb01, isoWeekday = 6, opensAt = "08:00", closesAt = "12:00" }, ct), HttpStatusCode.Created, ct);
            Assert.Equal(HttpStatusCode.Forbidden, (await ops.PostAsJsonAsync(Windows, new { branchId = SctLkr01, isoWeekday = 6, opensAt = "08:00", closesAt = "12:00" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ops.PostAsJsonAsync(Exceptions, new { branchId = SctLkr01, exceptionDate = "2027-03-06", isClosed = true, reason = "x" }, ct)).StatusCode);
            var theirs = await Read<Window>(await sct.PostAsJsonAsync(Windows, new { branchId = SctLkr01, isoWeekday = 6, opensAt = "08:00", closesAt = "12:00" }, ct), HttpStatusCode.Created, ct);
            Assert.Equal(HttpStatusCode.Forbidden, (await ops.PutAsJsonAsync($"{Windows}/{theirs.GateHoursWindowId}", new { branchId = SctLkr01, isoWeekday = 6, opensAt = "09:00", closesAt = "12:00", rowVersion = theirs.RowVersion }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ops.DeleteAsync(RowVersions.WithVersion($"{Windows}/{theirs.GateHoursWindowId}", theirs.RowVersion), ct)).StatusCode);

            // Another tenant: SCT's depot is not theirs to read, and SCT's rows do not exist for them.
            var read = await other.GetAsync($"{Windows}?branchId={SctLcb01}", ct);
            Assert.True(read.StatusCode == HttpStatusCode.Forbidden
                || (read.StatusCode == HttpStatusCode.OK && (await read.Content.ReadFromJsonAsync<List<Window>>(ct))!.Count == 0));
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{Windows}/{mine.GateHoursWindowId}", new { branchId = SctLcb01, isoWeekday = 6, opensAt = "09:00", closesAt = "12:00", rowVersion = mine.RowVersion }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(RowVersions.WithVersion($"{Windows}/{mine.GateHoursWindowId}", mine.RowVersion), ct)).StatusCode);
            Assert.NotEqual(HttpStatusCode.Created, (await other.PostAsJsonAsync(Windows, new { branchId = SctLcb01, isoWeekday = 6, opensAt = "13:00", closesAt = "15:00" }, ct)).StatusCode);
        }
        finally
        {
            await ClearAsync(sct, SctLcb01, ct);
            await ClearAsync(sct, SctLkr01, ct);
        }
    }

    private sealed record Window(Guid GateHoursWindowId, Guid BranchId, byte IsoWeekday, string OpensAt, string ClosesAt, bool IsOvernight, int Minutes, string RowVersion);
    private sealed record DateRow(Guid GateHoursExceptionId, string ExceptionDate, bool IsClosed, string? OpensAt, string? ClosesAt, string Reason, string RowVersion);
    private sealed record Holiday(Guid PublicHolidayId, string RowVersion);
    private sealed record Status(string State, bool IsOpen, bool IsConfigured, string? Note, DateTimeOffset? OpenUntil, DateTimeOffset? NextOpensAt);
}
