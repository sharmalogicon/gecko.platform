using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// GET /api/tos/containers/{no}/story through the real host: a box's stays with
/// their journal and the bookings it has been on — scoped to the caller's depots,
/// invisible to another tenant.
///
/// Boxes are ZZTU numbers booked with a ZZS- carrier ref, gated through the real
/// barrier, and removed in finally.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class ContainerStoryApiTests(TosApiFactory api)
{
    private const string Prefix = "ZZS-";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewRef() => $"{Prefix}{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static string NewBox()
    {
        var ten = $"ZZTU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    private static string Story(string box, Guid? branchId = null) =>
        $"/api/tos/containers/{box}/story" + (branchId is { } b ? $"?branchId={b}" : "");

    private static async Task<Guid> BookAsync(HttpClient client, string carrierRef, string box, CancellationToken ct, Guid? branchId = null)
    {
        var response = await client.PostAsJsonAsync("/api/tos/bookings", new
        {
            branchId = branchId ?? SctLcb01,
            orderTypeCode = "IMP CY/CY",
            lineCode = "MAEU",
            customerCode = "CUS-TAE",
            carrierRef,
            validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
            containers = new object[] { new { containerNo = box } },
        }, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"booking returned {(int)response.StatusCode}: {text}");
        using var body = JsonDocument.Parse(text);
        return body.RootElement.GetProperty("booking").GetProperty("bookingId").GetGuid();
    }

    private static async Task<GateTransactionResponse> GateAsync(HttpClient client, string box, string direction,
        DateTimeOffset at, CancellationToken ct, Guid? branchId = null)
    {
        var response = await client.PostAsJsonAsync("/api/tos/gate/transactions", new
        {
            branchId = branchId ?? SctLcb01,
            containerNo = box,
            direction,
            tripType = direction == "IN" ? "DROP_OFF_CONT" : "PICK_UP_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 18000m, customsPermitNo = "ZZ-PERMIT-1",
            truck = new { plate = "70-4321", driverName = "Somchai P." },
            grossWeightKg = 24100m,
            weightSource = "WEIGHBRIDGE",
            seals = new object[] { new { sealNo = $"ZZ-{box[^4..]}", sealType = "LINE", isIntact = true } },
            transactionAt = at,
        }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"gate {direction} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<GateTransactionResponse>(ct))!;
    }

    [Fact]
    public async Task A_box_tells_its_stay_its_events_and_its_booking()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        try
        {
            var bookingId = await BookAsync(client, carrierRef, box, ct);

            // Booked but never gated: a booking, no stay.
            var before = (await client.GetFromJsonAsync<ContainerStoryResponse>(Story(box), ct))!;
            Assert.False(before.IsInYard);
            Assert.Empty(before.Visits);
            var booked = Assert.Single(before.Bookings);
            Assert.Equal((bookingId, true, "IMP CY/CY"), (booked.BookingId, booked.IsOpen, booked.OrderTypeCode));

            var gateIn = await GateAsync(client, box, "IN", DateTimeOffset.UtcNow.AddDays(-2), ct);

            // The number is read the way the clerk types it, lower case and spaced.
            var spaced = $"{box[..4].ToLowerInvariant()} {box[4..]}";
            var inYard = (await client.GetFromJsonAsync<ContainerStoryResponse>(Story(Uri.EscapeDataString(spaced)), ct))!;
            Assert.Equal(box, inYard.ContainerNo);
            Assert.True(inYard.IsInYard);
            var stay = inYard.Current!;
            Assert.Equal((gateIn.GateTransactionId, gateIn.EirNo, "FULL", "MAEU"),
                (stay.GateInTransactionId, stay.GateInEirNo, stay.FullEmpty, stay.LineCode));
            Assert.Equal(2, stay.DaysInYard);
            Assert.Equal("SCT-LCB01", stay.BranchCode);
            Assert.Null(stay.GateOutTransactionId);
            Assert.Equal(["GATE_IN"], stay.Events.Select(e => e.EventType));

            // Out it goes: the stay closes and keeps its dwell; the booking line ends.
            var gateOut = await GateAsync(client, box, "OUT", DateTimeOffset.UtcNow.AddMinutes(-1), ct);
            var after = (await client.GetFromJsonAsync<ContainerStoryResponse>(Story(box), ct))!;
            Assert.False(after.IsInYard);
            Assert.Null(after.Current);
            var closed = Assert.Single(after.Visits);
            Assert.Equal((gateOut.GateTransactionId, gateOut.EirNo, false), (closed.GateOutTransactionId, closed.GateOutEirNo, closed.IsInYard));
            Assert.Equal(2, closed.DaysInYard);
            Assert.Contains("GATE_OUT", closed.Events.Select(e => e.EventType));
            Assert.Equal(bookingId, Assert.Single(after.Bookings).BookingId);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    [Fact]
    public async Task A_malformed_number_is_a_400_on_the_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var response = await client.GetAsync(Story("NOT_A_BOX"), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, text);
        using var body = JsonDocument.Parse(text);
        Assert.Contains(body.RootElement.GetProperty("errors").EnumerateObject(),
            p => string.Equals(p.Name, "containerNo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_story_stays_inside_the_callers_depots_and_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var atLcb = NewBox();
        var atBkk = NewBox();
        try
        {
            await BookAsync(owner, carrierRef, atLcb, ct);
            await GateAsync(owner, atLcb, "IN", DateTimeOffset.UtcNow.AddHours(-1), ct);
            await BookAsync(owner, carrierRef, atBkk, ct, TestDatabase.SctBkk01);
            await GateAsync(owner, atBkk, "IN", DateTimeOffset.UtcNow.AddHours(-1), ct, TestDatabase.SctBkk01);

            // The LCB01 gate clerk reads the box at LCB01...
            var clerk = await api.ClientForAsync(TosApiFactory.SctGateLcb);
            var mine = (await clerk.GetFromJsonAsync<ContainerStoryResponse>(Story(atLcb), ct))!;
            Assert.True(mine.IsInYard);
            Assert.Single(mine.Visits);

            // ...sees nothing of the one at BKK01, and cannot ask about that depot.
            var theirs = (await clerk.GetFromJsonAsync<ContainerStoryResponse>(Story(atBkk), ct))!;
            Assert.False(theirs.IsInYard);
            Assert.Empty(theirs.Visits);
            Assert.Empty(theirs.Bookings);
            Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync(Story(atBkk, TestDatabase.SctBkk01), ct)).StatusCode);

            // The owner sees both depots, and can narrow to one.
            Assert.Single((await owner.GetFromJsonAsync<ContainerStoryResponse>(Story(atBkk), ct))!.Visits);
            Assert.Empty((await owner.GetFromJsonAsync<ContainerStoryResponse>(Story(atBkk, SctLcb01), ct))!.Visits);

            // Another tenant does not learn the box exists.
            var other = await api.ClientForAsync(TosApiFactory.SssOwner);
            var hidden = (await other.GetFromJsonAsync<ContainerStoryResponse>(Story(atLcb), ct))!;
            Assert.False(hidden.IsInYard);
            Assert.Empty(hidden.Visits);
            Assert.Empty(hidden.Bookings);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }
}
