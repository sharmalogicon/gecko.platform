using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Gate hours at the barrier (TIER3 §1, owner decision 2026-09-30): outside the
/// depot's opening hours, or on a public holiday, the barrier WARNS and names the
/// next opening — it never refuses. A depot with no gate hours says nothing.
/// Windows and holidays are set through MasterData's API at SCT LCB01 and
/// removed in finally. 1 March 2027 is a Monday.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class GateHoursBarrierTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private const string Windows = "/api/master/gate-hours/windows";
    private const string Stranger = "ZZZU1234564";   // well formed, on no booking
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly TimeSpan Bangkok = TimeSpan.FromHours(7);

    private static async Task<GatePreflightResponse> PreflightAsync(HttpClient client, string box, DateTimeOffset at, CancellationToken ct) =>
        (await client.GetFromJsonAsync<GatePreflightResponse>(
            $"{Gate}/preflight?branchId={SctLcb01}&containerNo={box}&direction=IN&at={Uri.EscapeDataString(at.ToString("O"))}", ct))!;

    private static async Task AddWindowAsync(HttpClient client, int weekday, string opens, string closes, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(Windows, new { branchId = SctLcb01, isoWeekday = weekday, opensAt = opens, closesAt = closes }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task ClearWindowsAsync(HttpClient client, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"{Windows}?branchId={SctLcb01}", ct));
        foreach (var w in doc.RootElement.EnumerateArray())
            await client.DeleteAsync($"{Windows}/{w.GetProperty("gateHoursWindowId").GetGuid()}?rowVersion={Uri.EscapeDataString(w.GetProperty("rowVersion").GetString()!)}", ct);
    }

    [Fact]
    public async Task Outside_hours_and_on_a_holiday_the_barrier_warns_and_inside_it_says_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        await ClearWindowsAsync(client, ct);
        Guid? holidayId = null;
        string? holidayVersion = null;
        try
        {
            // No gate hours at the depot: today's behaviour, not a word about hours.
            var before = await PreflightAsync(client, Stranger, new DateTimeOffset(2027, 3, 1, 3, 0, 0, Bangkok), ct);
            Assert.Equal("NO_ASSIGNMENT", Assert.Single(before.Findings).Code);

            for (var d = 1; d <= 5; d++) await AddWindowAsync(client, d, "08:00", "17:00", ct);

            var inside = await PreflightAsync(client, Stranger, new DateTimeOffset(2027, 3, 1, 10, 0, 0, Bangkok), ct);
            Assert.Equal("NO_ASSIGNMENT", Assert.Single(inside.Findings).Code);

            var evening = await PreflightAsync(client, Stranger, new DateTimeOffset(2027, 3, 1, 20, 0, 0, Bangkok), ct);
            var late = Assert.Single(evening.Findings, f => f.Code == "OUTSIDE_GATE_HOURS");
            Assert.Equal("WARN", late.Severity);
            Assert.Contains("Tue 2 Mar 08:00", late.Message);

            var holiday = await client.PostAsJsonAsync("/api/master/public-holidays",
                new { holidayDate = "2027-03-03", nameEn = "ZZ barrier holiday", branchId = SctLcb01 }, ct);
            Assert.Equal(HttpStatusCode.Created, holiday.StatusCode);
            using (var h = JsonDocument.Parse(await holiday.Content.ReadAsStringAsync(ct)))
            {
                holidayId = h.RootElement.GetProperty("publicHolidayId").GetGuid();
                holidayVersion = h.RootElement.GetProperty("rowVersion").GetString();
            }

            var onHoliday = await PreflightAsync(client, Stranger, new DateTimeOffset(2027, 3, 3, 10, 0, 0, Bangkok), ct);
            var closed = Assert.Single(onHoliday.Findings, f => f.Code == "GATE_CLOSED_DAY");
            Assert.Equal("WARN", closed.Severity);
            Assert.Contains("ZZ barrier holiday", closed.Message);
            Assert.Contains("Thu 4 Mar 08:00", closed.Message);
        }
        finally
        {
            await ClearWindowsAsync(client, ct);
            if (holidayId is { } id)
                await client.DeleteAsync($"/api/master/public-holidays/{id}?rowVersion={Uri.EscapeDataString(holidayVersion!)}", ct);
        }
    }

    /// <summary>The warning changes nothing: the decision stays ALLOWED and the gate-in is recorded.</summary>
    [Fact]
    public async Task A_box_gated_in_outside_hours_is_warned_about_and_still_goes_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = $"ZZH-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        const string box = "AKLU6018567";
        await ClearWindowsAsync(client, ct);
        try
        {
            // One short window on a weekday that is not today at the depot: now is outside hours.
            var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok")).DayOfWeek;
            var iso = today == DayOfWeek.Sunday ? 7 : (int)today;
            await AddWindowAsync(client, iso % 7 + 1, "08:00", "09:00", ct);

            var booked = await client.PostAsJsonAsync("/api/tos/bookings", new
            {
                branchId = SctLcb01, orderTypeCode = "IMP CY/CY", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
                validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
                containers = new[] { new { containerNo = box } },
            }, ct);
            Assert.True(booked.StatusCode == HttpStatusCode.Created, await booked.Content.ReadAsStringAsync(ct));
            Assert.NotNull(await booked.Content.ReadFromJsonAsync<BookingDetailResponse>(ct));

            var view = await PreflightAsync(client, box, DateTimeOffset.UtcNow, ct);
            Assert.Equal("ALLOWED", view.Decision);
            Assert.Equal("WARN", Assert.Single(view.Findings, f => f.Code == "OUTSIDE_GATE_HOURS").Severity);

            var gateIn = await client.PostAsJsonAsync($"{Gate}/transactions", new
            {
                branchId = SctLcb01, containerNo = box, direction = "IN",
                tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 18000m, customsPermitNo = "ZZ-PERMIT-1",
                truck = new { plate = "70-1234", driverName = "Somchai P.", driverLicence = "1234567890123" },
                grossWeightKg = 22150m, weightSource = "WEIGHBRIDGE",
                seals = new object[] { new { sealNo = "ZZ-HOURS-1", sealType = "LINE", isIntact = true } },
                positionText = "A-03-2",
            }, ct);
            Assert.True(gateIn.StatusCode == HttpStatusCode.Created, await gateIn.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            await ClearWindowsAsync(client, ct);
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }
}
