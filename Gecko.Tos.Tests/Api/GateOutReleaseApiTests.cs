using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Gate;
using Gecko.Tos.Endpoints.Vessels;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Vector's gate-out release gates (gate-in-vector-parity Part B §B2.2, §B2.4)
/// through the real host, on SCT fixture data: a full export box asked to leave
/// on EXP CY-IN (NOMINATING) — one step, FULL_OUT — against a vessel call with a
/// laden release date and a registry box designated to certain ports.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class GateOutReleaseApiTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private const string Calls = "/api/tos/vessel-calls";
    private const string Containers = "/api/master/containers";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewBox()
    {
        var ten = $"ZZRU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    [Fact]
    public async Task A_full_export_box_waits_for_the_laden_release_date_and_goes_only_to_its_fixed_ports()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var callRef = $"ZZ-{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var carrierRef = $"ZZR-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        var voyage = $"T{Random.Shared.Next(100000, 999999)}";
        var box = NewBox();
        var etd = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(12), TimeSpan.Zero).AddHours(15);
        var registered = false;
        try
        {
            // The ship releases laden boxes from three days before it sails; the box is designated to Laem Chabang only.
            var call = await client.PostAsJsonAsync(Calls, new
            {
                callRef, vesselCode = "CHAOPHRAYA", portCode = "THLCH", operatorVoyageOut = voyage,
                eta = etd.AddHours(-30), etd, ladenReleaseAt = etd.AddDays(-3),
                lines = new object[] { new { lineCode = "MAEU", voyageOut = voyage + "M" } },
            }, ct);
            Assert.True(call.StatusCode == HttpStatusCode.Created, await call.Content.ReadAsStringAsync(ct));
            var callId = (await call.Content.ReadFromJsonAsync<VesselCallDetailResponse>(ct))!.Call.VesselCallId;

            var container = await client.PostAsJsonAsync(Containers, new { containerNo = box, ownershipType = "LINE_OWNED", fixedPortCodes = new[] { "THLCH" } }, ct);
            Assert.True(container.StatusCode == HttpStatusCode.Created, await container.Content.ReadAsStringAsync(ct));
            registered = true;

            var booking = await client.PostAsJsonAsync("/api/tos/bookings", new
            {
                branchId = SctLcb01, orderTypeCode = "EXP CY-IN (NOMINATING)", lineCode = "MAEU", customerCode = "CUS-BKF", carrierRef,
                vesselCallId = callId, polPortCode = "THLCH", podPortCode = "SGSIN",
                requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
                containers = new object[] { new { containerNo = box } },
            }, ct);
            Assert.True(booking.StatusCode == HttpStatusCode.Created, await booking.Content.ReadAsStringAsync(ct));
            var orderNo = (await booking.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!.Booking.OrderNo;

            async Task<GatePreflightResponse> AskAsync(DateTimeOffset? at = null) =>
                (await client.GetFromJsonAsync<GatePreflightResponse>(
                    $"{Gate}/preflight?branchId={SctLcb01}&containerNo={box}&direction=OUT" +
                    (at is { } when ? $"&at={Uri.EscapeDataString(when.ToString("O"))}" : ""), ct))!;

            // Today, nine days before the release date, and bound for Singapore: refused twice over.
            var now = await AskAsync();
            Assert.Equal("BLOCKED", now.Decision);
            var early = Assert.Single(now.Findings, f => f.Code == "BEFORE_LADEN_RELEASE");
            Assert.Equal("BLOCK", early.Severity);
            Assert.Contains(callRef, early.Message);
            var port = Assert.Single(now.Findings, f => f.Code == "FIXED_PORT");
            Assert.Contains("THLCH", port.Message);
            Assert.Contains("SGSIN", port.Message);
            Assert.Contains(orderNo, port.Message);

            // From the release date on, that gate is open; the port one is not.
            var later = await AskAsync(etd.AddDays(-2));
            Assert.DoesNotContain(later.Findings, f => f.Code == "BEFORE_LADEN_RELEASE");
            Assert.Contains(later.Findings, f => f.Code == "FIXED_PORT");

            // Singapore added to the box's ports: nothing left to say about the port.
            var row = (await client.GetFromJsonAsync<JsonElement>($"{Containers}/{box}", ct)).GetProperty("rowVersion").GetString();
            var widened = await client.PutAsJsonAsync($"{Containers}/{box}",
                new { rowVersion = row, ownershipType = "LINE_OWNED", status = "IN_SERVICE", fixedPortCodes = new[] { "THLCH", "SGSIN" } }, ct);
            Assert.True(widened.StatusCode == HttpStatusCode.OK, await widened.Content.ReadAsStringAsync(ct));
            Assert.DoesNotContain((await AskAsync(etd.AddDays(-2))).Findings, f => f.Code is "FIXED_PORT" or "BEFORE_LADEN_RELEASE");
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
            await TestDatabase.RemoveCallAsync(callRef);
            if (registered)
            {
                var row = (await client.GetFromJsonAsync<JsonElement>($"{Containers}/{box}", ct)).GetProperty("rowVersion").GetString();
                await client.DeleteAsync($"{Containers}/{box}?rowVersion={Uri.EscapeDataString(row!)}", ct);
            }
        }
    }
}
