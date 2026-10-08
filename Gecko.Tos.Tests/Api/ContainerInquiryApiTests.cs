using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Data;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// GET /api/tos/containers and /containers/{no} through the real host: one row per
/// box (its latest stay), the filters, and the pop-up read — scoped to the caller's
/// depots, invisible to another tenant.
///
/// Boxes are ZZTU numbers booked with a ZZI- carrier ref, gated through the real
/// barrier, and removed in finally.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class ContainerInquiryApiTests(TosApiFactory api)
{
    private const string Prefix = "ZZI-";
    private const string Containers = "/api/tos/containers";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewRef() => $"{Prefix}{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static string NewBox()
    {
        var ten = $"ZZTU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    private static async Task BookAsync(HttpClient client, string carrierRef, string box, CancellationToken ct, Guid? branchId = null)
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
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"booking returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }

    private static async Task GateAsync(HttpClient client, string box, string direction, DateTimeOffset at, CancellationToken ct, Guid? branchId = null)
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
    }

    private static async Task<PagedResult<ContainerInquiryRowResponse>> ListAsync(HttpClient client, string query, CancellationToken ct) =>
        (await client.GetFromJsonAsync<PagedResult<ContainerInquiryRowResponse>>($"{Containers}?{query}", ct))!;

    [Fact]
    public async Task One_row_per_box_filtered_by_where_it_is_and_what_it_is()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var (staying, gone) = (NewBox(), NewBox());
        try
        {
            await BookAsync(client, carrierRef, staying, ct);
            await GateAsync(client, staying, "IN", DateTimeOffset.UtcNow.AddDays(-3), ct);
            await BookAsync(client, carrierRef + "G", gone, ct);
            await GateAsync(client, gone, "IN", DateTimeOffset.UtcNow.AddDays(-5), ct);
            await GateAsync(client, gone, "OUT", DateTimeOffset.UtcNow.AddDays(-1), ct);

            // The default page is 50 rows.
            Assert.Equal(50, (await ListAsync(client, "", ct)).PageSize);

            // Part of the number finds the box; it is in the yard, on its booking, with its dwell.
            var row = Assert.Single((await ListAsync(client, $"search={staying[..8].ToLowerInvariant()}{staying[8..]}", ct)).Items);
            Assert.Equal((staying, "IN_YARD", "FULL", "MAEU", "20", 3), (row.ContainerNo, row.Status, row.FullEmpty, row.LineCode, row.SizeCode, row.DaysInYard));
            Assert.Equal((carrierRef, "CUS-TAE"), (row.CarrierRef, row.CustomerCode));
            Assert.Null(row.GateOutAt);

            // Gone out: one row, its latest stay, dwell to the gate-out.
            var left = Assert.Single((await ListAsync(client, $"search={gone}", ct)).Items);
            Assert.Equal(("OUT_OF_YARD", 4), (left.Status, left.DaysInYard));
            Assert.NotNull(left.GateOutEirNo);

            // The filters.
            Assert.Contains((await ListAsync(client, $"search={staying}&status=IN_YARD&sizeCode=20&fullEmpty=FULL&lineCode=MAEU&customerCode=CUS-TAE", ct)).Items, r => r.ContainerNo == staying);
            Assert.Empty((await ListAsync(client, $"search={staying}&status=OUT_OF_YARD", ct)).Items);
            Assert.Empty((await ListAsync(client, $"search={staying}&sizeCode=40", ct)).Items);
            Assert.Empty((await ListAsync(client, $"search={staying}&fullEmpty=EMPTY", ct)).Items);
            Assert.Empty((await ListAsync(client, $"search={gone}&status=IN_YARD", ct)).Items);
            Assert.Single((await ListAsync(client, $"booking={carrierRef}G", ct)).Items);

            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Containers}?status=SOMEWHERE", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Containers}?sort=COLOUR", ct)).StatusCode);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef + "G");
            await TestDatabase.RemoveBookingsAsync(carrierRef + "G");
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    [Fact]
    public async Task The_pop_up_reads_one_box_and_keeps_to_the_callers_depots()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var (atLcb, atBkk) = (NewBox(), NewBox());
        try
        {
            await BookAsync(owner, carrierRef, atLcb, ct);
            await GateAsync(owner, atLcb, "IN", DateTimeOffset.UtcNow.AddHours(-1), ct);
            await BookAsync(owner, carrierRef, atBkk, ct, TestDatabase.SctBkk01);
            await GateAsync(owner, atBkk, "IN", DateTimeOffset.UtcNow.AddHours(-1), ct, TestDatabase.SctBkk01);

            var one = (await owner.GetFromJsonAsync<ContainerInquiryResponse>($"{Containers}/{atLcb}", ct))!;
            Assert.Equal((atLcb, "IN_YARD"), (one.ContainerNo, one.Latest!.Status));
            Assert.True(one.Story.IsInYard);
            Assert.Single(one.Story.Visits);
            Assert.Empty(one.Holds);

            // The LCB01 clerk sees the LCB01 box, not the BKK01 one.
            var clerk = await api.ClientForAsync(TosApiFactory.SctGateLcb);
            Assert.Contains((await ListAsync(clerk, $"search={atLcb}", ct)).Items, r => r.ContainerNo == atLcb);
            Assert.Empty((await ListAsync(clerk, $"search={atBkk}", ct)).Items);
            Assert.Equal(HttpStatusCode.NotFound, (await clerk.GetAsync($"{Containers}/{atBkk}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync($"{Containers}?branchId={TestDatabase.SctBkk01}", ct)).StatusCode);

            // Another tenant does not learn the box exists.
            var other = await api.ClientForAsync(TosApiFactory.SssOwner);
            Assert.Empty((await ListAsync(other, $"search={atLcb}", ct)).Items);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Containers}/{atLcb}", ct)).StatusCode);

            // A malformed number (under 4 letters or digits) is a 400 on the field.
            var bad = await owner.GetAsync($"{Containers}/AB1", ct);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            using var body = JsonDocument.Parse(await bad.Content.ReadAsStringAsync(ct));
            Assert.Contains(body.RootElement.GetProperty("errors").EnumerateObject(),
                p => string.Equals(p.Name, "containerNo", StringComparison.OrdinalIgnoreCase));
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }
}
