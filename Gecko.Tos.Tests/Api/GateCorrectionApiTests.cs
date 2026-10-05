using System.Net;
using System.Net.Http.Json;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// After Save, the clerk corrects the seals, remarks, customs permit and clip-on of a live EIR (owner D1,
/// GATE_IN_COMPLETION_PLAN A9; Vector "Update Details", GateIn.cs:2371, 3659). Every correction keeps what the
/// fields were and became, why, who and when; anything else is void and re-record.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class GateCorrectionApiTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewRef() => $"ZZC-{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static string NewBox()
    {
        var ten = $"ZZCU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    private static async Task<GateTransactionResponse> GateInAsync(HttpClient client, string carrierRef, string box, CancellationToken ct)
    {
        var booked = await client.PostAsJsonAsync("/api/tos/bookings", new
        {
            branchId = SctLcb01, orderTypeCode = "GATE TEST", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
            validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
            containers = new object[] { new { containerNo = box } },
        }, ct);
        Assert.True(booked.StatusCode == HttpStatusCode.Created, await booked.Content.ReadAsStringAsync(ct));
        _ = await booked.Content.ReadFromJsonAsync<BookingDetailResponse>(ct);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await client.GetFromJsonAsync<GatePreflightResponse>($"{Gate}/preflight?branchId={SctLcb01}&containerNo={box}&direction=IN", ct))!.Decision != "ALLOWED")
        {
            Assert.True(DateTime.UtcNow < deadline, $"{box} never allowed in");
            await Task.Delay(250, ct);
        }
        var response = await client.PostAsJsonAsync($"{Gate}/transactions", new
        {
            branchId = SctLcb01, containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT",
            truck = new { plate = "70-3659" }, remarks = "As keyed",
            grossWeightKg = 22000m, tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 19800m,
            seals = new object[] { new { sealNo = "OLD-1", sealType = "LINE", isIntact = true } },
        }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<GateTransactionResponse>(ct))!;
    }

    [Fact]
    public async Task A_clerk_corrects_the_seals_and_papers_of_a_live_EIR_and_the_trail_keeps_both()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await api.ClientForAsync(TosApiFactory.SctOwner);
        var clerk = await api.ClientForAsync(TosApiFactory.SctGateLcb);
        var carrierRef = NewRef();
        var box = NewBox();
        try
        {
            var eir = await GateInAsync(owner, carrierRef, box, ct);
            var url = $"{Gate}/transactions/{eir.GateTransactionId}/corrections";

            // A reason is required, and something must change.
            Assert.Equal(HttpStatusCode.BadRequest, (await clerk.PostAsJsonAsync(url, new { remarks = "x" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await clerk.PostAsJsonAsync(url, new { reason = "Nothing really" }, ct)).StatusCode);

            // Another tenant does not see the EIR at all.
            var other = await api.ClientForAsync(TosApiFactory.SssOwner);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(url, new { reason = "Not mine", remarks = "x" }, ct)).StatusCode);

            var corrected = await clerk.PostAsJsonAsync(url, new
            {
                reason = "Seal misread at the lane",
                seals = new object[] { new { sealNo = "NEW-1", sealType = "LINE" }, new { sealNo = "CUS-9", sealType = "CUSTOMS" } },
                customsPermitNo = "A0123456789",
                clipOnNo = "CLIP-7",
            }, ct);
            Assert.True(corrected.StatusCode == HttpStatusCode.Created, $"{(int)corrected.StatusCode}: {await corrected.Content.ReadAsStringAsync(ct)}");
            var trail = (await corrected.Content.ReadFromJsonAsync<GateCorrectionResponse>(ct))!;
            Assert.Equal(new[] { "OLD-1" }, trail.Before.Seals.Select(s => s.SealNo));
            Assert.Equal(new[] { "CUS-9", "NEW-1" }, trail.After.Seals.Select(s => s.SealNo).Order());
            Assert.Equal((null, "A0123456789"), (trail.Before.CustomsPermitNo, trail.After.CustomsPermitNo));
            Assert.Equal(("As keyed", "As keyed"), (trail.Before.Remarks, trail.After.Remarks));   // not sent = unchanged

            var now = (await owner.GetFromJsonAsync<GateTransactionResponse>($"{Gate}/transactions/{eir.GateTransactionId}", ct))!;
            Assert.Equal(new[] { "CUS-9", "NEW-1" }, now.Seals.Select(s => s.SealNo).Order());
            Assert.Equal(("A0123456789", "CLIP-7", "COMPLETED"), (now.CustomsPermitNo, now.ClipOnNo, now.Status));

            // A second correction clears the remarks; the trail lists both, oldest first.
            Assert.Equal(HttpStatusCode.Created, (await clerk.PostAsJsonAsync(url, new { reason = "Remarks were for another box", remarks = "" }, ct)).StatusCode);
            var list = (await owner.GetFromJsonAsync<List<GateCorrectionResponse>>(url, ct))!;
            Assert.Equal(2, list.Count);
            Assert.Equal(("As keyed", null), (list[1].Before.Remarks, list[1].After.Remarks));
            Assert.Equal("Seal misread at the lane", list[0].Reason);

            // A voided EIR is not corrected: it is re-recorded.
            var voided = await owner.PostAsJsonAsync($"{Gate}/transactions/{eir.GateTransactionId}/void", new { reason = "Wrong box" }, ct);
            Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await clerk.PostAsJsonAsync(url, new { reason = "Too late", clipOnNo = "X" }, ct)).StatusCode);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }
}
