using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Data;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// The Container Master's writes (web.tos docs/CONTAINER_MASTER_FOR_API.md, owner 2026-10-12): a standing box's condition
/// (a damaged one picks up the depot's damage hold), its EMR remarks (said at gate-out), and correcting its number — which
/// moves the registry row, its TOS rows and, by the outbox, Revenue's. Boxes come in by a walk-in drop-off (dev_12 fixture)
/// and are registered first, as the screen works on registered boxes.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class ContainerMasterApiTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewTag() => $"ZZCM-{Guid.NewGuid():N}"[..20];

    private static string NewBox()
    {
        var ten = $"ZZCU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    private static async Task RegisterAsync(HttpClient client, string box, CancellationToken ct)
    {
        var made = await client.PostAsJsonAsync("/api/master/containers", new { containerNo = box, ownershipType = "LINE_OWNED" }, ct);
        Assert.True(made.StatusCode == HttpStatusCode.Created, await made.Content.ReadAsStringAsync(ct));
    }

    private static async Task DropOffAsync(HttpClient client, string box, string tag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Gate}/trips")
        {
            Content = JsonContent.Create(new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-7171" },
                rows = new[]
                {
                    new
                    {
                        walkIn = new { fullEmpty = "EMPTY", customerCode = "CUS-TAE", lineCode = "MAEU", equipmentTypeCode = "20GP", remarks = tag },
                        move = new { containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m },
                    },
                },
            }),
        };
        request.Headers.Add(Idempotency.Header, Guid.NewGuid().ToString());
        var saved = await client.SendAsync(request, ct);
        Assert.True(saved.StatusCode == HttpStatusCode.Created, await saved.Content.ReadAsStringAsync(ct));
        Assert.Equal("GATED", (await saved.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!.Rows.Single().Status);
    }

    private static async Task<ContainerInquiryRowResponse> LatestAsync(HttpClient client, string box, CancellationToken ct) =>
        (await client.GetFromJsonAsync<ContainerInquiryResponse>($"/api/tos/containers/{box}", ct))!.Latest!;

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done, string waitingFor, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var value = await read();
            if (done(value)) return value;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Waited 30 s for {waitingFor}.");
            await Task.Delay(250, ct);
        }
    }

    [Fact]
    public async Task A_standing_box_is_marked_damaged_with_its_hold_and_its_EMR_remarks_are_told_at_gate_out()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var tag = NewTag();
        var box = NewBox();
        try
        {
            await RegisterAsync(client, box, ct);
            await DropOffAsync(client, box, tag, ct);
            var visitId = (await LatestAsync(client, box, ct)).ContainerVisitId;

            // A gate clerk cannot: tos.yard.manage is the supervisors' and the yard planner's.
            var clerk = await api.ClientForAsync(TosApiFactory.SctGateLcb);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await clerk.PostAsJsonAsync($"/api/tos/visits/{visitId}/condition", new { conditionCode = "DM", reason = "Found damaged" }, ct)).StatusCode);

            var unknown = await client.PostAsJsonAsync($"/api/tos/visits/{visitId}/condition", new { conditionCode = "ZZ", reason = "Found damaged" }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            Assert.Contains("conditionCode", await unknown.Content.ReadAsStringAsync(ct));

            var damaged = await client.PostAsJsonAsync($"/api/tos/visits/{visitId}/condition",
                new { conditionCode = "DM", gradeCode = "C", reason = "Found damaged on a yard walk-round" }, ct);
            Assert.True(damaged.StatusCode == HttpStatusCode.OK, await damaged.Content.ReadAsStringAsync(ct));
            var stay = (await damaged.Content.ReadFromJsonAsync<YardStayResponse>(ct))!;
            Assert.Equal(("DM", "C"), (stay.ConditionCode, stay.GradeCode));
            Assert.Contains("DAMAGE", stay.HoldsApplied);

            var emr = await client.PutAsJsonAsync($"/api/tos/visits/{visitId}/emr-remarks", new { remarks = "Awaiting line approval for the door repair" }, ct);
            Assert.True(emr.StatusCode == HttpStatusCode.OK, await emr.Content.ReadAsStringAsync(ct));
            var latest = await LatestAsync(client, box, ct);
            Assert.Equal(("DM", "Awaiting line approval for the door repair"), (latest.ConditionCode, latest.EmrRemarks));
            var story = (await client.GetFromJsonAsync<ContainerStoryResponse>($"/api/tos/containers/{box}/story", ct))!;
            Assert.Contains(story.Current!.Events, e => e.EventType == "CONDITION_CHANGE" && e.ToValue == "DM");
            Assert.Contains(story.Current.Events, e => e.EventType == "EMR_REMARKS");

            var preflight = (await client.GetFromJsonAsync<GatePreflightResponse>($"{Gate}/preflight?branchId={SctLcb01}&containerNo={box}&direction=OUT", ct))!;
            Assert.Contains(preflight.Findings, f => f is { Code: "EMR_REMARKS", Severity: "INFO" });
        }
        finally
        {
            await TestDatabase.RemoveWalkInAsync(tag);
            await TestDatabase.RemoveHoldsAsync(box);
            await TestDatabase.RemoveBoxReservationsAsync(box);
            await TestDatabase.RemoveRegistryAsync(box);
        }
    }

    /// <summary>Owner 2026-10-12: a box the depot handles and the registry does not know is registered, as Vector's gate did.</summary>
    [Fact]
    public async Task A_box_first_seen_at_the_gate_is_registered_with_its_type_and_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var tag = NewTag();
        var box = NewBox();
        try
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/master/containers/{box}", ct)).StatusCode);
            await DropOffAsync(client, box, tag, ct);

            using var registered = JsonDocument.Parse(await client.GetStringAsync($"/api/master/containers/{box}", ct));
            var row = registered.RootElement;
            Assert.Equal(("20GP", "MAEU", "LINE_OWNED", "IN_SERVICE"),
                (row.GetProperty("typeCode").GetString(), row.GetProperty("ownerCode").GetString(),
                 row.GetProperty("ownershipType").GetString(), row.GetProperty("status").GetString()));
        }
        finally
        {
            await TestDatabase.RemoveWalkInAsync(tag);
            await TestDatabase.RemoveBoxReservationsAsync(box);
            await TestDatabase.RemoveRegistryAsync(box);
        }
    }

    [Fact]
    public async Task A_corrected_number_moves_the_registry_row_its_stay_booking_EIR_and_Revenues_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var tag = NewTag();
        var (wrong, right, other) = (NewBox(), NewBox(), NewBox());
        try
        {
            await RegisterAsync(client, wrong, ct);
            await RegisterAsync(client, other, ct);
            await DropOffAsync(client, wrong, tag, ct);
            await EventuallyAsync(() => TestDatabase.RevenueRowsAsync(wrong), n => n > 0, $"Revenue to hear of {wrong}", ct);

            using var registered = JsonDocument.Parse(await client.GetStringAsync($"/api/master/containers/{wrong}", ct));
            var rowVersion = registered.RootElement.GetProperty("rowVersion").GetString();

            // A number another registered box has is refused: renumbering would merge two boxes.
            var taken = await client.PostAsJsonAsync($"/api/tos/containers/{wrong}/renumber", new { newContainerNo = other, reason = "Plate misread", rowVersion }, ct);
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);

            var renumbered = await client.PostAsJsonAsync($"/api/tos/containers/{wrong}/renumber",
                new { newContainerNo = right.ToLowerInvariant(), reason = "Mis-keyed at first registration", rowVersion }, ct);
            Assert.True(renumbered.StatusCode == HttpStatusCode.OK, await renumbered.Content.ReadAsStringAsync(ct));
            var moved = (await renumbered.Content.ReadFromJsonAsync<RenumberContainerResponse>(ct))!;
            Assert.Equal((wrong, right), (moved.OldContainerNo, moved.NewContainerNo));
            Assert.True(moved.Moved["containerVisits"] == 1 && moved.Moved["bookingContainers"] == 1 && moved.Moved["gateTransactions"] == 1,
                string.Join(", ", moved.Moved.Select(m => $"{m.Key}={m.Value}")));

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/master/containers/{wrong}", ct)).StatusCode);
            Assert.Equal(right, (await LatestAsync(client, right, ct)).ContainerNo);
            var story = (await client.GetFromJsonAsync<ContainerStoryResponse>($"/api/tos/containers/{right}/story", ct))!;
            Assert.Contains(story.Current!.Events, e => e.EventType == "RENUMBERED" && e.FromValue == wrong && e.ToValue == right);

            using var history = JsonDocument.Parse(await client.GetStringAsync($"/api/master/containers/{right}/history", ct));
            Assert.Contains(history.RootElement.EnumerateArray(), e =>
                e.GetProperty("kind").GetString() == "RENUMBERED" && e.GetProperty("reason").GetString() == "Mis-keyed at first registration");

            // Revenue follows by the outbox.
            await EventuallyAsync(() => TestDatabase.RevenueRowsAsync(wrong), n => n == 0, $"Revenue to move {wrong}'s rows", ct);
            Assert.True(await TestDatabase.RevenueRowsAsync(right) > 0);
        }
        finally
        {
            await TestDatabase.RemoveWalkInAsync(tag);
            await TestDatabase.RemoveBoxReservationsAsync(wrong, right);
            await TestDatabase.RemoveRegistryAsync(wrong, right, other);
        }
    }
}
