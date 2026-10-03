using System.Net;
using System.Net.Http.Json;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Vessels;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Vector's P/U Mode / D/O Mode per booked box (owner 2026-10-02: per container, the
/// checks carried over, new bookings only), through the real host on SCT fixture data.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class BookingHandoverModeApiTests(TosApiFactory api)
{
    private const string Bookings = "/api/tos/bookings";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewBox()
    {
        var ten = $"ZZHU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    [Fact]
    public async Task A_booked_box_carries_its_mode_and_the_mode_switches_on_Vectors_checks()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = $"ZZH-{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var callRef = $"ZZ-{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var voyage = $"T{Random.Shared.Next(100000, 999999)}";
        var etd = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(12), TimeSpan.Zero).AddHours(15);
        try
        {
            object Import(string suffix, string? handoverMode) => new
            {
                branchId = SctLcb01, orderTypeCode = "IMP CY/CY", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef = carrierRef + suffix,
                requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
                containers = new object[] { new { containerNo = NewBox(), handoverMode } },
            };

            // An import booking offers the D/O list only.
            var wrongList = await client.PostAsJsonAsync(Bookings, Import("A", "PU_OWN"), ct);
            Assert.Equal(HttpStatusCode.BadRequest, wrongList.StatusCode);
            var body = await wrongList.Content.ReadAsStringAsync(ct);
            Assert.Contains("containers[0].handoverMode", body);
            Assert.Contains("DO_CUS", body);

            // A box on its way in, dropped off by the depot's own truck: taken, and read back.
            var created = await client.PostAsJsonAsync(Bookings, Import("B", "do_own"), ct);
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
            var import = (await created.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!;
            Assert.Equal("DO_OWN", import.Containers.Single().HandoverMode);
            Assert.Equal("DO_OWN", (await client.GetFromJsonAsync<BookingDetailResponse>($"{Bookings}/{import.Booking.BookingId}", ct))!.Containers.Single().HandoverMode);

            // An export booking: the box must be an empty one in this yard — unless it is picked up elsewhere.
            var call = await client.PostAsJsonAsync("/api/tos/vessel-calls", new
            {
                callRef, vesselCode = "CHAOPHRAYA", portCode = "THLCH", operatorVoyageOut = voyage, eta = etd.AddHours(-30), etd,
                lines = new object[] { new { lineCode = "MAEU", voyageOut = voyage + "M" } },
            }, ct);
            Assert.True(call.StatusCode == HttpStatusCode.Created, await call.Content.ReadAsStringAsync(ct));
            var callId = (await call.Content.ReadFromJsonAsync<VesselCallDetailResponse>(ct))!.Call.VesselCallId;
            object Export(string suffix, string? handoverMode) => new
            {
                branchId = SctLcb01, orderTypeCode = "EXP CY/CY", lineCode = "MAEU", customerCode = "CUS-BKF", carrierRef = carrierRef + suffix,
                vesselCallId = callId, polPortCode = "THLCH", podPortCode = "SGSIN",
                requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
                containers = new object[] { new { containerNo = NewBox(), handoverMode } },
            };

            var notHere = await client.PostAsJsonAsync(Bookings, Export("C", "PU_OWN"), ct);
            Assert.Equal(HttpStatusCode.BadRequest, notHere.StatusCode);
            Assert.Contains("not in this yard", await notHere.Content.ReadAsStringAsync(ct));

            var elsewhere = await client.PostAsJsonAsync(Bookings, Export("D", "PU_OTHER"), ct);
            Assert.True(elsewhere.StatusCode == HttpStatusCode.Created, await elsewhere.Content.ReadAsStringAsync(ct));
            Assert.Equal("PU_OTHER", (await elsewhere.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!.Containers.Single().HandoverMode);

            // No mode said: assigned exactly as before the field existed.
            var silent = await client.PostAsJsonAsync(Bookings, Export("E", null), ct);
            Assert.True(silent.StatusCode == HttpStatusCode.Created, await silent.Content.ReadAsStringAsync(ct));
            Assert.Null((await silent.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!.Containers.Single().HandoverMode);
        }
        finally
        {
            await TestDatabase.RemoveBookingsAsync(carrierRef);
            await TestDatabase.RemoveCallAsync(callRef);
        }
    }
}
