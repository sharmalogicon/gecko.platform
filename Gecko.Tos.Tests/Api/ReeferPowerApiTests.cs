using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Data;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Gate;
using Gecko.Tos.Endpoints.Reefer;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// The reefer plug log (TIER3_DESIGN_NOTES §6) through the real host: a reefer
/// standing in the yard is plugged in and out, the journal and the outbox say so
/// in the same transaction, the gate-out unplugs it, and a void of that gate-out
/// plugs it back in.
///
/// Boxes are ZZTU numbers booked with a ZZR- carrier ref, gated in through the
/// real barrier, and removed (with their sessions) in finally.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class ReeferPowerApiTests(TosApiFactory api)
{
    private const string Reefer = "/api/tos/reefer";
    private const string Gate = "/api/tos/gate";
    private const string Prefix = "ZZR-";

    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewRef() => $"{Prefix}{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static string NewBox()
    {
        var ten = $"ZZTU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    private static async Task BookAsync(HttpClient client, string carrierRef, string box, bool reefer, CancellationToken ct,
        Guid? branchId = null)
    {
        var response = await client.PostAsJsonAsync("/api/tos/bookings", new
        {
            branchId = branchId ?? SctLcb01,
            orderTypeCode = "IMP CY/CY",
            lineCode = "MAEU",
            customerCode = "CUS-TAE",
            carrierRef,
            validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            requirements = new object[]
            {
                reefer ? new { equipmentTypeCode = "20RF", qty = 1, reeferSetTempC = (decimal?)-18.0m }
                       : new { equipmentTypeCode = "20GP", qty = 1, reeferSetTempC = (decimal?)null },
            },
            containers = new object[] { new { containerNo = box } },
        }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"booking returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }

    private static async Task<GateTransactionResponse> GateAsync(HttpClient client, string box, string direction,
        DateTimeOffset? at, CancellationToken ct, Guid? branchId = null)
    {
        var response = await client.PostAsJsonAsync($"{Gate}/transactions", new
        {
            branchId = branchId ?? SctLcb01,
            containerNo = box,
            direction,
            tripType = direction == "IN" ? "DROP_OFF_CONT" : "PICK_UP_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 18000m, customsPermitNo = "ZZ-PERMIT-1",
            truck = new { plate = "70-4321", driverName = "Somchai P.", driverLicence = "1234567890123" },
            grossWeightKg = 24100m,
            weightSource = "WEIGHBRIDGE",
            seals = new object[] { new { sealNo = $"ZZ-{box[^4..]}", sealType = "LINE", isIntact = true } },
            transactionAt = at,
        }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"gate {direction} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<GateTransactionResponse>(ct))!;
    }

    /// <summary>Books a box and gates it in at LCB01, <paramref name="gateInAgo"/> ago.</summary>
    private static async Task<GateTransactionResponse> InYardAsync(HttpClient client, string carrierRef, string box, bool reefer,
        TimeSpan gateInAgo, CancellationToken ct, Guid? branchId = null)
    {
        await BookAsync(client, carrierRef, box, reefer, ct, branchId);
        return await GateAsync(client, box, "IN", DateTimeOffset.UtcNow - gateInAgo, ct, branchId);
    }

    private static Task<HttpResponseMessage> PlugInAsync(HttpClient client, string box, CancellationToken ct,
        DateTimeOffset? at = null, decimal? setPointC = null, string? plugPointCode = null) =>
        client.PostAsJsonAsync($"{Reefer}/sessions", new { containerNo = box, pluggedInAt = at, setPointC, plugPointCode }, ct);

    private static async Task<ReeferSessionResponse> ReadAsync(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        Assert.True(response.StatusCode == expected,
            $"expected {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<ReeferSessionResponse>(ct))!;
    }

    private static async Task AssertFieldAsync(HttpResponseMessage response, string field, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400 on {field}, got {(int)response.StatusCode}: {text}");
        using var body = JsonDocument.Parse(text);
        Assert.Contains(body.RootElement.GetProperty("errors").EnumerateObject(),
            p => string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ── plug in, plug out ───────────────────────────────────────────────────

    [Fact]
    public async Task A_reefer_in_the_yard_is_plugged_in_and_out_with_journal_and_outbox()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        try
        {
            var gateIn = await InYardAsync(client, carrierRef, box, reefer: true, TimeSpan.FromHours(3), ct);

            // Offered for plugging in, with the booking line's set point.
            var candidates = (await client.GetFromJsonAsync<PagedResult<ReeferCandidateResponse>>(
                $"{Reefer}/candidates?branchId={SctLcb01}&search={box}", ct))!;
            var candidate = Assert.Single(candidates.Items);
            Assert.Equal(("20RF", -18.0m), (candidate.EquipmentTypeCode, candidate.SuggestedSetPointC));

            // Not before it came through the gate; not in the future; set point in range.
            await AssertFieldAsync(await PlugInAsync(client, box, ct, at: DateTimeOffset.UtcNow.AddHours(-4)), "pluggedInAt", ct);
            await AssertFieldAsync(await PlugInAsync(client, box, ct, at: DateTimeOffset.UtcNow.AddHours(1)), "pluggedInAt", ct);
            await AssertFieldAsync(await PlugInAsync(client, box, ct, setPointC: -60m), "setPointC", ct);

            var pluggedInAt = DateTimeOffset.UtcNow.AddHours(-2);
            var session = await ReadAsync(await PlugInAsync(client, box, ct, pluggedInAt, -18.0m, "R-07"), HttpStatusCode.Created, ct);
            Assert.True(session.IsOpen);
            Assert.True(session.CanManage);
            Assert.Equal((box, "20RF", "R-07", -18.0m), (session.ContainerNo, session.EquipmentTypeCode, session.PlugPointCode, session.SetPointC));
            Assert.Equal(3, session.BillableHours);   // two hours and a few seconds so far: three started hours

            // One open session per box.
            Assert.Equal(HttpStatusCode.Conflict, (await PlugInAsync(client, box, ct)).StatusCode);

            // No longer a candidate; on the plugged-in-now list.
            Assert.Empty((await client.GetFromJsonAsync<PagedResult<ReeferCandidateResponse>>($"{Reefer}/candidates?search={box}", ct))!.Items);
            var open = (await client.GetFromJsonAsync<PagedResult<ReeferSessionResponse>>($"{Reefer}/sessions?search={box}", ct))!;
            Assert.Equal(session.Id, Assert.Single(open.Items).Id);

            // A correction cannot close the session; an honest one moves the rowVersion on.
            var closeByPut = await client.PutAsJsonAsync($"{Reefer}/sessions/{session.Id}", new
            {
                pluggedInAt, pluggedOutAt = DateTimeOffset.UtcNow, plugPointCode = "R-07", setPointC = -18.0m, rowVersion = session.RowVersion,
            }, ct);
            await AssertFieldAsync(closeByPut, "pluggedOutAt", ct);
            var corrected = await ReadAsync(await client.PutAsJsonAsync($"{Reefer}/sessions/{session.Id}", new
            {
                pluggedInAt, plugPointCode = "R-08", setPointC = -20.0m, remarks = "Moved to the next socket", rowVersion = session.RowVersion,
            }, ct), HttpStatusCode.OK, ct);
            Assert.Equal(("R-08", -20.0m), (corrected.PlugPointCode, corrected.SetPointC));

            // Plug-out: rowVersion required, and a stale one is a 409.
            var pluggedOutAt = pluggedInAt.AddMinutes(61);
            await AssertFieldAsync(await client.PostAsJsonAsync($"{Reefer}/sessions/{session.Id}/plug-out", new { pluggedOutAt }, ct), "rowVersion", ct);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{Reefer}/sessions/{session.Id}/plug-out",
                new { pluggedOutAt, rowVersion = session.RowVersion }, ct)).StatusCode);
            await AssertFieldAsync(await client.PostAsJsonAsync($"{Reefer}/sessions/{session.Id}/plug-out",
                new { pluggedOutAt = pluggedInAt.AddMinutes(-1), rowVersion = corrected.RowVersion }, ct), "pluggedOutAt", ct);

            var closed = await ReadAsync(await client.PostAsJsonAsync($"{Reefer}/sessions/{session.Id}/plug-out",
                new { pluggedOutAt, rowVersion = corrected.RowVersion }, ct), HttpStatusCode.OK, ct);
            Assert.False(closed.IsOpen);
            Assert.Equal(("MANUAL", 61, 2), (closed.CloseReason, closed.MinutesPlugged, closed.BillableHours));

            // Already out: a second plug-out is a conflict.
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{Reefer}/sessions/{session.Id}/plug-out",
                new { rowVersion = closed.RowVersion }, ct)).StatusCode);

            var log = (await client.GetFromJsonAsync<PagedResult<ReeferSessionResponse>>($"{Reefer}/sessions?status=CLOSED&search={box}", ct))!;
            Assert.Equal(session.Id, Assert.Single(log.Items).Id);

            // The journal says why the box's story changed.
            await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
            var events = await db.VisitEvents.Where(e => e.ContainerVisitId == session.ContainerVisitId)
                .OrderBy(e => e.VisitEventId).Select(e => e.EventType).ToListAsync(ct);
            Assert.Equal(["GATE_IN", "PLUG_IN", "CORRECTION", "PLUG_OUT"], events);

            // The outbox: full state every time, keyed so Revenue can find the stay.
            var messages = await TestDatabase.OutboxInOrderAsync(session.Id);
            Assert.Equal(["ReeferPlugged", "ReeferSessionCorrected", "ReeferUnplugged"], messages.Select(m => m.MessageType));
            var unplugged = Payload(messages[^1].PayloadJson);
            Assert.Equal(session.Id, unplugged.GetProperty("sessionId").GetGuid());
            Assert.Equal(session.ContainerVisitId, unplugged.GetProperty("containerVisitId").GetGuid());
            Assert.Equal(gateIn.GateTransactionId, unplugged.GetProperty("gateInTransactionId").GetGuid());
            Assert.Equal(box, unplugged.GetProperty("containerNo").GetString());
            Assert.Equal("20RF", unplugged.GetProperty("equipmentTypeCode").GetString());
            Assert.Equal("MANUAL", unplugged.GetProperty("closeReason").GetString());
            Assert.Equal(pluggedOutAt, unplugged.GetProperty("pluggedOutAt").GetDateTimeOffset(), TimeSpan.FromMilliseconds(1));
            Assert.False(unplugged.GetProperty("isVoided").GetBoolean());
            Assert.Equal(JsonValueKind.Null, Payload(messages[0].PayloadJson).GetProperty("pluggedOutAt").ValueKind);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    [Fact]
    public async Task Only_a_reefer_standing_in_the_yard_can_be_plugged_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var dry = NewBox();
        try
        {
            // Never came through the gate.
            await AssertFieldAsync(await PlugInAsync(client, NewBox(), ct), "containerNo", ct);

            // In the yard, but a dry box.
            await InYardAsync(client, carrierRef, dry, reefer: false, TimeSpan.FromMinutes(30), ct);
            await AssertFieldAsync(await PlugInAsync(client, dry, ct), "containerNo", ct);
            Assert.Empty((await client.GetFromJsonAsync<PagedResult<ReeferCandidateResponse>>($"{Reefer}/candidates?search={dry}", ct))!.Items);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    // ── the gate ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gate_out_unplugs_the_reefer_and_a_void_plugs_it_back_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        try
        {
            await InYardAsync(client, carrierRef, box, reefer: true, TimeSpan.FromHours(2), ct);
            var session = await ReadAsync(await PlugInAsync(client, box, ct, DateTimeOffset.UtcNow.AddMinutes(-90)), HttpStatusCode.Created, ct);

            // Gate-out is not refused for a plugged-in box: leaving unplugs it.
            var gateOut = await GateAsync(client, box, "OUT", null, ct);
            var closed = (await client.GetFromJsonAsync<ReeferSessionResponse>($"{Reefer}/sessions/{session.Id}", ct))!;
            Assert.False(closed.IsOpen);
            Assert.Equal(("GATE_OUT", gateOut.GateTransactionId), (closed.CloseReason, closed.CloseGateTransactionId));
            Assert.Equal(gateOut.TransactionAt, closed.PluggedOutAt!.Value, TimeSpan.FromMilliseconds(1));

            // Revenue hears the unplug BEFORE the gate-out.
            var queue = await TestDatabase.OutboxInOrderAsync(session.Id, gateOut.GateTransactionId);
            var unplugged = queue.Single(m => m.MessageType == "ReeferUnplugged");
            var gatedOut = queue.Single(m => m.MessageType == "ContainerGatedOut");
            Assert.True(unplugged.MessageId < gatedOut.MessageId);
            Assert.Equal(gateOut.GateTransactionId, Payload(unplugged.PayloadJson).GetProperty("closeGateTransactionId").GetGuid());

            // Gone out: its power time may be billed, so it cannot be voided.
            Assert.Equal(HttpStatusCode.Conflict,
                (await client.DeleteAsync($"{Reefer}/sessions/{session.Id}?rowVersion={Uri.EscapeDataString(closed.RowVersion)}", ct)).StatusCode);

            // Void the gate-out: the box never left, so it is still plugged in.
            var voided = await client.PostAsJsonAsync($"{Gate}/transactions/{gateOut.GateTransactionId}/void",
                new { reason = "Wrong box on the trailer" }, ct);
            Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
            var reopened = (await client.GetFromJsonAsync<ReeferSessionResponse>($"{Reefer}/sessions/{session.Id}", ct))!;
            Assert.True(reopened.IsOpen);
            Assert.Null(reopened.CloseReason);
            Assert.Null(reopened.CloseGateTransactionId);
            var corrected = (await TestDatabase.OutboxInOrderAsync(session.Id))[^1];
            Assert.Equal("ReeferSessionCorrected", corrected.MessageType);
            Assert.Equal(JsonValueKind.Null, Payload(corrected.PayloadJson).GetProperty("pluggedOutAt").ValueKind);

            // Back in the yard: a session typed by mistake can be voided — with its rowVersion.
            await AssertFieldAsync(await client.DeleteAsync($"{Reefer}/sessions/{session.Id}", ct), "rowVersion", ct);
            Assert.Equal(HttpStatusCode.Conflict,
                (await client.DeleteAsync($"{Reefer}/sessions/{session.Id}?rowVersion={Uri.EscapeDataString(session.RowVersion)}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent,
                (await client.DeleteAsync($"{Reefer}/sessions/{session.Id}?rowVersion={Uri.EscapeDataString(reopened.RowVersion)}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Reefer}/sessions/{session.Id}", ct)).StatusCode);
            Assert.Empty((await client.GetFromJsonAsync<PagedResult<ReeferSessionResponse>>($"{Reefer}/sessions?status=ALL&search={box}", ct))!.Items);
            var last = (await TestDatabase.OutboxInOrderAsync(session.Id))[^1];
            Assert.Equal("ReeferSessionVoided", last.MessageType);
            Assert.True(Payload(last.PayloadJson).GetProperty("isVoided").GetBoolean());
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    // ── who may ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_plug_log_stays_inside_its_depot_and_its_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var atLcb = NewBox();
        var atBkk = NewBox();
        try
        {
            await InYardAsync(owner, carrierRef, atLcb, reefer: true, TimeSpan.FromMinutes(30), ct);
            await InYardAsync(owner, carrierRef, atBkk, reefer: true, TimeSpan.FromMinutes(30), ct, TestDatabase.SctBkk01);

            // The LCB01 gate clerk plugs in at LCB01...
            var clerk = await api.ClientForAsync(TosApiFactory.SctGateLcb);
            var mine = await ReadAsync(await PlugInAsync(clerk, atLcb, ct), HttpStatusCode.Created, ct);
            Assert.True(mine.CanManage);

            // ...but not at another depot, and cannot even ask about it.
            Assert.Equal(HttpStatusCode.Forbidden, (await PlugInAsync(clerk, atBkk, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await clerk.GetAsync($"{Reefer}/sessions?branchId={TestDatabase.SctBkk01}", ct)).StatusCode);
            var theirs = await ReadAsync(await PlugInAsync(owner, atBkk, ct), HttpStatusCode.Created, ct);
            var clerkSees = (await clerk.GetFromJsonAsync<PagedResult<ReeferSessionResponse>>($"{Reefer}/sessions?status=ALL&pageSize=200", ct))!;
            Assert.Contains(clerkSees.Items, s => s.Id == mine.Id);
            Assert.DoesNotContain(clerkSees.Items, s => s.Id == theirs.Id);
            Assert.Equal(HttpStatusCode.NotFound, (await clerk.PostAsJsonAsync($"{Reefer}/sessions/{theirs.Id}/plug-out",
                new { rowVersion = theirs.RowVersion }, ct)).StatusCode);

            // Another tenant does not learn the session, or the box, exists.
            var other = await api.ClientForAsync(TosApiFactory.SssOwner);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Reefer}/sessions/{mine.Id}", ct)).StatusCode);
            Assert.Empty((await other.GetFromJsonAsync<PagedResult<ReeferSessionResponse>>($"{Reefer}/sessions?status=ALL&search={atLcb}", ct))!.Items);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{Reefer}/sessions/{mine.Id}/plug-out",
                new { rowVersion = mine.RowVersion }, ct)).StatusCode);
            await AssertFieldAsync(await PlugInAsync(other, atLcb, ct), "containerNo", ct);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }
}
