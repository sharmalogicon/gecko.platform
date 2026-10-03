using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Data;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Vessels;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Bookings through the real host (PLAN §4.2 / §5.1, batch B). The assertions
/// target the rules the database cannot hold alone (§10): the order type drives
/// direction, booking type and the vessel-call requirement; parties play the
/// role they are named for; a box is checked against the registry, the check
/// digit and every other booking; the plan is snapshotted per box; nothing that
/// has history can be cancelled or unassigned.
///
/// Every test books with a carrier_ref starting ZZ- and removes it in finally.
/// Boxes come from SCT's real registry (dev_04) and are free in the fixtures.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class BookingApiTests(TosApiFactory api)
{
    private const string Bookings = "/api/tos/bookings";
    private const string Prefix = "ZZ-";

    // Free SCT registry boxes (not active on any fixture booking).
    private const string Gp20A = "AKLU6018567", Gp20B = "AKLU6019856", Gp40 = "APZU4230891", Hc40 = "AMFU8539517";

    /// <summary>Active on fixture booking BK-SCT-LCB01-2609-00002 (MAEU import D/O).</summary>
    private const string BusyBox = "MRKU4122335";

    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewRef() => $"{Prefix}{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    /// <summary>A well-formed number with a correct check digit that no registry holds.</summary>
    private static string UnknownBox()
    {
        var ten = $"ZZTU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    private static object ImportDo(string carrierRef, object[]? requirements = null, object[]? containers = null) => new
    {
        branchId = SctLcb01,
        orderTypeCode = "IMP CY/CY",
        lineCode = "MAEU",
        customerCode = "CUS-TAE",
        carrierRef,
        validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
        requirements = requirements ?? new object[] { new { equipmentTypeCode = "20GP", qty = 2 } },
        containers,
    };

    private static async Task<BookingDetailResponse> CreateAsync(HttpClient client, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(Bookings, body, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"create returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!;
    }

    private static async Task<string> ExpectAsync(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expected, $"expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return body;
    }

    private static T Read<T>(string body) => JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web)!;

    private static Task<HttpResponseMessage> AssignAsync(HttpClient client, Guid bookingId, CancellationToken ct, params object[] containers) =>
        client.PostAsJsonAsync($"{Bookings}/{bookingId}/containers", new { containers }, ct);

    // ── amending the header (PUT /bookings/{id}) ────────────────────────────

    /// <summary>
    /// A booking is raised before the voyage is known: create minimal, amend as the
    /// facts arrive. The PUT replaces the WHOLE header (a field left out is cleared),
    /// takes the rowVersion (stale = 409), and refuses what may no longer change.
    /// </summary>
    [Fact]
    public async Task The_header_is_amended_as_a_whole_with_a_row_version_and_some_of_it_can_no_longer_change()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var root = NewRef()[..14];
        var (carrierRef, corrected) = ($"{root}-A", $"{root}-B");
        try
        {
            var booking = (await CreateAsync(client, ImportDo(carrierRef), ct)).Booking;
            Assert.Null(booking.HaulierCode);
            Assert.Null(booking.PodPortCode);

            Task<HttpResponseMessage> Put(object body) => client.PutAsJsonAsync($"{Bookings}/{booking.BookingId}", body, ct);
            object Header(string? rowVersion, string? haulierCode = null, string? podPortCode = null, string? remarks = null,
                Guid? branchId = null, string orderTypeCode = "IMP CY/CY", string lineCode = "MAEU", object[]? requirements = null) => new
            {
                branchId = branchId ?? SctLcb01, orderTypeCode, lineCode, customerCode = "CUS-TAE", carrierRef = corrected,
                haulierCode, podPortCode, polPortCode = podPortCode is null ? null : "THLCH", customerRef = "PO-7781", remarks,
                validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20)), requirements, rowVersion,
            };

            // ── facts arrive: the haulier, the ports, a corrected carrier reference
            var amended = Read<BookingDetailResponse>(await ExpectAsync(
                await Put(Header(booking.RowVersion, "HAU-SHT", "SGSIN", "Customer rang: collect before Friday")), HttpStatusCode.OK, ct)).Booking;
            Assert.Equal((corrected, "HAU-SHT", "THLCH", "SGSIN", "PO-7781", "Customer rang: collect before Friday"),
                (amended.CarrierRef, amended.HaulierCode, amended.PolPortCode, amended.PodPortCode, amended.CustomerRef, amended.Remarks));
            Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20)), amended.ValidTo);
            Assert.Equal(booking.OrderNo, amended.OrderNo);                 // the number never changes
            Assert.NotEqual(booking.RowVersion, amended.RowVersion);

            // ── the version read before that amend is stale now; none at all is a 400
            Assert.Equal(HttpStatusCode.Conflict, (await Put(Header(booking.RowVersion, "HAU-LCH"))).StatusCode);
            Assert.Contains("rowVersion", await ExpectAsync(await Put(Header(null, "HAU-LCH")), HttpStatusCode.BadRequest, ct));

            // ── it replaces the whole header: what is left out is cleared
            var cleared = Read<BookingDetailResponse>(await ExpectAsync(await Put(Header(amended.RowVersion)), HttpStatusCode.OK, ct)).Booking;
            Assert.Equal((null, null, null, null), (cleared.HaulierCode, cleared.PolPortCode, cleared.PodPortCode, cleared.Remarks));
            Assert.Equal("PO-7781", cleared.CustomerRef);

            // ── what a PUT may not do, each named on its field
            Assert.Contains("podPortCode", await ExpectAsync(await Put(Header(cleared.RowVersion, podPortCode: "ZZNOPE")), HttpStatusCode.BadRequest, ct));
            Assert.Contains("branchId", await ExpectAsync(await Put(Header(cleared.RowVersion, branchId: TestDatabase.SctBkk01)), HttpStatusCode.BadRequest, ct));
            Assert.Contains("requirements", await ExpectAsync(
                await Put(Header(cleared.RowVersion, requirements: [new { equipmentTypeCode = "20GP", qty = 1 }])), HttpStatusCode.BadRequest, ct));

            // ── once a box is on the booking, its order type and its line are fixed
            await ExpectAsync(await AssignAsync(client, booking.BookingId, ct, new { containerNo = UnknownBox() }), HttpStatusCode.OK, ct);
            var withBox = (await client.GetFromJsonAsync<BookingDetailResponse>($"{Bookings}/{booking.BookingId}", ct))!.Booking;
            Assert.Contains("orderTypeCode", await ExpectAsync(await Put(Header(withBox.RowVersion, orderTypeCode: "IMP CFS")), HttpStatusCode.BadRequest, ct));
            Assert.Contains("lineCode", await ExpectAsync(await Put(Header(withBox.RowVersion, lineCode: "HLCU")), HttpStatusCode.BadRequest, ct));
            // ...and everything else still is not.
            var late = Read<BookingDetailResponse>(await ExpectAsync(await Put(Header(withBox.RowVersion, "HAU-LCH")), HttpStatusCode.OK, ct)).Booking;
            Assert.Equal("HAU-LCH", late.HaulierCode);

            // ── a booking that is no longer OPEN is not amended
            await ExpectAsync(await client.PostAsJsonAsync($"{Bookings}/{booking.BookingId}/cancel",
                new { reason = "Customer cancelled the delivery order", rowVersion = late.RowVersion }, ct), HttpStatusCode.OK, ct);
            var gone = (await client.GetFromJsonAsync<BookingDetailResponse>($"{Bookings}/{booking.BookingId}", ct))!.Booking;
            Assert.Equal(HttpStatusCode.Conflict, (await Put(Header(gone.RowVersion, "HAU-SHT"))).StatusCode);
        }
        finally { await TestDatabase.RemoveBookingsAsync(root); }
    }

    // ── a booking is created once ───────────────────────────────────────────

    private static Task<HttpResponseMessage> PostWithKeyAsync(HttpClient client, string key, object body, CancellationToken ct)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, Bookings) { Content = JsonContent.Create(body) };
        message.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(message, ct);
    }

    [Fact]
    public async Task The_same_idempotency_key_creates_one_booking_and_a_carrier_reference_is_on_one_live_booking()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var root = NewRef()[..13];
        try
        {
            // ── no carrier reference (most gate bookings): only the key can tell a repeat from a new booking
            object NoRef(int qty) => new
            {
                branchId = SctLcb01, orderTypeCode = "IMP CY/CY", lineCode = "MAEU", customerCode = "CUS-TAE",
                customerRef = root,   // kept on the row so the test can find and remove what it made
                requirements = new object[] { new { equipmentTypeCode = "20GP", qty } },
            };
            var key = Guid.NewGuid().ToString();
            var first = Read<BookingDetailResponse>(await ExpectAsync(await PostWithKeyAsync(client, key, NoRef(1), ct), HttpStatusCode.Created, ct));
            var second = Read<BookingDetailResponse>(await ExpectAsync(await PostWithKeyAsync(client, key, NoRef(1), ct), HttpStatusCode.Created, ct));
            var third = Read<BookingDetailResponse>(await ExpectAsync(await PostWithKeyAsync(client, key, NoRef(1), ct), HttpStatusCode.Created, ct));
            Assert.Equal((first.Booking.BookingId, first.Booking.OrderNo), (second.Booking.BookingId, second.Booking.OrderNo));
            Assert.Equal(first.Booking.OrderNo, third.Booking.OrderNo);

            // The same key with a different body is a client bug, not a booking.
            Assert.Contains("different request", await ExpectAsync(await PostWithKeyAsync(client, key, NoRef(2), ct), HttpStatusCode.UnprocessableEntity, ct));
            // Another key is another booking; no key at all behaves as it always did.
            var other = Read<BookingDetailResponse>(await ExpectAsync(await PostWithKeyAsync(client, Guid.NewGuid().ToString(), NoRef(1), ct), HttpStatusCode.Created, ct));
            Assert.NotEqual(first.Booking.OrderNo, other.Booking.OrderNo);
            Assert.Contains("Idempotency-Key", await ExpectAsync(await PostWithKeyAsync(client, new string('k', 101), NoRef(1), ct), HttpStatusCode.BadRequest, ct));

            // ── a carrier reference names ONE live booking at a depot
            var reference = $"{root}-R";
            var original = await CreateAsync(client, ImportDo(reference), ct);
            var duplicate = await ExpectAsync(await client.PostAsJsonAsync(Bookings, ImportDo(reference.ToLowerInvariant()), ct), HttpStatusCode.Conflict, ct);
            using (var problem = JsonDocument.Parse(duplicate))
            {
                Assert.Equal(original.Booking.OrderNo, problem.RootElement.GetProperty("existingOrderNo").GetString());
                Assert.Contains(original.Booking.OrderNo, problem.RootElement.GetProperty("title").GetString());
            }
            // Amending another booking onto that reference is refused the same way; keeping its own is not.
            var amend = new
            {
                branchId = SctLcb01, orderTypeCode = "IMP CY/CY", lineCode = "MAEU", customerCode = "CUS-TAE", customerRef = root,
                carrierRef = reference, rowVersion = first.Booking.RowVersion,
            };
            await ExpectAsync(await client.PutAsJsonAsync($"{Bookings}/{first.Booking.BookingId}", amend, ct), HttpStatusCode.Conflict, ct);
            await ExpectAsync(await client.PutAsJsonAsync($"{Bookings}/{original.Booking.BookingId}",
                new { branchId = SctLcb01, orderTypeCode = "IMP CY/CY", lineCode = "MAEU", carrierRef = reference, remarks = "same reference, kept", rowVersion = original.Booking.RowVersion }, ct),
                HttpStatusCode.OK, ct);

            // A cancelled booking frees its reference.
            var current = (await client.GetFromJsonAsync<BookingDetailResponse>($"{Bookings}/{original.Booking.BookingId}", ct))!.Booking;
            await ExpectAsync(await client.PostAsJsonAsync($"{Bookings}/{original.Booking.BookingId}/cancel",
                new { reason = "Raised by mistake", rowVersion = current.RowVersion }, ct), HttpStatusCode.OK, ct);
            await CreateAsync(client, ImportDo(reference), ct);
        }
        finally
        {
            await TestDatabase.RemoveBookingsAsync(root);
            await TestDatabase.RemoveBookingsByCustomerRefAsync(root);
        }
    }

    // ── the fixture ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_fixture_bookings_read_back_with_derived_progress_and_per_box_steps()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);

        // Fetched by number, not off page 1: dev_03's gate day put hundreds of
        // WALK_IN bookings in front of dev_02's hand-written ones.
        async Task<BookingSummaryResponse> FindAsync(string orderNo) =>
            (await client.GetFromJsonAsync<PagedResult<BookingSummaryResponse>>($"{Bookings}?search={orderNo}", ct))!
            .Items.Single(b => b.OrderNo == orderNo);

        // Valid to 16 September, never started: the depot's calendar says EXPIRED.
        var expired = await FindAsync("BK-SCT-BKK01-2609-00001");
        Assert.Equal(("OPEN", "EXPIRED", "SCT-BKK01"), (expired.Status, expired.Progress, expired.BranchCode));

        var dO = await FindAsync("BK-SCT-LCB01-2609-00002");
        var detail = (await client.GetFromJsonAsync<BookingDetailResponse>($"{Bookings}/{dO.BookingId}", ct))!;
        Assert.Equal(("IMP CY/CY", "IMPORT", "IMPORT_DO"), (detail.Booking.OrderTypeCode, detail.Booking.DirectionCode, detail.Booking.BookingTypeCode));
        Assert.Equal(3, detail.Containers.Count);
        Assert.All(detail.Containers, box =>
            Assert.Equal(["FULL_IN", "FULL_OUT", "MTY_IN"], box.Steps.Select(s => s.MovementCode)));

        // Search finds a booking by one of its boxes.
        var byBox = (await client.GetFromJsonAsync<PagedResult<BookingSummaryResponse>>($"{Bookings}?search={BusyBox}", ct))!;
        Assert.Contains(byBox.Items, b => b.OrderNo == "BK-SCT-LCB01-2609-00002");

        var expiredOnly = (await client.GetFromJsonAsync<PagedResult<BookingSummaryResponse>>($"{Bookings}?progress=EXPIRED&pageSize=200", ct))!;
        Assert.All(expiredOnly.Items, b => Assert.Equal("EXPIRED", b.Progress));
        Assert.Contains(expiredOnly.Items, b => b.OrderNo == "BK-SCT-BKK01-2609-00001");
    }

    [Fact]
    public async Task Another_tenants_bookings_are_invisible()
    {
        var ct = TestContext.Current.CancellationToken;
        var sct = await api.ClientForAsync(TosApiFactory.SctOwner);
        var sss = await api.ClientForAsync(TosApiFactory.SssOwner);

        var sctBooking = (await sct.GetFromJsonAsync<PagedResult<BookingSummaryResponse>>($"{Bookings}?search=BK-SCT-LCB01-2609-00002", ct))!.Items.Single();
        Assert.Equal(HttpStatusCode.NotFound, (await sss.GetAsync($"{Bookings}/{sctBooking.BookingId}", ct)).StatusCode);
        var sssList = (await sss.GetFromJsonAsync<PagedResult<BookingSummaryResponse>>($"{Bookings}?pageSize=200", ct))!;
        Assert.DoesNotContain(sssList.Items, b => b.OrderNo.StartsWith("BK-SCT"));
    }

    // ── create ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_import_DO_is_created_with_pre_advised_boxes_and_each_box_gets_its_steps()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            var created = await CreateAsync(client, ImportDo(carrierRef, containers:
                [new { containerNo = Gp20A, declaredSealNo = "ML1234567" }, new { containerNo = Gp20B.ToLowerInvariant() }]), ct);

            Assert.Matches(@"^BK-SCT-LCB01-\d{4}-\d{5}$", created.Booking.OrderNo);   // the depot's own series (D-9)
            Assert.Equal(("IMPORT", "IMPORT_DO", "GENERAL"), (created.Booking.DirectionCode, created.Booking.BookingTypeCode, created.Booking.CargoClassCode));
            Assert.Equal(("OPEN", "NOT_STARTED"), (created.Booking.Status, created.Booking.Progress));
            Assert.Equal((2, 2), (created.QtyRequired, created.QtyAssigned));

            Assert.Equal([Gp20A, Gp20B], created.Containers.Select(c => c.ContainerNo).Order());
            Assert.All(created.Containers, c =>
            {
                Assert.True(c.InRegistry);
                Assert.Equal("PRE_ADVISED", c.Source);
                Assert.Equal(["FULL_IN", "FULL_OUT", "MTY_IN"], c.Steps.Select(s => s.MovementCode));
                Assert.All(c.Steps, s => Assert.Equal("PENDING", s.Status));
            });
            Assert.Equal("ML1234567", created.Containers.Single(c => c.ContainerNo == Gp20A).DeclaredSealNo);

            // Revenue's copy of the booking travels in the same transaction (PLAN_BILLING §4.2 step 0)
            var (messageType, payloadJson) = await TestDatabase.OutboxAsync(created.Booking.BookingId);
            Assert.Equal("BookingChanged", messageType);
            using var changed = JsonDocument.Parse(payloadJson);
            var root = changed.RootElement;
            Assert.Equal(("CREATED", "IMP CY/CY", "MAEU"), (root.GetProperty("reason").GetString(), root.GetProperty("orderTypeCode").GetString(), root.GetProperty("lineCode").GetString()));
            Assert.Equal(("20GP", 2), (root.GetProperty("requirements")[0].GetProperty("equipmentTypeCode").GetString(), root.GetProperty("requirements")[0].GetProperty("qty").GetInt32()));
            Assert.Equal(2, root.GetProperty("containers").GetArrayLength());
            Assert.All(root.GetProperty("containers").EnumerateArray(), c =>
                Assert.Equal(["FULL_IN", "FULL_OUT", "MTY_IN"], c.GetProperty("steps").EnumerateArray().Select(x => x.GetProperty("movementCode").GetString())));
        }
        finally { await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    [Fact]
    public async Task An_export_booking_needs_a_live_vessel_call_that_carries_its_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            var calls = (await client.GetFromJsonAsync<PagedResult<VesselCallSummaryResponse>>("/api/tos/vessel-calls?pageSize=200", ct))!.Items;
            var open = calls.Single(c => c.CallRef == "EASTPIONEER-069S");      // COSU, EGLV, ONEY
            var cancelled = calls.Single(c => c.CallRef == "SIAMSTAR-2642N");

            object Export(Guid? callId, string line = "ONEY") => new
            {
                branchId = SctLcb01, orderTypeCode = "EXP CY/CY", lineCode = line, customerCode = "CUS-BKF", carrierRef,
                vesselCallId = callId, podPortCode = "SGSIN",
                requirements = new object[] { new { equipmentTypeCode = "40HC", qty = 3 } },
            };

            // EXP CY/CY's FULL_IN requires the vessel/voyage (MDM gate rule).
            Assert.Contains("FULL_IN", await ExpectAsync(await client.PostAsJsonAsync(Bookings, Export(null), ct), HttpStatusCode.BadRequest, ct));
            Assert.Contains("is cancelled", await ExpectAsync(await client.PostAsJsonAsync(Bookings, Export(cancelled.VesselCallId, "RCLU"), ct), HttpStatusCode.BadRequest, ct));
            Assert.Contains("MAEU is not on", await ExpectAsync(await client.PostAsJsonAsync(Bookings, Export(open.VesselCallId, "MAEU"), ct), HttpStatusCode.BadRequest, ct));

            var booked = await CreateAsync(client, Export(open.VesselCallId), ct);
            Assert.Equal(("EASTPIONEER-069S", "069S", "EXPORT"), (booked.Booking.CallRef, booked.Booking.VoyageOut, booked.Booking.DirectionCode));
            Assert.Equal("SGSIN", booked.Booking.PodPortCode);
            Assert.NotNull(booked.Booking.Etd);   // read through the call, never copied (D-2)
        }
        finally { await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    [Fact]
    public async Task Parties_must_play_the_role_they_are_named_for_and_a_reefer_needs_its_set_point()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            var response = await client.PostAsJsonAsync(Bookings, new
            {
                branchId = TestDatabase.SssLcb01,             // another tenant's depot is unknown here
                orderTypeCode = "NO SUCH TYPE", lineCode = "CUS-TAE", customerCode = "MAEU", carrierRef,
                requirements = new object[]
                {
                    new { equipmentTypeCode = "40RH", qty = 1 },                          // reefer without set point
                    new { equipmentTypeCode = "20GP", qty = 1, declaredGrossWeightKg = 0 }, // V-15: zero is not a weight
                    new { equipmentTypeCode = "NOPE", qty = 1 },
                },
            }, ct);
            var body = await ExpectAsync(response, HttpStatusCode.BadRequest, ct);
            foreach (var expected in new[]
                     {
                         "\"branchId\"", "\"orderTypeCode\"", "CUS-TAE is not a shipping line", "MAEU is not a customer",
                         "requirements[0].reeferSetTempC", "requirements[1].declaredGrossWeightKg", "requirements[2].equipmentTypeCode",
                     })
                Assert.Contains(expected, body);
        }
        finally { await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    // ── boxes ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_box_is_checked_against_the_registry_the_check_digit_and_every_other_booking()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            var booking = await CreateAsync(client, ImportDo(carrierRef, requirements:
                [new { equipmentTypeCode = "20GP", qty = 1 }, new { equipmentTypeCode = "40HC", qty = 1 }]), ct);
            var id = booking.Booking.BookingId;

            // The registry says APZU4230891 is a 40GP; there is no 40GP line.
            Assert.Contains("the line asks for", await ExpectAsync(await AssignAsync(client, id, ct, new { containerNo = Gp40, lineNo = 1 }), HttpStatusCode.BadRequest, ct));

            // ISO 6346: the eleventh character is wrong. SCT enforces the check digit.
            var misread = Gp20A[..10] + ((Gp20A[10] - '0' + 1) % 10);
            Assert.Contains("check digit", await ExpectAsync(await AssignAsync(client, id, ct, new { containerNo = misread, lineNo = 1 }), HttpStatusCode.BadRequest, ct));

            // A box is active on one booking at a time.
            Assert.Contains("BK-SCT-LCB01-2609-00002", await ExpectAsync(await AssignAsync(client, id, ct, new { containerNo = BusyBox, lineNo = 1 }), HttpStatusCode.Conflict, ct));

            // A box the registry does not know is allowed at SCT (gate.allow_unknown_container),
            // but with two lines it must say which one.
            var unknown = UnknownBox();
            Assert.Contains("\"containers[0].lineNo\"", await ExpectAsync(await AssignAsync(client, id, ct, new { containerNo = unknown }), HttpStatusCode.BadRequest, ct));
            var afterUnknown = Read<BookingDetailResponse>(await ExpectAsync(await AssignAsync(client, id, ct, new { containerNo = unknown, lineNo = 1 }), HttpStatusCode.OK, ct));
            Assert.False(afterUnknown.Containers.Single(c => c.ContainerNo == unknown).InRegistry);

            // The registry type picks the line by itself; the 20GP line is now full.
            var afterHc = Read<BookingDetailResponse>(await ExpectAsync(await AssignAsync(client, id, ct, new { containerNo = Hc40 }), HttpStatusCode.OK, ct));
            Assert.Equal(2, afterHc.Containers.Single(c => c.ContainerNo == Hc40).LineNo);
            Assert.Contains("line is full", await ExpectAsync(await AssignAsync(client, id, ct, new { containerNo = Gp20A }), HttpStatusCode.BadRequest, ct));
        }
        finally { await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    [Fact]
    public async Task Unassigning_frees_the_place_and_cancels_the_steps_but_not_after_the_gate()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            var booking = await CreateAsync(client, ImportDo(carrierRef, containers: [new { containerNo = Gp20A }, new { containerNo = Gp20B }]), ct);
            var id = booking.Booking.BookingId;
            var first = booking.Containers.Single(c => c.ContainerNo == Gp20A);
            var second = booking.Containers.Single(c => c.ContainerNo == Gp20B);

            var off = Read<BookingDetailResponse>(await ExpectAsync(
                await client.DeleteAsync($"{Bookings}/{id}/containers/{first.BookingContainerId}", ct), HttpStatusCode.OK, ct));
            var ended = off.Containers.Single(c => c.BookingContainerId == first.BookingContainerId);
            Assert.Equal("UNASSIGNED", ended.EndReason);
            Assert.All(ended.Steps, s => Assert.Equal("CANCELLED", s.Status));
            Assert.Equal(1, off.QtyAssigned);

            // Its place is free again — the same box can come back.
            await ExpectAsync(await AssignAsync(client, id, ct, new { containerNo = Gp20A }), HttpStatusCode.OK, ct);

            // Once a box has passed the gate it stays.
            await TestDatabase.MarkFirstStepDoneAsync(second.BookingContainerId);
            var refused = await client.DeleteAsync($"{Bookings}/{id}/containers/{second.BookingContainerId}", ct);
            Assert.Contains("already passed the gate", await ExpectAsync(refused, HttpStatusCode.Conflict, ct));

            var progress = (await client.GetFromJsonAsync<BookingDetailResponse>($"{Bookings}/{id}", ct))!;
            Assert.Equal("IN_PROGRESS", progress.Booking.Progress);
        }
        finally { await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    [Fact]
    public async Task Requirement_lines_change_by_number_but_never_under_boxes_already_on_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            var booking = await CreateAsync(client, ImportDo(carrierRef, containers: [new { containerNo = Gp20A }, new { containerNo = Gp20B }]), ct);
            var id = booking.Booking.BookingId;
            Task<HttpResponseMessage> Put(string rowVersion, params object[] requirements) =>
                client.PutAsJsonAsync($"{Bookings}/{id}/requirements", new { rowVersion, requirements }, ct);

            var rv = booking.Booking.RowVersion;
            Assert.Contains("unassign some", await ExpectAsync(await Put(rv, new { lineNo = 1, equipmentTypeCode = "20GP", qty = 1 }), HttpStatusCode.BadRequest, ct));
            Assert.Contains("type cannot change", await ExpectAsync(await Put(rv, new { lineNo = 1, equipmentTypeCode = "40GP", qty = 2 }), HttpStatusCode.BadRequest, ct));
            Assert.Contains("cannot be removed", await ExpectAsync(await Put(rv, new { lineNo = 2, equipmentTypeCode = "40GP", qty = 1 }), HttpStatusCode.BadRequest, ct));

            var grown = Read<BookingDetailResponse>(await ExpectAsync(await Put(rv,
                new { lineNo = 1, equipmentTypeCode = "20GP", qty = 3 },
                new { equipmentTypeCode = "40GP", qty = 1, remarks = "added after the D/O came in" }), HttpStatusCode.OK, ct));
            Assert.Equal([(1, "20GP", 3), (2, "40GP", 1)], grown.Requirements.Select(r => ((int)r.LineNo, r.EquipmentTypeCode, (int)r.Qty)));
            Assert.Equal(4, grown.QtyRequired);

            // The old rowVersion is spent: the lines are part of the booking.
            await ExpectAsync(await Put(rv, new { lineNo = 1, equipmentTypeCode = "20GP", qty = 4 }), HttpStatusCode.Conflict, ct);
        }
        finally { await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    // ── cancel / close ──────────────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_releases_the_boxes_and_a_booking_with_gate_history_must_be_closed_instead()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var (first, second) = (NewRef(), NewRef());
        try
        {
            // 1. Cancel an untouched booking: its box is released onto another booking at once.
            var a = await CreateAsync(client, ImportDo(first, containers: [new { containerNo = Gp20A }]), ct);
            var cancelled = Read<BookingDetailResponse>(await ExpectAsync(await client.PostAsJsonAsync($"{Bookings}/{a.Booking.BookingId}/cancel",
                new { reason = "Line re-routed the cargo to Bangkok", rowVersion = a.Booking.RowVersion }, ct), HttpStatusCode.OK, ct));
            Assert.Equal(("CANCELLED", "CANCELLED"), (cancelled.Booking.Status, cancelled.Booking.Progress));
            Assert.Equal("BOOKING_CANCELLED", cancelled.Containers.Single().EndReason);
            Assert.Contains("CANCELLED", await ExpectAsync(await AssignAsync(client, a.Booking.BookingId, ct, new { containerNo = Gp20B }), HttpStatusCode.Conflict, ct));

            // 2. A booking whose box has passed the gate cannot be cancelled — close it.
            var b = await CreateAsync(client, ImportDo(second, containers: [new { containerNo = Gp20A }, new { containerNo = Gp20B }]), ct);
            await TestDatabase.MarkFirstStepDoneAsync(b.Containers.Single(c => c.ContainerNo == Gp20A).BookingContainerId);

            var refusal = await client.PostAsJsonAsync($"{Bookings}/{b.Booking.BookingId}/cancel", new { reason = "Customer changed their mind", rowVersion = b.Booking.RowVersion }, ct);
            Assert.Contains("Close the booking instead", await ExpectAsync(refusal, HttpStatusCode.Conflict, ct));

            var closed = Read<BookingDetailResponse>(await ExpectAsync(await client.PostAsJsonAsync($"{Bookings}/{b.Booking.BookingId}/close",
                new { reason = "Second box never collected", rowVersion = b.Booking.RowVersion }, ct), HttpStatusCode.OK, ct));
            Assert.Equal("CLOSED", closed.Booking.Status);
            Assert.All(closed.Containers, c => Assert.Equal("BOOKING_CLOSED", c.EndReason));
            var worked = closed.Containers.Single(c => c.ContainerNo == Gp20A);
            Assert.Equal(["DONE", "CANCELLED", "CANCELLED"], worked.Steps.Select(s => s.Status));   // history kept, the rest stopped
        }
        finally
        {
            await TestDatabase.RemoveBookingsAsync(first);
            await TestDatabase.RemoveBookingsAsync(second);
        }
    }

    // ── two clerks at once (BOOKING_ENTRY_GAP_ANALYSIS G1) ──────────────────

    /// <summary>
    /// The last free place on a line, claimed by two clerks at the same instant: one
    /// gets it, the other is told the line is full. Never two boxes on a one-box line.
    /// </summary>
    [Fact]
    public async Task Two_clerks_filling_the_last_place_on_a_line_do_not_both_get_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var other = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            var booking = await CreateAsync(client, ImportDo(carrierRef, requirements: [new { equipmentTypeCode = "20GP", qty = 1 }]), ct);
            var id = booking.Booking.BookingId;

            var answers = await Task.WhenAll(
                AssignAsync(client, id, ct, new { containerNo = Gp20A }),
                AssignAsync(other, id, ct, new { containerNo = Gp20B }));

            Assert.Equal(1, answers.Count(a => a.StatusCode == HttpStatusCode.OK));
            var loser = answers.Single(a => a.StatusCode != HttpStatusCode.OK);
            Assert.Contains("line is full", await ExpectAsync(loser, HttpStatusCode.BadRequest, ct));

            var detail = (await client.GetFromJsonAsync<BookingDetailResponse>($"{Bookings}/{id}", ct))!;
            Assert.Single(detail.Containers, c => c.EndedAt is null);
        }
        finally { await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    /// <summary>A box assigned while the booking is being cancelled never stays active on the cancelled booking.</summary>
    [Fact]
    public async Task A_box_assigned_during_a_cancel_never_stays_on_the_cancelled_booking()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var other = await api.ClientForAsync(TosApiFactory.SctOwner);
        var refs = new List<string>();
        try
        {
            for (var round = 0; round < 3; round++)
            {
                var carrierRef = NewRef();
                refs.Add(carrierRef);
                var booking = await CreateAsync(client, ImportDo(carrierRef), ct);
                var id = booking.Booking.BookingId;

                await Task.WhenAll(
                    AssignAsync(other, id, ct, new { containerNo = Gp20A }),
                    client.PostAsJsonAsync($"{Bookings}/{id}/cancel", new { reason = "Customer withdrew", rowVersion = booking.Booking.RowVersion }, ct));

                var detail = (await client.GetFromJsonAsync<BookingDetailResponse>($"{Bookings}/{id}", ct))!;
                if (detail.Booking.Status == "CANCELLED")
                    Assert.DoesNotContain(detail.Containers, c => c.EndedAt is null);
                await TestDatabase.RemoveBookingsAsync(carrierRef);   // frees Gp20A for the next round
            }
        }
        finally
        {
            foreach (var carrierRef in refs) await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }
}
