using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Application;
using Gecko.Revenue.Endpoints.Reefer;
using Gecko.Revenue.Endpoints.Tariffs;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// Reefer power billing, Revenue side: TOS's REEFER_SESSION messages → the
/// projection → per started hour × the tenant's tariff, shown on the TOS reefer
/// page (GET /reefer/power) and quoted at the cash window on the way out.
///
/// The owner has given no rate, so the fixture tenants have none either: SCT's
/// only REEFER code (PLUGIN) has no CASH variant. Priced cases use a throwaway
/// charge code made through the MasterData API and throwaway tariffs made and
/// approved through the Revenue API — both removed at the end. Messages are
/// handed to the real handlers (as the TOS dispatcher would) with synthetic ids
/// above <see cref="TestDatabase.SyntheticMessageIdFloor"/>; boxes are ZZTU.
/// </summary>
[Collection(RevenueApiCollection.Name)]
public sealed class ReeferPowerApiTests(RevenueApiFactory api)
{
    private const string Power = "/api/revenue/reefer/power";
    private const string Tariffs = "/api/revenue/tariffs";
    private const string Charges = "/api/master/charge-codes";

    /// <summary>EDI_COORDINATOR: neither tos.reefer.view nor revenue.tariff.view.</summary>
    private const string SctEdi = "edi@sct.co.th";
    /// <summary>GATE_CLERK at SCT-LCB01 only.</summary>
    private const string SctGateLcb = "gate1.lcb@sct.co.th";

    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");
    private static readonly Guid SctBkk01 = Guid.Parse("775AE785-33A5-F111-9B0D-00919E4766D5");

    private static long _messageId = TestDatabase.SyntheticMessageIdFloor + Random.Shared.NextInt64(1_000_000_000_000);

    private static long NextMessageId() => Interlocked.Increment(ref _messageId);

    private static string NewBox() => $"ZZTU{Random.Shared.Next(0, 9_999_999):D7}";

    // ── feeding the projection the way the TOS dispatcher does ──────────────

    private IServiceScope Scope(Guid tenant)
    {
        var scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<OutboxTenantScope>().TenantId = tenant;
        return scope;
    }

    private async Task DeliverAsync<THandler>(Guid tenant, long messageId, string aggregateType, Guid aggregateId, string type, object payload, CancellationToken ct)
        where THandler : IOutboxHandler
    {
        using var scope = Scope(tenant);
        var handler = scope.ServiceProvider.GetServices<IOutboxHandler>().OfType<THandler>().Single();
        await handler.HandleAsync(new OutboxMessage(messageId, tenant, aggregateType, aggregateId, type,
            JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), null, DateTimeOffset.UtcNow, 1, 5), ct);
    }

    private sealed record Session(Guid SessionId, Guid VisitId, Guid? GateIn, string Box, Guid BranchId, DateTimeOffset In);

    private Task PlugAsync(Guid tenant, Session s, string type, DateTimeOffset changedAt, CancellationToken ct,
        DateTimeOffset? pluggedOut = null, bool voided = false, long? messageId = null, DateTimeOffset? pluggedIn = null) =>
        DeliverAsync<ReeferSessionHandler>(tenant, messageId ?? NextMessageId(), ReeferSessionHandler.AggregateType, s.SessionId, type, new
        {
            sessionId = s.SessionId, containerVisitId = s.VisitId, gateInTransactionId = s.GateIn, containerNo = s.Box,
            branchId = s.BranchId, equipmentTypeCode = "40RH", pluggedInAt = pluggedIn ?? s.In, pluggedInBy = Guid.NewGuid(),
            pluggedOutAt = pluggedOut, pluggedOutBy = pluggedOut is null ? (Guid?)null : Guid.NewGuid(),
            closeReason = pluggedOut is null ? null : "MANUAL", closeGateTransactionId = (Guid?)null,
            isVoided = voided, changedAt,
        }, ct);

    /// <summary>A gate-in opens the stay the window prices from (projection.container_stay).</summary>
    private Task GateInAsync(Guid gateIn, string box, DateTimeOffset at, CancellationToken ct) =>
        DeliverAsync<GateEventHandler>(TestDatabase.Sct, NextMessageId(), "GATE_TRANSACTION", gateIn, GateEventHandler.GatedIn, new
        {
            gateTransactionId = gateIn, eirNo = (string?)null, branchId = SctLcb01, containerNo = box, direction = "IN",
            movementCode = (string?)null, fullEmpty = "FULL", bookingId = (Guid?)null, bookingContainerId = (Guid?)null,
            equipmentTypeCode = "40RH", isoCode = "45R1", lineCode = (string?)null, transactionAt = at,
        }, ct);

    /// <summary>A plugged-in box, in the yard since 3 h ago, plugged in 61 minutes ago and still plugged.</summary>
    private async Task<Session> ReeferInYardAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var s = new Session(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NewBox(), SctLcb01, now.AddMinutes(-61));
        await GateInAsync(s.GateIn!.Value, s.Box, now.AddHours(-3), ct);
        await PlugAsync(TestDatabase.Sct, s, ReeferSessionHandler.Plugged, now.AddMinutes(-61), ct);
        return s;
    }

    /// <summary>What the cash window would quote for this box's gate-out on a booking numbered <paramref name="orderNo"/>.</summary>
    private async Task<MovementQuote> WindowQuoteAsync(string box, string orderNo, CancellationToken ct)
    {
        using var scope = Scope(TestDatabase.Sct);
        var sp = scope.ServiceProvider;
        var branch = (await sp.GetRequiredService<BranchCalendar>().BranchAsync(SctLcb01, ct))!;
        var plan = new BookingPlan
        {
            BookingId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BranchId = SctLcb01, OrderNo = orderNo, Status = "OPEN",
            OrderTypeCode = "ZZ-NO-MENU", CustomerPartyCode = "CUS-TAE", LastReason = "TEST", PayloadJson = "{}",
        };
        var container = new BookingPlanContainer
        {
            BookingContainerId = Guid.NewGuid(), TenantId = TestDatabase.Sct, BookingId = plan.BookingId, ContainerNo = box,
            EquipmentTypeCode = "40RH", IsReefer = true, StepsJson = "[]", IsCurrent = true,
        };
        var step = new OrderTypeStepRef(Guid.Empty, Guid.Empty, "ZZ_OUT", 2, true, true, false, false, false, false, false,
            "OUT", "FULL", false, false);
        return await sp.GetRequiredService<CashQuoter>().QuoteAsync(plan, container, step, branch, null, DateTimeOffset.UtcNow, ct);
    }

    private static async Task<List<ReeferPowerResponse>> PowerAsync(HttpClient client, CancellationToken ct, params Guid[] visits)
    {
        var response = await client.GetAsync($"{Power}?containerVisitIds={string.Join(",", visits)}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"power returned {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<List<ReeferPowerResponse>>(body, JsonSerializerOptions.Web)!;
    }

    // ── the projection ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_plug_session_is_copied_and_an_older_message_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        // Yesterday, so every time here is in the past whatever the machine's clock says.
        var t0 = DateTimeOffset.UtcNow.AddDays(-1);
        var s = new Session(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NewBox(), SctLcb01, t0);
        try
        {
            await PlugAsync(TestDatabase.Sct, s, ReeferSessionHandler.Plugged, t0, ct);
            var unplugId = NextMessageId();
            await PlugAsync(TestDatabase.Sct, s, ReeferSessionHandler.Unplugged, t0.AddHours(2), ct, pluggedOut: t0.AddMinutes(121), messageId: unplugId);

            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                var row = await db.ReeferSessions.SingleAsync(r => r.SessionId == s.SessionId, ct);
                Assert.Equal((s.VisitId, s.GateIn, s.Box, t0.AddMinutes(121), "MANUAL", false, ReeferSessionHandler.Unplugged),
                    (row.ContainerVisitId, row.InGateTransactionId, row.ContainerNo, row.PluggedOutAt, row.CloseReason, row.IsVoided, row.LastMessageType));
            }

            // 2 h 01 min = 3 started hours.
            var owner = await api.ClientForAsync(RevenueApiFactory.SctOwner);
            var closed = Assert.Single(await PowerAsync(owner, ct, s.VisitId));
            Assert.Equal((s.Box, 3, 121), (closed.ContainerNo, closed.BillableHours, closed.MinutesPlugged));

            // A correction TOS made BEFORE the unplug, delivered late, is stale: nothing changes.
            await PlugAsync(TestDatabase.Sct, s, ReeferSessionHandler.Corrected, t0.AddHours(1), ct, pluggedIn: t0.AddMinutes(-600));
            // The same message twice is handled once (the inbox).
            await PlugAsync(TestDatabase.Sct, s, ReeferSessionHandler.Unplugged, t0.AddHours(3), ct, pluggedOut: t0.AddMinutes(500), messageId: unplugId);

            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                var row = await db.ReeferSessions.SingleAsync(r => r.SessionId == s.SessionId, ct);
                Assert.Equal((t0, t0.AddMinutes(121), unplugId), (row.PluggedInAt, row.PluggedOutAt, row.LastMessageId));
                var inbox = await db.Inboxes.Where(i => i.AggregateId == s.SessionId).OrderBy(i => i.MessageId).Select(i => i.Outcome).ToListAsync(ct);
                Assert.Equal(["APPLIED", "APPLIED", "STALE"], inbox);
            }

            // A newer correction wins; then a void takes the session out of billing for good.
            await PlugAsync(TestDatabase.Sct, s, ReeferSessionHandler.Corrected, t0.AddHours(4), ct, pluggedOut: t0.AddMinutes(60));
            Assert.Equal(1, Assert.Single(await PowerAsync(owner, ct, s.VisitId)).BillableHours);

            await PlugAsync(TestDatabase.Sct, s, ReeferSessionHandler.Voided, t0.AddHours(5), ct, pluggedOut: t0.AddMinutes(60), voided: true);
            var voided = Assert.Single(await PowerAsync(owner, ct, s.VisitId));
            Assert.Equal((ReeferOutcomes.NoSessions, 0, (decimal?)null), (voided.Outcome, voided.BillableHours, voided.Amount));
        }
        finally
        {
            await TestDatabase.RemoveReeferTestRowsAsync();
        }
    }

    // ── nothing configured → nothing charged, and the answer says why ───────

    [Fact]
    public async Task Without_an_hourly_reefer_charge_code_nothing_is_charged()
    {
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var s = await ReeferInYardAsync(ct);
            var owner = await api.ClientForAsync(RevenueApiFactory.SctOwner);

            var power = Assert.Single(await PowerAsync(owner, ct, s.VisitId));
            Assert.Equal((ReeferOutcomes.ChargeCodeNotSet, 2, (decimal?)null, (string?)null), (power.Outcome, power.BillableHours, power.Amount, power.ChargeCode));
            Assert.True(power.MinutesPlugged >= 61);
            Assert.Contains("REEFER charge code", power.Message);

            var quote = await WindowQuoteAsync(s.Box, "ZZ-RF-NOCODE", ct);
            Assert.DoesNotContain(quote.Lines, l => l.Kind == QuoteLine.Reefer);
            Assert.Contains(quote.Tried, t => t.ChargeCode == "REEFER" && t.Outcome == ReeferOutcomes.ChargeCodeNotSet);
            Assert.False(quote.ReeferApplies);
        }
        finally
        {
            await TestDatabase.RemoveReeferTestRowsAsync();
        }
    }

    // ── a code, then a rate: priced by the tariff, hours × rate ─────────────

    private sealed record ChargeRow(string ChargeCode, string RowVersion);
    private sealed record ChargeDetail(ChargeRow Charge);

    [Fact]
    public async Task The_tenant_tariff_prices_the_started_hours_and_only_an_hourly_rate_counts()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await api.ClientForAsync(RevenueApiFactory.SctOwner);
        var maker = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var code = $"ZZRF{Guid.NewGuid():N}"[..10].ToUpperInvariant();
        var publicNo = $"ZZ-RFP-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var spotNo = $"ZZ-RFS-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var spotBooking = $"ZZ-RFB-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var perBoxNo = $"ZZ-RFC-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var perBoxBooking = $"ZZ-RFD-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        try
        {
            // ── MDM: an hourly REEFER charge code, paid in cash by the customer
            var created = await owner.PostAsJsonAsync(Charges, new
            {
                chargeCode = code, descriptionEn = "Reefer power (test)", moduleCode = "TOS", chargeType = "REEFER",
                chargeCategory = "REEFER", billingUnitCode = "PER_HOUR",
            }, ct);
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
            var charge = (await created.Content.ReadFromJsonAsync<ChargeDetail>(ct))!.Charge;
            var variants = await owner.PutAsJsonAsync($"{Charges}/{code}/variants", new
            {
                rowVersion = charge.RowVersion,
                variants = new object[] { new { billTo = "CUSTOMER", paymentTermCode = "CASH", taxCode = "VAT7" } },
            }, ct);
            Assert.True(variants.StatusCode == HttpStatusCode.OK, await variants.Content.ReadAsStringAsync(ct));

            var s = await ReeferInYardAsync(ct);

            // ── a code but no rate: RATE_NOT_SET, no amount, no line at the window
            var unpriced = Assert.Single(await PowerAsync(owner, ct, s.VisitId));
            Assert.Equal((ReeferOutcomes.RateNotSet, 2, (decimal?)null, code), (unpriced.Outcome, unpriced.BillableHours, unpriced.Amount, unpriced.ChargeCode));
            var noRate = await WindowQuoteAsync(s.Box, "ZZ-RF-NORATE", ct);
            Assert.DoesNotContain(noRate.Lines, l => l.Kind == QuoteLine.Reefer);
            Assert.Contains(noRate.Tried, t => t.ChargeCode == code && t.Outcome == ReeferOutcomes.RateNotSet && t.Amount is null);
            Assert.False(noRate.ReeferApplies);

            // ── the tenant's public tariff at SCT-BKK01: 45.50 per hour (LCB01's public tariff has no such rate)
            await ApprovedTariffAsync(maker, owner, new
            {
                scheduleNo = publicNo, name = "Reefer power test", moduleCode = "TOS", scheduleType = "PUBLIC",
                branchId = SctBkk01, effectiveFrom = "2026-01-01",
            }, new object[] { new { chargeCode = code, billTo = "CUSTOMER", paymentTermCode = "CASH", rate = 45.50m } }, ct);

            var now = DateTimeOffset.UtcNow;
            var bkk = new Session(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NewBox(), SctBkk01, now.AddMinutes(-61));
            await PlugAsync(TestDatabase.Sct, bkk, ReeferSessionHandler.Plugged, now, ct);
            var both = await PowerAsync(owner, ct, s.VisitId, bkk.VisitId);
            Assert.Equal(ReeferOutcomes.RateNotSet, both.Single(p => p.ContainerVisitId == s.VisitId).Outcome);
            var priced = both.Single(p => p.ContainerVisitId == bkk.VisitId);
            Assert.Equal((ReeferOutcomes.Priced, 2, (decimal?)91.00m, code, "THB"),
                (priced.Outcome, priced.BillableHours, priced.Amount, priced.ChargeCode, priced.Currency));   // 2 h × 45.50
            Assert.Contains(publicNo, priced.Message);

            // ── at the window, a booking's own (SPOT) hourly rate: a REEFER line, quantity = hours
            await ApprovedTariffAsync(maker, owner, Spot(spotNo, spotBooking),
                new object[] { new { chargeCode = code, billTo = "CUSTOMER", paymentTermCode = "CASH", rate = 45.50m } }, ct);
            var window = await WindowQuoteAsync(s.Box, spotBooking, ct);
            var line = Assert.Single(window.Lines, l => l.Kind == QuoteLine.Reefer);
            Assert.Equal((code, "CUSTOMER", 2m, (decimal?)45.50m, 91.00m, 6.37m),
                (line.ChargeCode, line.BillTo, line.Quantity, line.UnitRate, line.Amount, line.TaxAmount));
            Assert.True(window.ReeferApplies);
            Assert.Equal(97.37m, window.Total);

            // ── a rate that is not per hour is never multiplied by hours
            await ApprovedTariffAsync(maker, owner, Spot(perBoxNo, perBoxBooking),
                new object[] { new { chargeCode = code, billTo = "CUSTOMER", paymentTermCode = "CASH", billingUnitCode = "PER_CONTAINER", rate = 500m } }, ct);
            var perBox = await WindowQuoteAsync(s.Box, perBoxBooking, ct);
            Assert.DoesNotContain(perBox.Lines, l => l.Kind == QuoteLine.Reefer);
            var tried = Assert.Single(perBox.Tried, t => t.ChargeCode == code);
            Assert.Equal((ReeferOutcomes.RateNotSet, (decimal?)null), (tried.Outcome, tried.Amount));
            Assert.Contains(tried.Trail, t => t.Contains("reefer rate must be per hour"));
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(perBoxNo);
            await TestDatabase.RemoveTariffAsync(spotNo);
            await TestDatabase.RemoveTariffAsync(publicNo);
            await TestDatabase.RemoveReeferTestRowsAsync();
            if (await owner.GetAsync($"{Charges}/{code}", ct) is { StatusCode: HttpStatusCode.OK } current)
            {
                var rowVersion = (await current.Content.ReadFromJsonAsync<ChargeDetail>(ct))!.Charge.RowVersion;
                Assert.Equal(HttpStatusCode.NoContent,
                    (await owner.DeleteAsync($"{Charges}/{code}?rowVersion={Uri.EscapeDataString(rowVersion)}", ct)).StatusCode);
            }
        }
    }

    // ── KORAKIT's SE004: a per-DAY rate tiered by DAY, priced on the calendar days plugged in ──

    [Fact]
    public async Task A_rate_tiered_by_day_prices_the_calendar_days_plugged_in_not_the_hours()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await api.ClientForAsync(RevenueApiFactory.SctOwner);
        var maker = await api.ClientForAsync(RevenueApiFactory.SctAccounts);
        var code = $"ZZRD{Guid.NewGuid():N}"[..10].ToUpperInvariant();
        var spotNo = $"ZZ-RFY-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var booking = $"ZZ-RFZ-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        try
        {
            var created = await owner.PostAsJsonAsync(Charges, new
            {
                chargeCode = code, descriptionEn = "Reefer power by day (test)", moduleCode = "TOS", chargeType = "REEFER",
                chargeCategory = "REEFER", billingUnitCode = "PER_HOUR",
            }, ct);
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
            var charge = (await created.Content.ReadFromJsonAsync<ChargeDetail>(ct))!.Charge;
            var variants = await owner.PutAsJsonAsync($"{Charges}/{code}/variants", new
            {
                rowVersion = charge.RowVersion,
                variants = new object[] { new { billTo = "CUSTOMER", paymentTermCode = "CASH", taxCode = "VAT7" } },
            }, ct);
            Assert.True(variants.StatusCode == HttpStatusCode.OK, await variants.Content.ReadAsStringAsync(ct));

            // The slabs as KORAKIT wrote them: days 1-23 at 84.11, day 24 at 1,000, day 25 on at 50.
            await ApprovedTariffAsync(maker, owner, Spot(spotNo, booking), new object[]
            {
                new
                {
                    chargeCode = code, billTo = "CUSTOMER", paymentTermCode = "CASH", billingUnitCode = "PER_DAY",
                    pricingMethod = "TIERED_INCREMENTAL", tierBasis = "DAY",
                    tiers = new object[] { new { fromQty = 1, toQty = 23, rate = 84.11m }, new { fromQty = 24, toQty = 24, rate = 1000m },
                                           new { fromQty = 25, rate = 50m } },
                },
            }, ct);

            // Plugged in 25 hours ago and still plugged: 26 started hours, but priced on the branch-local
            // calendar days the plug spans (2, or 3 just after local midnight), as the old system counted.
            var now = DateTimeOffset.UtcNow;
            var s = new Session(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NewBox(), SctLcb01, now.AddHours(-25));
            await GateInAsync(s.GateIn!.Value, s.Box, now.AddHours(-26), ct);
            await PlugAsync(TestDatabase.Sct, s, ReeferSessionHandler.Plugged, now.AddHours(-25), ct);
            BranchClockInfo branch;
            using (var scope = Scope(TestDatabase.Sct))
                branch = (await scope.ServiceProvider.GetRequiredService<BranchCalendar>().BranchAsync(SctLcb01, ct))!;
            var days = branch.LocalDate(now).DayNumber - branch.LocalDate(now.AddHours(-25)).DayNumber + 1;

            var window = await WindowQuoteAsync(s.Box, booking, ct);
            var line = Assert.Single(window.Lines, l => l.Kind == QuoteLine.Reefer);
            Assert.Equal((code, (decimal)days, days * 84.11m), (line.ChargeCode, line.Quantity, line.Amount));
            var tried = Assert.Single(window.Tried, t => t.ChargeCode == code);
            Assert.Contains(tried.Trail, t => t.Contains($"{days} calendar day(s) plugged in"));
            Assert.True(window.ReeferApplies);
        }
        finally
        {
            await TestDatabase.RemoveTariffAsync(spotNo);
            await TestDatabase.RemoveReeferTestRowsAsync();
            if (await owner.GetAsync($"{Charges}/{code}", ct) is { StatusCode: HttpStatusCode.OK } current)
            {
                var rowVersion = (await current.Content.ReadFromJsonAsync<ChargeDetail>(ct))!.Charge.RowVersion;
                Assert.Equal(HttpStatusCode.NoContent,
                    (await owner.DeleteAsync($"{Charges}/{code}?rowVersion={Uri.EscapeDataString(rowVersion)}", ct)).StatusCode);
            }
        }
    }

    private static object Spot(string scheduleNo, string booking) => new
    {
        scheduleNo, name = "Reefer power test (spot)", moduleCode = "TOS", scheduleType = "SPOT",
        bookingRef = booking, customerPartyCode = "CUS-TAE", effectiveFrom = "2026-01-01",
    };

    private static async Task ApprovedTariffAsync(HttpClient maker, HttpClient checker, object header, object[] rates, CancellationToken ct)
    {
        var created = await maker.PostAsJsonAsync(Tariffs, header, ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"create: {await created.Content.ReadAsStringAsync(ct)}");
        var schedule = (await created.Content.ReadFromJsonAsync<ScheduleResponse>(ct))!;

        var saved = await maker.PutAsJsonAsync($"{Tariffs}/{schedule.ScheduleId}/rates", new { rowVersion = schedule.RowVersion, rates }, ct);
        Assert.True(saved.StatusCode == HttpStatusCode.OK, $"rates: {await saved.Content.ReadAsStringAsync(ct)}");
        schedule = (await maker.GetFromJsonAsync<ScheduleResponse>($"{Tariffs}/{schedule.ScheduleId}", ct))!;

        if (schedule.ScheduleType == "PUBLIC")
        {
            // A public tariff must state its storage free days; this one states them for a box
            // no test moves (45ft DG empty export), so it never changes anyone's storage.
            var freeTime = await maker.PutAsJsonAsync($"{Tariffs}/{schedule.ScheduleId}/free-time", new
            {
                rowVersion = schedule.RowVersion,
                rules = new object[] { new { freeTimeKind = "STORAGE", fullEmpty = "EMPTY", direction = "EXPORT", cargoGroup = "DG", equipmentSize = "45", freeUnits = 0 } },
            }, ct);
            Assert.True(freeTime.StatusCode == HttpStatusCode.OK, $"free time: {await freeTime.Content.ReadAsStringAsync(ct)}");
            schedule = (await maker.GetFromJsonAsync<ScheduleResponse>($"{Tariffs}/{schedule.ScheduleId}", ct))!;
        }

        var submitted = await maker.PostAsJsonAsync($"{Tariffs}/{schedule.ScheduleId}/submit", new { rowVersion = schedule.RowVersion }, ct);
        Assert.True(submitted.StatusCode == HttpStatusCode.OK, $"submit: {await submitted.Content.ReadAsStringAsync(ct)}");
        schedule = (await submitted.Content.ReadFromJsonAsync<ScheduleResponse>(ct))!;

        var approved = await checker.PostAsJsonAsync($"{Tariffs}/{schedule.ScheduleId}/approve", new { rowVersion = schedule.RowVersion }, ct);
        Assert.True(approved.StatusCode == HttpStatusCode.OK, $"approve: {await approved.Content.ReadAsStringAsync(ct)}");
    }

    // ── who may see what ────────────────────────────────────────────────────

    [Fact]
    public async Task Visits_show_only_at_the_callers_branches_and_never_across_tenants()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var lcb = new Session(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NewBox(), SctLcb01, now.AddMinutes(-30));
        var bkk = new Session(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NewBox(), SctBkk01, now.AddMinutes(-30));
        try
        {
            await PlugAsync(TestDatabase.Sct, lcb, ReeferSessionHandler.Plugged, now, ct);
            await PlugAsync(TestDatabase.Sct, bkk, ReeferSessionHandler.Plugged, now, ct);

            var owner = await api.ClientForAsync(RevenueApiFactory.SctOwner);
            Assert.Equal([lcb.VisitId, bkk.VisitId], (await PowerAsync(owner, ct, lcb.VisitId, bkk.VisitId)).Select(p => p.ContainerVisitId));

            // A gate clerk at LCB01 (tos.reefer.view there only) sees the LCB box; the BKK one is simply absent.
            var clerk = await api.ClientForAsync(SctGateLcb);
            var seen = Assert.Single(await PowerAsync(clerk, ct, lcb.VisitId, bkk.VisitId));
            Assert.Equal((lcb.VisitId, 1), (seen.ContainerVisitId, seen.BillableHours));

            // Neither permission at all: the door is shut.
            var edi = await api.ClientForAsync(SctEdi);
            Assert.Equal(HttpStatusCode.Forbidden, (await edi.GetAsync($"{Power}?containerVisitIds={lcb.VisitId}", ct)).StatusCode);

            // Another tenant: RLS hides SCT's sessions, so the visits do not exist for it.
            var sss = await api.ClientForAsync(RevenueApiFactory.SssOwner);
            Assert.Empty(await PowerAsync(sss, ct, lcb.VisitId, bkk.VisitId));

            // An unknown visit is absent, not an error; repeated parameters work as well as commas.
            var repeated = await owner.GetFromJsonAsync<List<ReeferPowerResponse>>(
                $"{Power}?containerVisitIds={lcb.VisitId}&containerVisitIds={Guid.NewGuid()}", ct);
            Assert.Equal(lcb.VisitId, Assert.Single(repeated!).ContainerVisitId);
        }
        finally
        {
            await TestDatabase.RemoveReeferTestRowsAsync();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("?containerVisitIds=")]
    [InlineData("?containerVisitIds=not-a-guid")]
    [InlineData("?containerVisitIds=MANY")]
    public async Task A_bad_visit_list_is_a_400_on_containerVisitIds(string query)
    {
        var ct = TestContext.Current.CancellationToken;
        if (query.EndsWith("MANY")) query = $"?containerVisitIds={string.Join(",", Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()))}";
        var owner = await api.ClientForAsync(RevenueApiFactory.SctOwner);

        var response = await owner.GetAsync($"{Power}{query}", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(problem.GetProperty("errors").TryGetProperty("containerVisitIds", out _), problem.ToString());
    }
}
