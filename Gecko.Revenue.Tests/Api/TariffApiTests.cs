using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Data;
using Gecko.Revenue.Endpoints.Tariffs;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// Tariffs through the real host. The assertions target the rules the database
/// cannot hold (PLAN.md "Carried into 2.6"): approved is frozen, maker ≠
/// checker, one agreement per scope per day, parties play the role they are
/// named for, and a rate table is validated as a whole.
///
/// Every test that writes uses its own schedule number and removes it in
/// finally — approved rows through the test-only sysadmin door.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class TariffApiTests(RevenueApiFactory api)
{
    private const string Tariffs = "/api/revenue/tariffs";

    private static string NewNo(string prefix) => $"ZZ-{prefix}-{Guid.NewGuid():N}"[..20].ToUpperInvariant();

    private static object Spot(string scheduleNo, string booking, string from = "2026-01-01", string? to = null) => new
    {
        scheduleNo, name = "Test spot price", moduleCode = "TOS", scheduleType = "SPOT",
        bookingRef = booking, customerPartyCode = "CUS-TAE", effectiveFrom = from, effectiveTo = to,
    };

    private static readonly object[] GoodRates =
    [
        new { chargeCode = "LIFTIN", billTo = "CUSTOMER", paymentTermCode = "CASH", orderTypeCode = "LIN", equipmentSize = "40", cargoCategoryCode = "GENERAL", rate = 555 },
        new { chargeCode = "LIFTIN", billTo = "CUSTOMER", paymentTermCode = "CASH", equipmentTypeCode = "40RH", rate = 850 },
        new
        {
            chargeCode = "STORAGE", billTo = "CUSTOMER", paymentTermCode = "CASH", orderTypeCode = "LIN", equipmentSize = "20",
            pricingMethod = "TIERED_INCREMENTAL", tierBasis = "DAY",
            tiers = new object[] { new { fromQty = 1, toQty = 7, rate = 160 }, new { fromQty = 8, toQty = (int?)null, rate = 275 } },
            conditions = new object[]
            {
                new { axis = "WEIGHT_KG", op = "GT", number = 30000, modifierOp = "ADD", modifierValue = 50 },
                new { axis = "CARGO_CATEGORY", op = "IN", values = new[] { "USED_ENGINE", "car_dg" }, modifierOp = "MULTIPLY", modifierValue = 1.25 },
            },
        },
    ];

    private async Task<ScheduleResponse> CreateAsync(HttpClient client, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(Tariffs, body, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"create returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<ScheduleResponse>(ct))!;
    }

    private async Task<string> PutRatesAsync(HttpClient client, ScheduleResponse s, object[] rates, CancellationToken ct, HttpStatusCode expect = HttpStatusCode.OK)
    {
        var response = await client.PutAsJsonAsync($"{Tariffs}/{s.ScheduleId}/rates", new { rowVersion = s.RowVersion, rates }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expect, $"PUT rates returned {(int)response.StatusCode}: {body}");
        return body;
    }

    private static async Task<ScheduleResponse> DecideAsync(HttpClient client, ScheduleResponse s, string action, CancellationToken ct,
        HttpStatusCode expect = HttpStatusCode.OK, string? reason = null)
    {
        var response = await client.PostAsJsonAsync($"{Tariffs}/{s.ScheduleId}/{action}", new { rowVersion = s.RowVersion, reason }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expect, $"{action} returned {(int)response.StatusCode}: {body}");
        return expect == HttpStatusCode.OK ? JsonSerializer.Deserialize<ScheduleResponse>(body, JsonSerializerOptions.Web)! : s;
    }

    private async Task<ScheduleResponse> ReloadAsync(HttpClient client, Guid id, CancellationToken ct) =>
        (await client.GetFromJsonAsync<ScheduleResponse>($"{Tariffs}/{id}", ct))!;

    // ── the fixture, as the API sees it ─────────────────────────────────────

    [Fact]
    public async Task The_fixture_tariffs_read_back_with_their_lifecycle()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(RevenueApiFactory.SctAccounts);

        var page = (await sct.GetFromJsonAsync<PagedResult<ScheduleResponse>>($"{Tariffs}?search=CTR-MAEU&pageSize=50", ct))!;
        var v1 = page.Items.Single(s => s.ScheduleNo == "CTR-MAEU" && s.VersionNo == 1);
        var v2 = page.Items.Single(s => s.ScheduleNo == "CTR-MAEU" && s.VersionNo == 2);

        Assert.Equal("ACTIVE", v1.Lifecycle);          // v2 is only a draft, so v1 is not superseded
        Assert.Equal(7, v1.ScopeRank);
        Assert.Equal("DRAFT", v2.Lifecycle);
        Assert.True(v2.IsEditable);
        Assert.Equal(v1.LineageId, v2.LineageId);

        var rates = (await sct.GetFromJsonAsync<RateSetResponse>($"{Tariffs}/{v1.ScheduleId}/rates", ct))!;
        var storage = rates.Rates.Single(r => r.ChargeCode == "STORAGE");
        Assert.Equal([new TierItem(1, 45, 30), new TierItem(46, null, 50)], storage.Tiers);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_a_tariff()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var other = await api.ClientForAsync(RevenueApiFactory.SssOwner);
        var sctTariff = (await sct.GetFromJsonAsync<PagedResult<ScheduleResponse>>($"{Tariffs}?search=PUB-LCB", ct))!.Items.Single();

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Tariffs}/{sctTariff.ScheduleId}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Tariffs}/{sctTariff.ScheduleId}/rates", ct)).StatusCode);
        Assert.DoesNotContain((await other.GetFromJsonAsync<PagedResult<ScheduleResponse>>(Tariffs, ct))!.Items, s => s.ScheduleNo == "PUB-LCB");
    }

    [Fact]
    public async Task A_branch_scoped_manager_has_no_tariff_access() =>
        Assert.Equal(HttpStatusCode.Forbidden,
            (await (await api.ClientForAsync(RevenueApiFactory.SctOpsLcb)).GetAsync(Tariffs, TestContext.Current.CancellationToken)).StatusCode);

    // ── the workflow ────────────────────────────────────────────────────────

    /// <summary>Accounts drafts and submits, the owner approves, and from then on the prices are frozen.</summary>
    [Fact]
    public async Task Draft_submit_approve_then_frozen()
    {
        var ct = TestContext.Current.CancellationToken;
        var maker = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var checker = await api.ClientForAsync(RevenueApiFactory.SctOwner);
        var no = NewNo("FLOW");
        try
        {
            var draft = await CreateAsync(maker, Spot(no, $"BKG-{no}"), ct);
            Assert.Equal(("DRAFT", (byte)1), (draft.Lifecycle, draft.ScopeRank));

            var saved = JsonSerializer.Deserialize<RateSetResponse>(await PutRatesAsync(maker, draft, GoodRates, ct), JsonSerializerOptions.Web)!;
            Assert.Equal(3, saved.Rates.Count);
            Assert.NotEqual(draft.RowVersion, saved.RowVersion);   // replacing rates moves the tariff's version

            var reefer = saved.Rates.Single(r => r.EquipmentTypeCode == "40RH");
            Assert.Equal(("40", 12), (reefer.EquipmentSize, reefer.Specificity));   // size taken from the type
            var storage = saved.Rates.Single(r => r.ChargeCode == "STORAGE");
            Assert.Equal(["CAR_DG", "USED_ENGINE"], storage.Conditions[1].Values);  // normalised, SCT's own categories
            Assert.Equal(new short[] { 1, 2 }, storage.Conditions.Select(c => c.SequenceNo));

            var submitted = await DecideAsync(maker, await ReloadAsync(maker, draft.ScheduleId, ct), "submit", ct);
            Assert.Equal("PENDING", submitted.Status);
            Assert.False(submitted.IsEditable);

            // ACCOUNTS has no approve permission at all.
            await DecideAsync(maker, submitted, "approve", ct, HttpStatusCode.Forbidden);

            var approved = await DecideAsync(checker, submitted, "approve", ct);
            Assert.Equal(("APPROVED", "ACTIVE"), (approved.Status, approved.Lifecycle));
            Assert.NotNull(approved.ApprovedAt);

            // Frozen: no rate change, no header change, no withdrawal, no delete.
            await PutRatesAsync(maker, approved, GoodRates, ct, HttpStatusCode.Conflict);
            Assert.Equal(HttpStatusCode.Conflict, (await maker.PutAsJsonAsync($"{Tariffs}/{approved.ScheduleId}", Spot(no, $"BKG-{no}"), ct)).StatusCode);
            await DecideAsync(maker, approved, "withdraw", ct, HttpStatusCode.Conflict);
            Assert.Equal(HttpStatusCode.Conflict, (await maker.DeleteAsync($"{Tariffs}/{approved.ScheduleId}", ct)).StatusCode);
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    private const string SelfApproval = "revenue.tariff_self_approval_allowed";

    [Fact]
    public async Task By_default_the_person_who_submitted_may_approve()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await api.ClientForAsync(RevenueApiFactory.SctOwner);
        var no = NewNo("SELFOK");
        try
        {
            // No tenant row: the declared default (owner 2026-10-06: allowed) decides.
            await TestDatabase.SetSctSettingAsync(SelfApproval, null);
            var draft = await CreateAsync(owner, Spot(no, $"BKG-{no}"), ct);
            await PutRatesAsync(owner, draft, GoodRates[..1], ct);
            var submitted = await DecideAsync(owner, await ReloadAsync(owner, draft.ScheduleId, ct), "submit", ct);

            var approved = await DecideAsync(owner, submitted, "approve", ct);

            Assert.Equal("APPROVED", approved.Status);
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    [Fact]
    public async Task The_person_who_submitted_cannot_approve_when_the_tenant_turns_maker_checker_on()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await api.ClientForAsync(RevenueApiFactory.SctOwner);
        var no = NewNo("SELF");
        try
        {
            await TestDatabase.SetSctSettingAsync(SelfApproval, "false");
            var draft = await CreateAsync(owner, Spot(no, $"BKG-{no}"), ct);
            await PutRatesAsync(owner, draft, GoodRates[..1], ct);
            var submitted = await DecideAsync(owner, await ReloadAsync(owner, draft.ScheduleId, ct), "submit", ct);

            var response = await owner.PostAsJsonAsync($"{Tariffs}/{submitted.ScheduleId}/approve", new { rowVersion = submitted.RowVersion }, ct);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("revenue.tariff_self_approval_allowed", await response.Content.ReadAsStringAsync(ct));
            Assert.Equal("PENDING", (await ReloadAsync(owner, draft.ScheduleId, ct)).Status);
        }
        finally
        {
            await TestDatabase.SetSctSettingAsync(SelfApproval, null);
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    [Fact]
    public async Task A_rejection_needs_a_reason_and_ends_the_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var maker = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var checker = await api.ClientForAsync(RevenueApiFactory.SctOwner);
        var no = NewNo("REJ");
        try
        {
            var draft = await CreateAsync(maker, Spot(no, $"BKG-{no}"), ct);
            await PutRatesAsync(maker, draft, GoodRates[..1], ct);
            var submitted = await DecideAsync(maker, await ReloadAsync(maker, draft.ScheduleId, ct), "submit", ct);

            await DecideAsync(checker, submitted, "reject", ct, HttpStatusCode.BadRequest);
            var rejected = await DecideAsync(checker, submitted, "reject", ct, reason: "Lift rate below cost");

            Assert.Equal(("REJECTED", "Lift rate below cost"), (rejected.Status, rejected.RejectionReason));
            await DecideAsync(checker, rejected, "approve", ct, HttpStatusCode.Conflict);
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    [Fact]
    public async Task A_tariff_with_no_rates_cannot_be_submitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var maker = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var no = NewNo("EMPTY");
        try
        {
            var draft = await CreateAsync(maker, Spot(no, $"BKG-{no}"), ct);
            var response = await maker.PostAsJsonAsync($"{Tariffs}/{draft.ScheduleId}/submit", new { rowVersion = draft.RowVersion }, ct);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("no rates", await response.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    /// <summary>PLAN.md Q4: the public tariff is where free storage days come from.</summary>
    [Fact]
    public async Task A_public_tariff_must_state_its_free_storage_days()
    {
        var ct = TestContext.Current.CancellationToken;
        var maker = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var no = NewNo("PUB");
        try
        {
            var draft = await CreateAsync(maker, new
            {
                scheduleNo = no, name = "Test public", moduleCode = "TOS", scheduleType = "PUBLIC", effectiveFrom = "2031-01-01",
            }, ct);
            await PutRatesAsync(maker, draft, GoodRates[..1], ct);
            var current = await ReloadAsync(maker, draft.ScheduleId, ct);

            var withoutFreeTime = await maker.PostAsJsonAsync($"{Tariffs}/{current.ScheduleId}/submit", new { rowVersion = current.RowVersion }, ct);
            Assert.Equal(HttpStatusCode.Conflict, withoutFreeTime.StatusCode);
            Assert.Contains("free storage days", await withoutFreeTime.Content.ReadAsStringAsync(ct));

            var freeTime = await maker.PutAsJsonAsync($"{Tariffs}/{current.ScheduleId}/free-time", new
            {
                rowVersion = current.RowVersion,
                rules = new object[]
                {
                    new { freeTimeKind = "STORAGE", fullEmpty = "FULL", direction = "IMPORT", cargoGroup = "NORMAL", freeUnits = 3 },
                    new { freeTimeKind = "TRUCK_WAITING", freeUnits = 2 },
                },
            }, ct);
            Assert.Equal(HttpStatusCode.OK, freeTime.StatusCode);
            var rules = (await freeTime.Content.ReadFromJsonAsync<FreeTimeSetResponse>(ct))!;
            Assert.Equal("HOUR", rules.Rules.Single(r => r.FreeTimeKind == "TRUCK_WAITING").Unit);

            var submitted = await DecideAsync(maker, await ReloadAsync(maker, draft.ScheduleId, ct), "submit", ct);
            Assert.Equal("PENDING", submitted.Status);
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }

    /// <summary>
    /// Two approved agreements with identical scope in force on the same day:
    /// the resolver would have two rank-3 candidates and no rule. The second
    /// approval is refused. Dates are far in the future so the fixture's own
    /// CTR-SEA-ESL (2026–27) is not involved.
    /// </summary>
    [Fact]
    public async Task Two_approved_agreements_cannot_cover_the_same_scope_on_the_same_day()
    {
        var ct = TestContext.Current.CancellationToken;
        var maker = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var checker = await api.ClientForAsync(RevenueApiFactory.SctOwner);
        var (first, second, third) = (NewNo("OVA"), NewNo("OVB"), NewNo("OVC"));
        object Contract(string no, string from, string to) => new
        {
            scheduleNo = no, name = "Overlap test", moduleCode = "TOS", scheduleType = "CONTRACT",
            agentPartyCode = "AGT-SEA", customerPartyCode = "CUS-ESL", effectiveFrom = from, effectiveTo = to,
        };
        async Task<ScheduleResponse> SubmitAsync(string no, string from, string to)
        {
            var draft = await CreateAsync(maker, Contract(no, from, to), ct);
            Assert.Equal(3, draft.ScopeRank);
            await PutRatesAsync(maker, draft, GoodRates[..1], ct);
            return await DecideAsync(maker, await ReloadAsync(maker, draft.ScheduleId, ct), "submit", ct);
        }

        try
        {
            await DecideAsync(checker, await SubmitAsync(first, "2035-01-01", "2035-12-31"), "approve", ct);
            var clashing = await SubmitAsync(second, "2035-06-01", "2036-05-31");

            var response = await checker.PostAsJsonAsync($"{Tariffs}/{clashing.ScheduleId}/approve", new { rowVersion = clashing.RowVersion }, ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains(first, await response.Content.ReadAsStringAsync(ct));

            // A PENDING tariff cannot be edited, so the clash is withdrawn; the same
            // scope starting after the first one ends approves.
            await DecideAsync(maker, clashing, "withdraw", ct);
            await DecideAsync(checker, await SubmitAsync(third, "2036-01-01", "2036-12-31"), "approve", ct);
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(first);
            await TestDatabase.RemoveTariffAsync(second);
            await TestDatabase.RemoveTariffAsync(third);
        }
    }

    // ── validation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Parties_must_exist_and_play_the_role_they_are_named_for()
    {
        var ct = TestContext.Current.CancellationToken;
        var maker = await api.ClientForAsync(RevenueApiFactory.SctAccounts);

        var response = await maker.PostAsJsonAsync(Tariffs, new
        {
            scheduleNo = NewNo("ROLE"), name = "Bad parties", moduleCode = "TOS", scheduleType = "CONTRACT",
            forwarderPartyCode = "MAEU",          // a shipping line, not a forwarder
            customerPartyCode = "CUS-NOWHERE",    // does not exist
            effectiveFrom = "2031-01-01",
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("'MAEU' is not set up as a forwarder", body);
        Assert.Contains("Unknown party 'CUS-NOWHERE'", body);
    }

    [Fact]
    public async Task A_public_tariff_cannot_name_a_customer_and_a_billing_module_cannot_have_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var maker = await api.ClientForAsync(RevenueApiFactory.SctAccounts);

        var response = await maker.PostAsJsonAsync(Tariffs, new
        {
            scheduleNo = NewNo("SCOPE"), name = "Bad scope", moduleCode = "REVENUE", scheduleType = "PUBLIC",
            customerPartyCode = "CUS-ESL", effectiveFrom = "2031-01-01", effectiveTo = "2030-01-01",
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("applies to everyone", body);
        Assert.Contains("nothing to price", body);
        Assert.Contains("before the start date", body);
    }

    /// <summary>
    /// A rate table is validated as a whole and every problem comes back at once,
    /// keyed by row — the shape the Excel preview will show.
    /// </summary>
    [Fact]
    public async Task Every_problem_in_a_rate_table_is_reported_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var maker = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var sss = await api.ClientForAsync(RevenueApiFactory.SssOwner);
        var (no, sssNo) = (NewNo("BAD"), NewNo("SBAD"));
        try
        {
            var draft = await CreateAsync(maker, Spot(no, $"BKG-{no}"), ct);
            var body = await PutRatesAsync(maker, draft,
            [
                new { chargeCode = "NOSUCH", billTo = "FWD", paymentTermCode = "CASH", rate = 1 },                                   // [0] Vector spellings
                new { chargeCode = "STORAGE", billTo = "LINE", paymentTermCode = "CREDIT", pricingMethod = "TIERED_INCREMENTAL", tierBasis = "DAY",
                      tiers = new object[] { new { fromQty = 1, toQty = 6, rate = 350 }, new { fromQty = 6, toQty = 12, rate = 550 } } },  // [1] Vector's overlap
                new { chargeCode = "LIFTIN", billTo = "LINE", paymentTermCode = "CREDIT", equipmentTypeCode = "40RH", equipmentSize = "20", rate = 1 }, // [2]
                new { chargeCode = "LIFTIN", billTo = "LINE", paymentTermCode = "CREDIT", equipmentSize = "40", rate = 1 },          // [3]
                new { chargeCode = "LIFTIN", billTo = "LINE", paymentTermCode = "CREDIT", equipmentSize = "40", rate = 2 },          // [4] duplicate of [3]
                new { chargeCode = "PLUGIN", billTo = "LINE", paymentTermCode = "CREDIT", pricingMethod = "TIERED_INCREMENTAL", tierBasis = "DAY",
                      tiers = new object[] { new { fromQty = 1, toQty = (int?)null, rate = 40 } } },                                 // [5] per-hour unit, day tiers
                new { chargeCode = "GATEFEE", billTo = "LINE", paymentTermCode = "CREDIT", rate = 1,
                      conditions = new object[] { new { axis = "WEIGHT_KG", op = "IN", values = new[] { "1" }, modifierOp = "ADD", modifierValue = 1 } } }, // [6]
            ], ct, HttpStatusCode.BadRequest);

            Assert.Contains("Unknown charge code 'NOSUCH'", body);
            Assert.Contains("Unknown bill-to role 'FWD'", body);
            Assert.Contains("rates[1].tiers", body);
            Assert.Contains("'40RH' is a 40ft type, not 20ft", body);
            Assert.Contains("rates[4]", body);
            Assert.Contains("rates[5].billingUnitCode", body);
            Assert.Contains("rates[6].conditions[0]", body);
            Assert.DoesNotContain("\"rates[3]", body);                // row 3 is fine; only row 4 (its duplicate) is keyed

            // SCT's own cargo category is not SSS's.
            var sssDraft = await CreateAsync(sss, new
            {
                scheduleNo = sssNo, name = "SSS test", moduleCode = "TOS", scheduleType = "SPOT",
                bookingRef = $"BKG-{sssNo}", effectiveFrom = "2031-01-01",
            }, ct);
            var sssBody = await PutRatesAsync(sss, sssDraft,
                [new { chargeCode = "LIFTIN", billTo = "CUSTOMER", paymentTermCode = "CASH", cargoCategoryCode = "USED_ENGINE", rate = 1 }],
                ct, HttpStatusCode.BadRequest);
            Assert.Contains("'USED_ENGINE' is not a CARGO_CATEGORY for this depot", sssBody);
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
            await TestDatabase.RemoveTariffAsync(sssNo);
        }
    }

    [Fact]
    public async Task A_stale_edit_is_refused_and_a_draft_can_be_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var maker = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var no = NewNo("DEL");
        try
        {
            var draft = await CreateAsync(maker, Spot(no, $"BKG-{no}"), ct);
            await PutRatesAsync(maker, draft, GoodRates, ct);            // moves the rowVersion

            await PutRatesAsync(maker, draft, GoodRates, ct, HttpStatusCode.Conflict);   // stale

            Assert.Equal(HttpStatusCode.NoContent, (await maker.DeleteAsync($"{Tariffs}/{draft.ScheduleId}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await maker.GetAsync($"{Tariffs}/{draft.ScheduleId}", ct)).StatusCode);
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(no);
        }
    }
}
