using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// GET /api/tos/yard/stock through the real host: the stock list counted. The
/// fixture depots already hold boxes, so each test counts before and after gating
/// its own ZZTU boxes and checks the difference.
///
/// Boxes are booked with a ZZK- carrier ref, gated through the real barrier, and
/// removed in finally.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class YardStockApiTests(TosApiFactory api)
{
    private const string Prefix = "ZZK-";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewRef() => $"{Prefix}{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static string NewBox()
    {
        var ten = $"ZZTU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    private static string Stock(Guid? branchId = null, string? fullEmpty = null)
    {
        var query = new List<string>();
        if (branchId is { } b) query.Add($"branchId={b}");
        if (fullEmpty is not null) query.Add($"fullEmpty={fullEmpty}");
        return "/api/tos/yard/stock" + (query.Count == 0 ? "" : "?" + string.Join('&', query));
    }

    private static async Task BookAsync(HttpClient client, string carrierRef, string box, string type, CancellationToken ct, Guid? branchId = null)
    {
        var response = await client.PostAsJsonAsync("/api/tos/bookings", new
        {
            branchId = branchId ?? SctLcb01,
            orderTypeCode = "IMP CY/CY",
            lineCode = "MAEU",
            customerCode = "CUS-TAE",
            carrierRef = $"{carrierRef}-{box[^4..]}",   // one live booking per carrier reference; cleanup matches the prefix
            validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            requirements = new object[] { new { equipmentTypeCode = type, qty = 1 } },
            containers = new object[] { new { containerNo = box } },
        }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"booking returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }

    private static async Task GateInAsync(HttpClient client, string box, DateTimeOffset at, CancellationToken ct, Guid? branchId = null)
    {
        var response = await client.PostAsJsonAsync("/api/tos/gate/transactions", new
        {
            branchId = branchId ?? SctLcb01,
            containerNo = box,
            direction = "IN",
            tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 18000m, customsPermitNo = "ZZ-PERMIT-1",
            truck = new { plate = "70-4321", driverName = "Somchai P." },
            grossWeightKg = 24100m,
            weightSource = "WEIGHBRIDGE",
            seals = new object[] { new { sealNo = $"ZZ-{box[^4..]}", sealType = "LINE", isIntact = true } },
            transactionAt = at,
        }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"gate IN returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }

    private static int BoxesOf(IEnumerable<YardStockGroupResponse> groups, string? code) =>
        groups.SingleOrDefault(g => g.Code == code)?.Tally.Boxes ?? 0;

    [Fact]
    public async Task Gated_boxes_are_counted_by_type_line_customer_dwell_and_pool()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var fresh = NewBox();
        var old = NewBox();
        try
        {
            var before = (await client.GetFromJsonAsync<YardStockResponse>(Stock(SctLcb01), ct))!;

            await BookAsync(client, carrierRef, fresh, "20GP", ct);
            await GateInAsync(client, fresh, DateTimeOffset.UtcNow.AddHours(-1), ct);
            await BookAsync(client, carrierRef, old, "40HC", ct);
            await GateInAsync(client, old, DateTimeOffset.UtcNow.AddDays(-20), ct);

            var after = (await client.GetFromJsonAsync<YardStockResponse>(Stock(SctLcb01), ct))!;
            Assert.Equal(SctLcb01, after.BranchId);

            // Two more boxes, three more TEU (a 20' and a 40'), both FULL (an import gate-in).
            Assert.Equal(before.Total.Boxes + 2, after.Total.Boxes);
            Assert.Equal(before.Total.Teu + 3m, after.Total.Teu);
            Assert.Equal(before.Total.Full + 2, after.Total.Full);
            Assert.Equal(before.Total.Empty, after.Total.Empty);
            Assert.Equal(before.Total.Days0To7 + 1, after.Total.Days0To7);
            Assert.Equal(before.Total.Days15To30 + 1, after.Total.Days15To30);

            Assert.Equal(BoxesOf(before.Types, "20GP") + 1, BoxesOf(after.Types, "20GP"));
            Assert.Equal(BoxesOf(before.Types, "40HC") + 1, BoxesOf(after.Types, "40HC"));
            Assert.Equal(BoxesOf(before.Lines, "MAEU") + 2, BoxesOf(after.Lines, "MAEU"));

            // The customer is the booking's, named from MDM.
            var customer = Assert.Single(after.Customers, c => c.Code == "CUS-TAE");
            Assert.Equal(BoxesOf(before.Customers, "CUS-TAE") + 2, customer.Tally.Boxes);
            Assert.False(string.IsNullOrWhiteSpace(customer.Name));

            // The yard tally and the pools add up to the total.
            Assert.Equal(after.Total.Boxes, after.Yards.Sum(y => y.Tally.Boxes));
            Assert.All(after.Yards, y => Assert.Equal(y.Tally.Boxes, y.Areas.Sum(a => a.Tally.Boxes)));
            Assert.Equal(after.Total.Boxes, after.Pools.Sum(p => p.Tally.Boxes));
            var pool = after.Pools.Where(p => p.LineCode == "MAEU" && p.EquipmentTypeCode == "40HC").ToList();
            Assert.NotEmpty(pool);
            Assert.All(pool, p => Assert.Equal("40", p.SizeCode?[..2]));

            // Narrowed to EMPTY, neither import box is there.
            var empties = (await client.GetFromJsonAsync<YardStockResponse>(Stock(SctLcb01, "EMPTY"), ct))!;
            Assert.Equal("EMPTY", empties.FullEmpty);
            Assert.Equal(before.Total.Empty, empties.Total.Boxes);
            Assert.Equal(0, empties.Total.Full);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    [Fact]
    public async Task An_unknown_load_is_a_400_on_the_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var response = await client.GetAsync(Stock(SctLcb01, "HALF"), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, text);
        using var body = JsonDocument.Parse(text);
        Assert.Contains(body.RootElement.GetProperty("errors").EnumerateObject(),
            p => string.Equals(p.Name, "fullEmpty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_count_stays_inside_the_callers_depots_and_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await api.ClientForAsync(TosApiFactory.SctOwner);
        var clerk = await api.ClientForAsync(TosApiFactory.SctGateLcb);
        var other = await api.ClientForAsync(TosApiFactory.SssOwner);
        var carrierRef = NewRef();
        var atBkk = NewBox();
        try
        {
            var clerkBefore = (await clerk.GetFromJsonAsync<YardStockResponse>(Stock(), ct))!;
            var otherBefore = (await other.GetFromJsonAsync<YardStockResponse>(Stock(), ct))!;
            var ownerBefore = (await owner.GetFromJsonAsync<YardStockResponse>(Stock(), ct))!;

            await BookAsync(owner, carrierRef, atBkk, "20GP", ct, TestDatabase.SctBkk01);
            await GateInAsync(owner, atBkk, DateTimeOffset.UtcNow.AddHours(-1), ct, TestDatabase.SctBkk01);

            // The owner's all-depot count moves; the LCB01 clerk's and the other tenant's do not.
            Assert.Equal(ownerBefore.Total.Boxes + 1, (await owner.GetFromJsonAsync<YardStockResponse>(Stock(), ct))!.Total.Boxes);
            Assert.Equal(clerkBefore.Total.Boxes, (await clerk.GetFromJsonAsync<YardStockResponse>(Stock(), ct))!.Total.Boxes);
            Assert.Equal(otherBefore.Total.Boxes, (await other.GetFromJsonAsync<YardStockResponse>(Stock(), ct))!.Total.Boxes);

            // And the clerk cannot ask for BKK01 at all.
            Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync(Stock(TestDatabase.SctBkk01), ct)).StatusCode);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }
}
