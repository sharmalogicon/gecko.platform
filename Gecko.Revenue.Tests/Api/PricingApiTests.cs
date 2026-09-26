using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Revenue.Contracts;
using Gecko.Revenue.Endpoints.Tariffs;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// The resolver against the dev_01 fixture — every expected number here can be
/// worked out by hand from dev_01_revenue_tariffs.sql, and that is the point.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class PricingApiTests(RevenueApiFactory api)
{
    private const string Price = "/api/revenue/price";
    private const string Tariffs = "/api/revenue/tariffs";

    // 17 Sep 2026, 10:00 in Laem Chabang.
    private static readonly DateTimeOffset Sep17 = new(2026, 9, 17, 3, 0, 0, TimeSpan.Zero);

    private sealed record BranchRow(Guid BranchId, string BranchCode);
    private sealed record BranchPage(List<BranchRow> Items);

    private async Task<(HttpClient Client, Guid Lcb)> SctAsync(CancellationToken ct)
    {
        var owner = await api.ClientForAsync(RevenueApiFactory.SctOwner);
        var branches = await owner.GetFromJsonAsync<BranchPage>("/api/branches?pageSize=50", ct);
        return (await api.ClientForAsync(RevenueApiFactory.SctAccounts), branches!.Items.Single(b => b.BranchCode == "SCT-LCB01").BranchId);
    }

    private static async Task<PriceResult> PriceAsync(HttpClient client, PriceRequest request, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(Price, request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"price returned {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<PriceResult>(body, JsonSerializerOptions.Web)!;
    }

    /// <summary>The user's rule, end to end: 12 days, 3 free (public), 1–7 @160, 8+ @275.</summary>
    [Fact]
    public async Task A_walk_in_import_box_stored_twelve_days_costs_1670()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sct, lcb) = await SctAsync(ct);

        var result = await PriceAsync(sct, new PriceRequest("TOS", lcb, Sep17, "STORAGE", "CUSTOMER", "CASH",
            OrderTypeCode: "LIN", EquipmentSize: "20", CargoCategoryCode: "GENERAL",
            Quantity: 12, FreeTimeKind: "STORAGE", FullEmpty: "FULL", Direction: "IMPORT"), ct);

        Assert.Equal(PriceOutcomes.Priced, result.Outcome);
        Assert.Equal(("PUB-LCB", (byte?)9), (result.ScheduleNo, result.ScopeRank));
        Assert.Equal((3m, 9m), (result.FreeUnits!.Value, result.ChargeableQuantity!.Value));
        Assert.Equal([(7m, 160m), (2m, 275m)], result.Tiers.Select(t => (t.Quantity, t.Rate)));
        Assert.Equal(1670m, result.Amount);
    }

    /// <summary>A DG box gets the public DG free time (1 day) and the DG tiers.</summary>
    [Fact]
    public async Task Dangerous_goods_get_their_own_free_time_and_tiers()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sct, lcb) = await SctAsync(ct);

        var result = await PriceAsync(sct, new PriceRequest("TOS", lcb, Sep17, "STORAGE", "CUSTOMER", "CASH",
            OrderTypeCode: "LIN", EquipmentSize: "20", CargoCategoryCode: "DANGEROUS", IsDangerousGoods: true,
            Quantity: 9, FreeTimeKind: "STORAGE", FullEmpty: "FULL", Direction: "IMPORT"), ct);

        // 9 days − 1 free = 8 → 7×480 + 1×825
        Assert.Equal((1m, 8m, 4185m), (result.FreeUnits!.Value, result.ChargeableQuantity!.Value, result.Amount!.Value));
    }

    /// <summary>
    /// The collision the design exists for: agent+customer (rank 3) and
    /// forwarder+customer (rank 4) both price this lift; rank 3 wins, and the
    /// trail says so. The contract's own free time (5 days) overrides the public 3.
    /// </summary>
    [Fact]
    public async Task The_more_specific_contract_wins_and_brings_its_own_free_time()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sct, lcb) = await SctAsync(ct);
        PriceRequest Shipment(string charge, decimal qty, string? kind = null) => new("TOS", lcb, Sep17, charge, "CUSTOMER", "CREDIT",
            AgentPartyCode: "AGT-SEA", ForwarderPartyCode: "FWD-GCB", CustomerPartyCode: "CUS-ESL",
            OrderTypeCode: "LIN", EquipmentSize: charge == "LIFTIN" ? "40" : "20", CargoCategoryCode: "GENERAL",
            Quantity: qty, FreeTimeKind: kind, FullEmpty: "FULL", Direction: "IMPORT");

        var lift = await PriceAsync(sct, Shipment("LIFTIN", 1), ct);
        Assert.Equal(("CTR-SEA-ESL", 560m), (lift.ScheduleNo, lift.Amount!.Value));
        Assert.Contains(lift.PrecedenceTrail, t => t.StartsWith("CTR-SEA-ESL") && t.EndsWith("CHOSEN"));
        Assert.Contains(lift.PrecedenceTrail, t => t.StartsWith("CTR-GCB-ESL") && t.Contains("590") && t.EndsWith("outranked"));
        Assert.Contains(lift.PrecedenceTrail, t => t.StartsWith("PUB-LCB") && t.Contains("no LIFTIN rate"));

        // 12 days − 5 free (contract) = 7 → 7×120
        var storage = await PriceAsync(sct, Shipment("STORAGE", 12, "STORAGE"), ct);
        Assert.Equal(("CTR-SEA-ESL", 5m, "CTR-SEA-ESL", 840m),
            (storage.ScheduleNo, storage.FreeUnits!.Value, storage.FreeTimeFromScheduleNo, storage.Amount!.Value));
    }

    [Fact]
    public async Task Surcharges_apply_in_sequence_only_when_their_condition_holds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sct, lcb) = await SctAsync(ct);
        PriceRequest Lift(string cargo, decimal? weight) => new("TOS", lcb, Sep17, "LIFTIN", "CUSTOMER", "CASH",
            OrderTypeCode: "LIN", EquipmentSize: "40", CargoCategoryCode: cargo, GrossWeightKg: weight);

        var light = await PriceAsync(sct, Lift("GENERAL", 18000), ct);
        Assert.Equal((650m, 0), (light.Amount!.Value, light.Conditions.Count));

        var heavy = await PriceAsync(sct, Lift("GENERAL", 32000), ct);
        Assert.Equal(950m, heavy.Amount);
        Assert.Equal("Overweight (> 30 t) handling", Assert.Single(heavy.Conditions).Label);

        // USED_ENGINE falls to the any-cargo 40ft row (specificity 36 < 38 is not
        // in play — the GENERAL row does not match), and its engine surcharge fires.
        var engine = await PriceAsync(sct, Lift("USED_ENGINE", null), ct);
        Assert.Equal((36, 900m), (engine.Specificity!.Value, engine.Amount!.Value));
    }

    /// <summary>Reefer power by the hour (Vector bills HOUR slabs): no free time, 72 h @45 then @38.</summary>
    [Fact]
    public async Task Reefer_power_is_tiered_by_the_hour_and_the_type_supplies_the_size()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sct, lcb) = await SctAsync(ct);

        var result = await PriceAsync(sct, new PriceRequest("TOS", lcb, Sep17, "PLUGIN", "CUSTOMER", "CASH",
            EquipmentTypeCode: "40rh", Quantity: 100), ct);

        Assert.Equal((4304m, 100m), (result.Amount!.Value, result.ChargeableQuantity!.Value));   // 72×45 + 28×38

        var contradiction = await sct.PostAsJsonAsync(Price, new PriceRequest("TOS", lcb, Sep17, "PLUGIN", "CUSTOMER", "CASH",
            EquipmentTypeCode: "40RH", EquipmentSize: "20"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, contradiction.StatusCode);
    }

    [Fact]
    public async Task No_price_is_an_answer_not_a_guess()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sct, lcb) = await SctAsync(ct);

        var result = await PriceAsync(sct, new PriceRequest("TOS", lcb, Sep17, "LIFTIN", "HAULIER", "CASH", EquipmentSize: "20"), ct);

        Assert.Equal(PriceOutcomes.Unpriced, result.Outcome);
        Assert.Null(result.Amount);
        Assert.All(result.PrecedenceTrail, t => Assert.Contains("no LIFTIN rate", t));
    }

    [Fact]
    public async Task A_spot_price_needs_its_booking_and_a_draft_revision_is_never_used()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sct, lcb) = await SctAsync(ct);
        PriceRequest Lift(string? booking) => new("TOS", lcb, Sep17, "LIFTOUT", "CUSTOMER", "CASH",
            CustomerPartyCode: "CUS-TAE", BookingRef: booking, OrderTypeCode: "LOUT", EquipmentSize: "40", CargoCategoryCode: "GENERAL");

        Assert.Equal(("SPOT-0917", 500m), ((await PriceAsync(sct, Lift("bkg-sct-26-0917"), ct)) is var spot ? (spot.ScheduleNo, spot.Amount!.Value) : default));
        Assert.Equal(("PUB-LCB", 650m), ((await PriceAsync(sct, Lift(null), ct)) is var walkIn ? (walkIn.ScheduleNo, walkIn.Amount!.Value) : default));

        // CTR-MAEU v2 (+8% from 1 Oct) is only a DRAFT: November still prices from v1.
        var nov = await PriceAsync(sct, new PriceRequest("TOS", lcb, new DateTimeOffset(2026, 11, 2, 3, 0, 0, TimeSpan.Zero),
            "LIFTIN", "LINE", "CREDIT", AgentPartyCode: "MAEU", OrderTypeCode: "ERTN", EquipmentSize: "20"), ct);
        Assert.Equal((1, 300m), ((int)nov.VersionNo!.Value, nov.Amount!.Value));
    }

    [Fact]
    public async Task Each_tenant_is_priced_from_its_own_tariffs()
    {
        var ct = TestContext.Current.CancellationToken;
        var sss = await api.ClientForAsync(RevenueApiFactory.SssOwner);
        var lcb = (await sss.GetFromJsonAsync<BranchPage>("/api/branches?pageSize=50", ct))!.Items.Single(b => b.BranchCode == "SSS-LCB01").BranchId;

        var result = await PriceAsync(sss, new PriceRequest("TOS", lcb, Sep17, "LIFTIN", "CUSTOMER", "CASH",
            OrderTypeCode: "LIN", EquipmentTypeCode: "40HC", CargoCategoryCode: "GENERAL"), ct);

        Assert.Equal(("PUB-SSS", 680m), (result.ScheduleNo, result.Amount!.Value));
    }

    /// <summary>
    /// Revise → approve → the new version prices from its start date, measured on
    /// the BRANCH's calendar: 23:30 on 30 June in Laem Chabang is still v1,
    /// 00:30 on 1 July is v2 — although both are 30 June in UTC.
    /// </summary>
    [Fact]
    public async Task A_revision_takes_over_from_its_first_local_day()
    {
        var ct = TestContext.Current.CancellationToken;
        var (maker, lcb) = await SctAsync(ct);
        var checker = await api.ClientForAsync(RevenueApiFactory.SctOwner);
        var no = $"ZZ-REV-{Guid.NewGuid():N}"[..20].ToUpperInvariant();

        async Task<ScheduleResponse> Decide(HttpClient c, ScheduleResponse s, string action)
        {
            var r = await c.PostAsJsonAsync($"{Tariffs}/{s.ScheduleId}/{action}", new { rowVersion = s.RowVersion }, ct);
            Assert.True(r.IsSuccessStatusCode, $"{action}: {await r.Content.ReadAsStringAsync(ct)}");
            return (await r.Content.ReadFromJsonAsync<ScheduleResponse>(ct))!;
        }
        async Task<ScheduleResponse> Reload(Guid id) => (await maker.GetFromJsonAsync<ScheduleResponse>($"{Tariffs}/{id}", ct))!;
        async Task PutRate(ScheduleResponse s, decimal rate) => Assert.True((await maker.PutAsJsonAsync($"{Tariffs}/{s.ScheduleId}/rates", new
        {
            rowVersion = s.RowVersion,
            rates = new[] { new { chargeCode = "GATEFEE", billTo = "LINE", paymentTermCode = "CREDIT", rate } },
        }, ct)).IsSuccessStatusCode);

        try
        {
            var create = await maker.PostAsJsonAsync(Tariffs, new
            {
                scheduleNo = no, name = "Revision test", moduleCode = "TOS", scheduleType = "CONTRACT", branchId = lcb,
                agentPartyCode = "AGT-SEA", forwarderPartyCode = "FWD-GCB", customerPartyCode = "CUS-ESL",
                effectiveFrom = "2040-01-01",
            }, ct);
            var v1 = (await create.Content.ReadFromJsonAsync<ScheduleResponse>(ct))!;
            await PutRate(v1, 100);
            v1 = await Decide(checker, await Decide(maker, await Reload(v1.ScheduleId), "submit"), "approve");

            // A revision must start later than the version it replaces.
            Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync($"{Tariffs}/{v1.ScheduleId}/revise",
                new { rowVersion = v1.RowVersion, effectiveFrom = "2040-01-01" }, ct)).StatusCode);

            var revise = await maker.PostAsJsonAsync($"{Tariffs}/{v1.ScheduleId}/revise",
                new { rowVersion = v1.RowVersion, effectiveFrom = "2040-07-01" }, ct);
            Assert.Equal(HttpStatusCode.Created, revise.StatusCode);
            var v2 = (await revise.Content.ReadFromJsonAsync<ScheduleResponse>(ct))!;
            Assert.Equal(((short)2, "DRAFT", v1.LineageId, 1), (v2.VersionNo, v2.Status, v2.LineageId, v2.RateCount));   // prices copied

            // Only one open version at a time.
            Assert.Equal(HttpStatusCode.Conflict, (await maker.PostAsJsonAsync($"{Tariffs}/{v1.ScheduleId}/revise",
                new { rowVersion = v1.RowVersion, effectiveFrom = "2040-08-01" }, ct)).StatusCode);

            await PutRate(v2, 108);
            await Decide(checker, await Decide(maker, await Reload(v2.ScheduleId), "submit"), "approve");

            PriceRequest At(DateTimeOffset when) => new("TOS", lcb, when, "GATEFEE", "LINE", "CREDIT",
                AgentPartyCode: "AGT-SEA", ForwarderPartyCode: "FWD-GCB", CustomerPartyCode: "CUS-ESL");
            var beforeMidnight = await PriceAsync(maker, At(new DateTimeOffset(2040, 6, 30, 16, 30, 0, TimeSpan.Zero)), ct);
            var afterMidnight = await PriceAsync(maker, At(new DateTimeOffset(2040, 6, 30, 17, 30, 0, TimeSpan.Zero)), ct);

            Assert.Equal((new DateOnly(2040, 6, 30), (short)1, 100m), (beforeMidnight.PricedForDate, beforeMidnight.VersionNo!.Value, beforeMidnight.Amount!.Value));
            Assert.Equal((new DateOnly(2040, 7, 1), (short)2, 108m), (afterMidnight.PricedForDate, afterMidnight.VersionNo!.Value, afterMidnight.Amount!.Value));
            Assert.Equal(new DateOnly(2040, 6, 30), (await Reload(v1.ScheduleId)).EffectiveUntil);
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    [Fact]
    public async Task A_condition_that_can_never_fire_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (maker, _) = await SctAsync(ct);
        var no = $"ZZ-DEAD-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        try
        {
            var create = await maker.PostAsJsonAsync(Tariffs, new
            {
                scheduleNo = no, name = "Dead condition", moduleCode = "TOS", scheduleType = "SPOT",
                bookingRef = $"BKG-{no}", effectiveFrom = "2031-01-01",
            }, ct);
            var draft = (await create.Content.ReadFromJsonAsync<ScheduleResponse>(ct))!;

            var response = await maker.PutAsJsonAsync($"{Tariffs}/{draft.ScheduleId}/rates", new
            {
                rowVersion = draft.RowVersion,
                rates = new[]
                {
                    new
                    {
                        chargeCode = "LIFTIN", billTo = "CUSTOMER", paymentTermCode = "CASH", cargoCategoryCode = "GENERAL", rate = 1,
                        conditions = new[] { new { axis = "CARGO_CATEGORY", op = "IN", values = new[] { "USED_ENGINE" }, modifierOp = "ADD", modifierValue = 250 } },
                    },
                },
            }, ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("can never change the price", await response.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }
}
