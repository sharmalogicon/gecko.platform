using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Data;
using Gecko.Tos.Endpoints.Vessels;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Vessel calls through the real host (PLAN §4.1, batch A). The assertions target
/// what the database cannot hold on its own (§10): codes resolved through MDM,
/// cut-off ordering, one physical call per voyage, a line voyage once per port,
/// cancelled calls frozen, actuals ordered — and tenant isolation end to end.
///
/// Every test that writes uses its own call reference and voyage, and removes
/// the call in finally.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class VesselCallApiTests(TosApiFactory api)
{
    private const string Calls = "/api/tos/vessel-calls";

    /// <summary>SCT's fixture vessel with no calls of its own — nothing to collide with.</summary>
    private const string Vessel = "CHAOPHRAYA";

    private static string NewRef() => $"ZZ-{Guid.NewGuid():N}"[..14].ToUpperInvariant();
    private static string NewVoyage() => $"T{Random.Shared.Next(100000, 999999)}";

    private static readonly DateTimeOffset Etd = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(12), TimeSpan.Zero).AddHours(15);

    private static object Call(string callRef, string voyage, DateTimeOffset? eta = null, DateTimeOffset? etd = null,
        object[]? lines = null, object[]? cutoffs = null) => new
    {
        callRef,
        vesselCode = Vessel,
        portCode = "THLCH",
        terminalCode = "LCB-A0",
        operatorVoyageOut = voyage,
        eta = eta ?? Etd.AddHours(-30),
        etd = etd ?? Etd,
        lines = lines ?? new object[]
        {
            new { lineCode = "MAEU", voyageOut = voyage + "M" },
            new { lineCode = "HLCU", voyageOut = voyage + "H", agentCode = "AGT-SEA" },
        },
        cutoffs = cutoffs ?? new object[]
        {
            new { kind = "PORT_DRY", at = Etd.AddHours(-36) },
            new { kind = "PORT_REEFER", at = Etd.AddHours(-30) },
            new { kind = "YARD_REEFER", at = Etd.AddHours(-54) },
            new { kind = "YARD_DRY", branchId = TestDatabase.SctBkk01, at = Etd.AddHours(-66) },
            new { kind = "VGM", at = Etd.AddHours(-40) },
        },
    };

    private static async Task<VesselCallDetailResponse> CreateAsync(HttpClient client, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(Calls, body, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"create returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<VesselCallDetailResponse>(ct))!;
    }

    private static async Task<string> ExpectAsync(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expected, $"expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return body;
    }

    private static T Read<T>(string body) => JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web)!;

    // ── the fixture, as the API sees it ─────────────────────────────────────

    [Fact]
    public async Task The_fixture_schedule_reads_back_with_derived_status_and_line_voyages()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);

        var page = (await client.GetFromJsonAsync<PagedResult<VesselCallSummaryResponse>>($"{Calls}?pageSize=200", ct))!;
        var fixtures = page.Items.Where(c => !c.CallRef.StartsWith("ZZ-")).ToList();

        Assert.Equal(10, fixtures.Count);
        var cancelled = Assert.Single(fixtures, c => c.CallRef == "SIAMSTAR-2642N");
        Assert.Equal("CANCELLED", cancelled.Status);
        var shared = Assert.Single(fixtures, c => c.CallRef == "EASTPIONEER-069S");
        Assert.Equal(3, shared.Lines.Count);                       // one ship, three lines, three voyages
        Assert.Contains("EGLV 0692-069S", shared.Lines);
        Assert.Equal("EASTERN PIONEER", shared.VesselName);         // resolved through MDM, not copied
        Assert.True(fixtures.Zip(fixtures.Skip(1)).All(p => p.First.Etd <= p.Second.Etd), "the schedule is in ETD order");
    }

    [Fact]
    public async Task The_most_specific_cutoff_wins_for_a_line_at_a_branch()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var call = (await client.GetFromJsonAsync<PagedResult<VesselCallSummaryResponse>>($"{Calls}?search=BLUEMERIDIAN-2640W", ct))!.Items.Single();

        var whole = (await client.GetFromJsonAsync<List<EffectiveCutoffResponse>>($"{Calls}/{call.VesselCallId}/effective-cutoffs", ct))!;
        var specific = (await client.GetFromJsonAsync<List<EffectiveCutoffResponse>>(
            $"{Calls}/{call.VesselCallId}/effective-cutoffs?lineCode=MAEU&branchId={TestDatabase.SctBkk01}", ct))!;

        Assert.All(whole, c => Assert.Equal("whole call", c.AppliesTo));
        var yardDry = specific.Single(c => c.Kind == "YARD_DRY");
        Assert.Equal("this branch", yardDry.AppliesTo);
        Assert.Equal(TimeSpan.FromHours(6), whole.Single(c => c.Kind == "YARD_DRY").At - yardDry.At);
        Assert.Equal("this line", specific.Single(c => c.Kind == "PORT_REEFER").AppliesTo);

        var notOnCall = await client.GetAsync($"{Calls}/{call.VesselCallId}/effective-cutoffs?lineCode=COSU", ct);
        await ExpectAsync(notOnCall, HttpStatusCode.BadRequest, ct);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_or_touch_a_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(TosApiFactory.SctOwner);
        var sss = await api.ClientForAsync(TosApiFactory.SssOwner);

        var sctCall = (await sct.GetFromJsonAsync<PagedResult<VesselCallSummaryResponse>>($"{Calls}?search=OCEANCREST-047E", ct))!.Items.Single();
        var sssCalls = (await sss.GetFromJsonAsync<PagedResult<VesselCallSummaryResponse>>($"{Calls}?pageSize=200", ct))!;

        Assert.DoesNotContain(sssCalls.Items, c => c.VesselCallId == sctCall.VesselCallId);
        Assert.Equal(HttpStatusCode.NotFound, (await sss.GetAsync($"{Calls}/{sctCall.VesselCallId}", ct)).StatusCode);
        var cancel = await sss.PostAsJsonAsync($"{Calls}/{sctCall.VesselCallId}/cancel",
            new { reason = "not yours to cancel", rowVersion = "AAAAAAAAAAA=" }, ct);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
    }

    // ── create ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_call_is_created_whole_and_a_missing_yard_cutoff_is_derived()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var callRef = NewRef();
        try
        {
            var created = await CreateAsync(client, Call(callRef, NewVoyage()), ct);

            Assert.Equal("OPEN", created.Call.Status);
            Assert.Equal("CHAO PHRAYA BAY", created.Call.VesselName);
            Assert.Equal(2, created.Lines.Count);
            Assert.Equal("AGT-SEA", created.Lines.Single(l => l.LineCode == "HLCU").AgentCode);

            // PORT_DRY has only a Bangkok-branch YARD_DRY, so the other branches
            // get a derived whole-call one; PORT_REEFER has its own YARD_REEFER.
            Assert.Equal(6, created.Cutoffs.Count);
            var derived = Assert.Single(created.Cutoffs, c => c.Source == "DERIVED");
            Assert.Equal(("YARD_DRY", (Guid?)null, Etd.AddHours(-60)), (derived.Kind, derived.BranchId, derived.At));

            // Replace the set with port cut-offs only: both yard cut-offs are derived.
            var replaced = await client.PutAsJsonAsync($"{Calls}/{created.Call.VesselCallId}/cutoffs", new
            {
                cutoffs = new object[]
                {
                    new { kind = "PORT_DRY", at = Etd.AddHours(-36) },
                    new { kind = "PORT_REEFER", at = Etd.AddHours(-30) },
                },
            }, ct);
            var detail = Read<VesselCallDetailResponse>(await ExpectAsync(replaced, HttpStatusCode.OK, ct));

            var derivedDry = detail.Cutoffs.Single(c => c.Kind == "YARD_DRY");
            Assert.Equal("DERIVED", derivedDry.Source);
            Assert.Equal((short)24, derivedDry.DerivedLeadHours);
            Assert.Equal(Etd.AddHours(-60), derivedDry.At);
            Assert.Equal(Etd.AddHours(-54), detail.Cutoffs.Single(c => c.Kind == "YARD_REEFER").At);
        }
        finally { await TestDatabase.RemoveCallAsync(callRef); }
    }

    [Fact]
    public async Task The_laden_release_date_is_a_header_field_of_the_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var callRef = NewRef();
        var voyage = NewVoyage();
        try
        {
            var release = Etd.AddHours(-72);
            var created = await client.PostAsJsonAsync(Calls, new
            {
                callRef, vesselCode = Vessel, portCode = "THLCH", operatorVoyageOut = voyage,
                eta = Etd.AddHours(-30), etd = Etd, ladenReleaseAt = release,
                lines = new object[] { new { lineCode = "MAEU", voyageOut = voyage + "M" } },
            }, ct);
            var call = Read<VesselCallDetailResponse>(await ExpectAsync(created, HttpStatusCode.Created, ct)).Call;
            Assert.Equal(release, call.LadenReleaseAt);

            // The header PUT stores what it is sent: a new date, then none.
            object Header(DateTimeOffset? ladenReleaseAt, string rowVersion) => new
            {
                callRef, vesselCode = Vessel, portCode = "THLCH", operatorVoyageOut = voyage,
                eta = Etd.AddHours(-30), etd = Etd, ladenReleaseAt, rowVersion,
            };
            var later = Read<VesselCallDetailResponse>(await ExpectAsync(
                await client.PutAsJsonAsync($"{Calls}/{call.VesselCallId}", Header(release.AddHours(6), call.RowVersion), ct), HttpStatusCode.OK, ct)).Call;
            Assert.Equal(release.AddHours(6), later.LadenReleaseAt);
            var none = Read<VesselCallDetailResponse>(await ExpectAsync(
                await client.PutAsJsonAsync($"{Calls}/{call.VesselCallId}", Header(null, later.RowVersion), ct), HttpStatusCode.OK, ct)).Call;
            Assert.Null(none.LadenReleaseAt);
        }
        finally { await TestDatabase.RemoveCallAsync(callRef); }
    }

    [Fact]
    public async Task Vectors_schedule_defects_are_refused_on_create()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var callRef = NewRef();
        try
        {
            // V-5: ETA = ETD on every Vector row.
            var sameTime = await client.PostAsJsonAsync(Calls, Call(callRef, NewVoyage(), eta: Etd, etd: Etd), ct);
            Assert.Contains("\"etd\"", await ExpectAsync(sameTime, HttpStatusCode.BadRequest, ct));

            // V-6: yard after port, and a cut-off after the ship sails.
            var badCutoffs = await client.PostAsJsonAsync(Calls, Call(callRef, NewVoyage(), cutoffs:
            [
                new { kind = "PORT_DRY", at = Etd.AddHours(-36) },
                new { kind = "YARD_DRY", at = Etd.AddHours(-30) },
                new { kind = "SI", at = Etd.AddHours(3) },
            ]), ct);
            var body = await ExpectAsync(badCutoffs, HttpStatusCode.BadRequest, ct);
            Assert.Contains("cutoffs[1]", body);
            Assert.Contains("cutoffs[2]", body);

            // Codes that are not what they claim to be.
            var wrongCodes = await client.PostAsJsonAsync(Calls, new
            {
                callRef, vesselCode = "NO-SUCH-SHIP", portCode = "THLCH", terminalCode = "THLCH",
                eta = Etd.AddHours(-30), etd = Etd,
                lines = new object[] { new { lineCode = "CUS-TAE", voyageOut = "X1" } },   // a customer, not a line
                cutoffs = new object[] { new { kind = "YARD_DRY", branchId = TestDatabase.SssLcb01, at = Etd.AddHours(-40) } },
            }, ct);
            body = await ExpectAsync(wrongCodes, HttpStatusCode.BadRequest, ct);
            Assert.Contains("vesselCode", body);
            Assert.Contains("terminalCode", body);
            Assert.Contains("not a shipping line", body);
            Assert.Contains("cutoffs[0].branchId", body);   // another tenant's branch is simply unknown

            var noLines = await client.PostAsJsonAsync(Calls, Call(callRef, NewVoyage(), lines: []), ct);
            Assert.Contains("at least one line", await ExpectAsync(noLines, HttpStatusCode.BadRequest, ct));
        }
        finally { await TestDatabase.RemoveCallAsync(callRef); }
    }

    [Fact]
    public async Task One_physical_call_is_one_row_and_a_line_voyage_calls_a_port_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var (first, second) = (NewRef(), NewRef());
        var voyage = NewVoyage();
        try
        {
            await CreateAsync(client, Call(first, voyage), ct);

            var sameRef = await client.PostAsJsonAsync(Calls, Call(first, NewVoyage()), ct);
            await ExpectAsync(sameRef, HttpStatusCode.Conflict, ct);

            // Vector stored one call once per agent x booking type: 12,291 calls in 15,515 rows.
            var sameVoyage = await client.PostAsJsonAsync(Calls, Call(second, voyage,
                lines: [new { lineCode = "ONEY", voyageOut = NewVoyage() }]), ct);
            Assert.Contains("add the line to that call", await ExpectAsync(sameVoyage, HttpStatusCode.Conflict, ct));

            var lineVoyageAgain = await client.PostAsJsonAsync(Calls, Call(second, NewVoyage(), lines:
                [new { lineCode = "MAEU", voyageOut = voyage + "M" }]), ct);
            Assert.Contains("already calls this port", await ExpectAsync(lineVoyageAgain, HttpStatusCode.BadRequest, ct));
        }
        finally
        {
            await TestDatabase.RemoveCallAsync(first);
            await TestDatabase.RemoveCallAsync(second);
        }
    }

    // ── change ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_header_updates_with_optimistic_concurrency_and_keeps_etd_after_every_cutoff()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var callRef = NewRef();
        var voyage = NewVoyage();
        try
        {
            var created = await CreateAsync(client, Call(callRef, voyage), ct);
            var id = created.Call.VesselCallId;
            object Header(DateTimeOffset etd, string rowVersion) => new
            {
                callRef, vesselCode = Vessel, portCode = "THLCH", terminalCode = "LCB-B4", operatorVoyageOut = voyage,
                eta = Etd.AddHours(-30), etd, rowVersion,
            };

            // The ship now sails before its own port cut-off: refused, nothing moves.
            var early = await client.PutAsJsonAsync($"{Calls}/{id}", Header(Etd.AddHours(-37), created.Call.RowVersion), ct);
            Assert.Contains("PORT_DRY", await ExpectAsync(early, HttpStatusCode.BadRequest, ct));

            var moved = await client.PutAsJsonAsync($"{Calls}/{id}", Header(Etd.AddHours(6), created.Call.RowVersion), ct);
            var updated = Read<VesselCallDetailResponse>(await ExpectAsync(moved, HttpStatusCode.OK, ct));
            Assert.Equal("LCB-B4", updated.Call.TerminalCode);
            Assert.Equal(Etd.AddHours(6), updated.Call.Etd);

            var stale = await client.PutAsJsonAsync($"{Calls}/{id}", Header(Etd.AddHours(8), created.Call.RowVersion), ct);
            await ExpectAsync(stale, HttpStatusCode.Conflict, ct);
        }
        finally { await TestDatabase.RemoveCallAsync(callRef); }
    }

    [Fact]
    public async Task A_line_cannot_leave_the_call_while_it_has_its_own_cutoff()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var callRef = NewRef();
        var voyage = NewVoyage();
        try
        {
            var created = await CreateAsync(client, Call(callRef, voyage), ct);
            var id = created.Call.VesselCallId;
            await ExpectAsync(await client.PutAsJsonAsync($"{Calls}/{id}/cutoffs", new
            {
                cutoffs = new object[]
                {
                    new { kind = "PORT_DRY", at = Etd.AddHours(-36) },
                    new { kind = "PORT_REEFER", lineCode = "HLCU", at = Etd.AddHours(-26) },
                },
            }, ct), HttpStatusCode.OK, ct);

            var dropHlcu = await client.PutAsJsonAsync($"{Calls}/{id}/lines",
                new { lines = new object[] { new { lineCode = "MAEU", voyageOut = voyage + "M" } } }, ct);
            Assert.Contains("PORT_REEFER for HLCU", await ExpectAsync(dropHlcu, HttpStatusCode.BadRequest, ct));

            var addOney = await client.PutAsJsonAsync($"{Calls}/{id}/lines", new
            {
                lines = new object[]
                {
                    new { lineCode = "MAEU", voyageOut = voyage + "M" },
                    new { lineCode = "HLCU", voyageOut = voyage + "H" },
                    new { lineCode = "ONEY", voyageIn = voyage + "N", serviceCode = "FE3" },
                },
            }, ct);
            var detail = Read<VesselCallDetailResponse>(await ExpectAsync(addOney, HttpStatusCode.OK, ct));
            Assert.Equal(["HLCU", "MAEU", "ONEY"], detail.Lines.Select(l => l.LineCode));
            Assert.Null(detail.Lines.Single(l => l.LineCode == "HLCU").AgentCode);   // a replace is the whole truth
        }
        finally { await TestDatabase.RemoveCallAsync(callRef); }
    }

    [Fact]
    public async Task Actuals_are_ordered_and_a_call_that_happened_cannot_be_cancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var callRef = NewRef();
        var now = DateTimeOffset.UtcNow;
        var etd = now.AddHours(20);
        try
        {
            var created = await CreateAsync(client, Call(callRef, NewVoyage(), eta: now.AddHours(-10), etd: etd,
                cutoffs: [new { kind = "PORT_DRY", at = now.AddHours(-12) }]), ct);
            var id = created.Call.VesselCallId;

            var berthedBeforeArriving = await client.PostAsJsonAsync($"{Calls}/{id}/actuals",
                new { rowVersion = created.Call.RowVersion, ata = now.AddHours(-2), atb = now.AddHours(-3) }, ct);
            Assert.Contains("\"atb\"", await ExpectAsync(berthedBeforeArriving, HttpStatusCode.BadRequest, ct));

            var future = await client.PostAsJsonAsync($"{Calls}/{id}/actuals",
                new { rowVersion = created.Call.RowVersion, ata = now.AddHours(5) }, ct);
            await ExpectAsync(future, HttpStatusCode.BadRequest, ct);

            var arrived = Read<VesselCallDetailResponse>(await ExpectAsync(await client.PostAsJsonAsync($"{Calls}/{id}/actuals",
                new { rowVersion = created.Call.RowVersion, ata = now.AddHours(-3), atb = now.AddHours(-1) }, ct), HttpStatusCode.OK, ct));
            Assert.Equal("WORKING", arrived.Call.Status);

            var cancel = await client.PostAsJsonAsync($"{Calls}/{id}/cancel",
                new { reason = "Ship skipped the port", rowVersion = arrived.Call.RowVersion }, ct);
            Assert.Contains("has arrived", await ExpectAsync(cancel, HttpStatusCode.Conflict, ct));
        }
        finally { await TestDatabase.RemoveCallAsync(callRef); }
    }

    [Fact]
    public async Task A_cancelled_call_keeps_its_reason_and_is_frozen()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var callRef = NewRef();
        try
        {
            var created = await CreateAsync(client, Call(callRef, NewVoyage()), ct);
            var id = created.Call.VesselCallId;

            var noReason = await client.PostAsJsonAsync($"{Calls}/{id}/cancel", new { reason = "", rowVersion = created.Call.RowVersion }, ct);
            await ExpectAsync(noReason, HttpStatusCode.BadRequest, ct);

            var cancelled = Read<VesselCallDetailResponse>(await ExpectAsync(await client.PostAsJsonAsync($"{Calls}/{id}/cancel",
                new { reason = "Blank sailing announced by the line", rowVersion = created.Call.RowVersion }, ct), HttpStatusCode.OK, ct));
            Assert.Equal("CANCELLED", cancelled.Call.Status);
            Assert.Equal("Blank sailing announced by the line", cancelled.Call.CancelReason);
            Assert.NotNull(cancelled.Call.CancelledAt);

            var edit = await client.PutAsJsonAsync($"{Calls}/{id}/cutoffs",
                new { cutoffs = new object[] { new { kind = "PORT_DRY", at = Etd.AddHours(-40) } } }, ct);
            Assert.Contains("cancelled", await ExpectAsync(edit, HttpStatusCode.Conflict, ct));

            var listed = (await client.GetFromJsonAsync<PagedResult<VesselCallSummaryResponse>>($"{Calls}?status=CANCELLED&search={callRef}", ct))!;
            Assert.Single(listed.Items);
        }
        finally { await TestDatabase.RemoveCallAsync(callRef); }
    }
}
