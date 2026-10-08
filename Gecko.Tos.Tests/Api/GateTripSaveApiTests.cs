using System.Net;
using System.Net.Http.Json;
using Gecko.Data;
using Gecko.Revenue.Endpoints.Window;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// The gate's big Save (owner 2026-10-04, GATE_IN_BIG_SAVE.md §2), through the real host and its real
/// dispatchers, on FIXTURE data. One call for the truck: ONE receipt across its bookings (the gate charge
/// once), an EIR per box, everything the clerk prints in the answer; a retry gets the same answer; a box
/// the barrier refuses after payment stays paid and comes back NOT_GATED.
///
/// SCT's GATE TEST (gecko_master dev_09, rates gecko_revenue dev_02): FULL_IN owes nothing; FULL_OUT owes
/// GATETRIP (PER_TRIP, ฿100 any truck) and GATEFEE (฿150) in cash. The coupon rule is switched ON for
/// SCT-LCB01 for these tests, so a box truly cannot move unpaid.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class GateTripSaveApiTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private const string Window = "/api/revenue/window";
    private const string OrderType = "GATE TEST";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewRef() => $"ZZT-{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static string NewBox()
    {
        var ten = $"ZZTU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    private static async Task<BookingDetailResponse> BookAsync(HttpClient client, string carrierRef, string box, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync("/api/tos/bookings", new
        {
            branchId = SctLcb01, orderTypeCode = OrderType, lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
            validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
            containers = new object[] { new { containerNo = box } },
        }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"booking returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!;
    }

    private static async Task DropOffAsync(HttpClient client, string box, CancellationToken ct)
    {
        await EventuallyAsync(() => PreflightAsync(client, box, "IN", ct), v => v.Decision == "ALLOWED", $"{box} allowed in", ct);
        var response = await client.PostAsJsonAsync($"{Gate}/transactions", new
        {
            branchId = SctLcb01, containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT",
            truck = new { plate = "70-1111" },
            grossWeightKg = 22000m, tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 19800m,
            seals = new object[] { new { sealNo = $"ZZT-{box[^4..]}", sealType = "LINE", isIntact = true } },
        }, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"gate-in returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }

    private static async Task<GatePreflightResponse> PreflightAsync(HttpClient client, string box, string direction, CancellationToken ct) =>
        (await client.GetFromJsonAsync<GatePreflightResponse>($"{Gate}/preflight?branchId={SctLcb01}&containerNo={box}&direction={direction}", ct))!;

    /// <summary>The pick-up quote once Revenue has seen the gate-in (FULL_OUT is then next).</summary>
    private static Task<WindowBookingResponse> PickUpQuoteAsync(HttpClient client, string orderNo, CancellationToken ct) =>
        EventuallyAsync(async () =>
        {
            var response = await client.GetAsync($"{Window}/bookings?orderNo={Uri.EscapeDataString(orderNo)}", ct);
            return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<WindowBookingResponse>(ct) : null;
        }, q => q is not null && q.Boxes.Single().NextMovementCode == "FULL_OUT", $"{orderNo} quoted for FULL_OUT", ct)!;

    private static object PickUp(Guid bookingContainerId, string box, string? heightCode = null) => new
    {
        bookingContainerId,
        move = new
        {
            containerNo = box, direction = "OUT", tripType = "PICK_UP_CONT", heightCode,
            seals = new object[] { new { sealNo = $"ZZT-{box[^4..]}", sealType = "LINE", isIntact = true } },
        },
    };

    private static async Task<HttpResponseMessage> SaveAsync(HttpClient client, string key, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Gate}/trips") { Content = JsonContent.Create(body) };
        request.Headers.Add(Idempotency.Header, key);
        return await client.SendAsync(request, ct);
    }

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

    private static async Task<Guid> OpenDrawerAsync(HttpClient client, CancellationToken ct)
    {
        var opened = await client.PostAsJsonAsync($"{Window}/shifts", new { branchId = SctLcb01, openingFloat = 0m }, ct);
        Assert.True(opened.StatusCode == HttpStatusCode.Created, await opened.Content.ReadAsStringAsync(ct));
        return (await opened.Content.ReadFromJsonAsync<ShiftResponse>(ct))!.ShiftId;
    }

    private static async Task CleanAsync(string carrierRef, Guid? shiftId, params string[] boxes)
    {
        await TestDatabase.RemoveCashWindowAsync(carrierRef, shiftId);
        await TestDatabase.RemoveGateAsync(carrierRef);
        await TestDatabase.RemoveBoxReservationsAsync(boxes);
        await TestDatabase.RemoveHoldsAsync(boxes);
        await TestDatabase.RemoveBookingsAsync(carrierRef);
        await TestDatabase.RequireCouponAtAsync(SctLcb01, null);
    }

    [Fact]
    public async Task One_Save_takes_the_trucks_cash_on_one_receipt_and_gates_every_box_and_a_retry_gets_the_same_answer()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var (boxA, boxB) = (NewBox(), NewBox());
        Guid? shiftId = null;
        await TestDatabase.RequireCouponAtAsync(SctLcb01, true);
        try
        {
            // Two bookings, each box already in the yard: the truck comes to pick both up.
            var a = await BookAsync(client, carrierRef + "A", boxA, ct);
            var b = await BookAsync(client, carrierRef + "B", boxB, ct);
            await DropOffAsync(client, boxA, ct);
            await DropOffAsync(client, boxB, ct);
            shiftId = await OpenDrawerAsync(client, ct);

            // What the card showed: each booking's quote, the gate charge once for the truck.
            var qa = await PickUpQuoteAsync(client, a.Booking.OrderNo, ct);
            var qb = await PickUpQuoteAsync(client, b.Booking.OrderNo, ct);
            var trip = qb.Boxes.Single().Due.Single(l => l.BillingUnitCode == "PER_TRIP");
            var expected = qa.Total + qb.Total - trip.Total;

            var body = new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(),
                truck = new { plate = "70-7777", driverName = "Somchai P." },
                rows = new[]
                {
                    PickUp(a.Containers.Single().BookingContainerId, boxA, heightCode: "HIGH_CUBE"),
                    PickUp(b.Containers.Single().BookingContainerId, boxB),
                },
                payment = new { payments = new[] { new { channel = "CASH", amount = expected } }, expectedTotal = expected },
            };
            var key = Guid.NewGuid().ToString();
            var saved = await SaveAsync(client, key, body, ct);
            Assert.True(saved.StatusCode == HttpStatusCode.Created, $"save returned {(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync(ct)}");
            var answer = (await saved.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!;

            // One receipt for the truck, both bookings on it.
            Assert.NotNull(answer.Receipt);
            Assert.Equal((expected, expected), (answer.Receipt.Total, answer.Receipt.Nett));
            var receipt = (await client.GetFromJsonAsync<ReceiptResponse>($"{Window}/receipts/{answer.Receipt.ReceiptId}", ct))!;
            Assert.Equal(new[] { boxA, boxB }.Order(), receipt.Lines.Select(l => l.ContainerNo).Distinct().Order());
            Assert.Equal(1, receipt.Lines.Count(l => l.BillingUnitCode == "PER_TRIP"));
            // A7: no payer named, so the invoice is made out to the first box's customer (Vector GateIn.cs:2342).
            Assert.NotEqual("Walk-in customer", receipt.PayerName);

            // Gate in (owner 2026-10-06, Vector GateIn.cs:1455): the pick-ups are paid and PLANNED on the visit, no EIR yet.
            Assert.All(answer.Rows, r => Assert.Equal(("PLANNED", (string?)null), (r.Status, r.EirNo)));
            Assert.All(answer.Rows, r => Assert.NotNull(r.CouponRef));
            Assert.All(answer.Rows, r => Assert.NotNull(r.VisitPickupId));
            Assert.NotNull(answer.VisitNo);
            var inYard = (await client.GetFromJsonAsync<TruckVisitResponse>($"{Gate}/visits/{answer.TruckVisitId}", ct))!;
            Assert.Equal(2, inYard.Pickups!.Count(p => p.Status == "PLANNED"));
            // The Gate Out clerk finds the truck by the box it came for, or that box's order number.
            async Task<bool> FoundByAsync(string search) =>
                (await client.GetFromJsonAsync<PagedResult<TruckVisitResponse>>($"{Gate}/visits?branchId={SctLcb01}&search={Uri.EscapeDataString(search)}", ct))!
                .Items.Any(v => v.TruckVisitId == answer.TruckVisitId);
            Assert.True(await FoundByAsync(boxB));
            Assert.True(await FoundByAsync(b.Booking.OrderNo));

            // Gate out: the clerk picks the truck in front of him and releases both boxes, on the same visit.
            var outKey = Guid.NewGuid().ToString();
            object outBody = new { branchId = SctLcb01, draftId = Guid.NewGuid(), truckVisitId = answer.TruckVisitId, rows = body.rows };
            var released = await SaveAsync(client, outKey, outBody, ct);
            Assert.True(released.StatusCode == HttpStatusCode.Created, $"gate out returned {(int)released.StatusCode}: {await released.Content.ReadAsStringAsync(ct)}");
            var gone = (await released.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!;
            Assert.Equal(answer.TruckVisitId, gone.TruckVisitId);
            Assert.All(gone.Rows, r => Assert.True(r.Status == "GATED", $"{r.ContainerNo} {r.Status}: {r.Reason}"));
            Assert.Equal(2, gone.Rows.Select(r => r.EirNo).Distinct().Count());
            Assert.NotNull(gone.TruckLeftAt);   // nothing left to collect: the truck has left
            Assert.False(await FoundByAsync(boxA));   // released, not PLANNED: no longer a box this truck is here for
            var outAgain = (await (await SaveAsync(client, outKey, outBody, ct)).Content.ReadFromJsonAsync<TripSaveResponse>(ct))!;
            Assert.Equal(gone.Rows.Select(r => r.EirNo), outAgain.Rows.Select(r => r.EirNo));   // a retried gate out: the same EIRs

            // A10: the truck-in form and the coupon slips print.
            foreach (var pdfUrl in new[] { answer.TruckInPdfUrl!, answer.Receipt.CouponPdfUrl! })
            {
                var pdf = await client.GetAsync(pdfUrl, ct);
                Assert.True(pdf.StatusCode == HttpStatusCode.OK, $"{pdfUrl}: {(int)pdf.StatusCode} {await pdf.Content.ReadAsStringAsync(ct)}");
                Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
                Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync(ct))[..4]));
            }

            // A5: the clerk's height overrides the equipment type's.
            var heights = new List<string?>();
            foreach (var r in gone.Rows)
                heights.Add((await client.GetFromJsonAsync<GateTransactionResponse>($"{Gate}/transactions/{r.GateTransactionId}", ct))!.HeightCode);
            Assert.Equal(new[] { "HIGH_CUBE", "STANDARD" }, heights);

            // The answer was lost: the same Save again is the same receipt and the same EIRs, nothing new.
            var again = await SaveAsync(client, key, body, ct);
            Assert.Equal(HttpStatusCode.Created, again.StatusCode);
            var replay = (await again.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!;
            Assert.Equal(answer.Receipt.ReceiptNo, replay.Receipt!.ReceiptNo);
            Assert.Equal(answer.Rows.Select(r => r.VisitPickupId), replay.Rows.Select(r => r.VisitPickupId));

            // The same key with another body is a client bug.
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SaveAsync(client, key, body with { draftId = Guid.NewGuid() }, ct)).StatusCode);
        }
        finally { await CleanAsync(carrierRef, shiftId, boxA, boxB); }
    }

    /// <summary>A box with no paperwork: the Save raises its BLIND GATE IN order, waits for the window to know it, takes the cash and gates it in.</summary>
    [Fact]
    public async Task A_blind_box_is_ordered_paid_and_gated_in_one_Save()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        Guid? shiftId = null;
        await TestDatabase.RequireCouponAtAsync(SctLcb01, true);
        await TestDatabase.GateChargeOnBlindGateInAsync(add: true);
        try
        {
            shiftId = await OpenDrawerAsync(client, ct);
            var preview = (await client.GetFromJsonAsync<WindowBookingResponse>(
                $"{Window}/preview?branchId={SctLcb01}&orderTypeCode=BLIND%20GATE%20IN&lineCode=MAEU&customerCode=CUS-TAE&equipmentTypeCode=20GP", ct))!;
            Assert.True(preview.Total > 0);

            var saved = await SaveAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(),
                truck = new { plate = "70-9999" },
                rows = new[]
                {
                    new
                    {
                        blind = new { containerNo = box, lineCode = "MAEU", customerCode = "CUS-TAE", equipmentTypeCode = "20GP", carrierRef },
                        move = new { containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m },
                    },
                },
                payment = new { payments = new[] { new { channel = "CASH", amount = preview.Total } }, expectedTotal = preview.Total },
            }, ct);
            Assert.True(saved.StatusCode == HttpStatusCode.Created, $"save returned {(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync(ct)}");
            var answer = (await saved.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!;

            Assert.Equal(preview.Total, answer.Receipt!.Total);
            var row = Assert.Single(answer.Rows);
            Assert.True(row.Status == "GATED", $"{row.Status}: {row.Reason}");
            Assert.NotNull(row.EirNo);
            Assert.StartsWith("BK-", row.OrderNo);

            // A5: no height keyed, so the EIR keeps the equipment type's (20GP = STANDARD).
            var eir = (await client.GetFromJsonAsync<GateTransactionResponse>($"{Gate}/transactions/{row.GateTransactionId}", ct))!;
            Assert.Equal("STANDARD", eir.HeightCode);
        }
        finally
        {
            await TestDatabase.GateChargeOnBlindGateInAsync(add: false);
            await CleanAsync(carrierRef, shiftId, box);
        }
    }

    /// <summary>A1: a box the barrier would refuse is found BEFORE the money: the whole Save is refused, nothing charged.</summary>
    [Fact]
    public async Task A_box_the_barrier_would_refuse_stops_the_Save_before_any_money_is_taken()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        Guid? shiftId = null;
        await TestDatabase.RequireCouponAtAsync(SctLcb01, true);
        try
        {
            var booking = await BookAsync(client, carrierRef, box, ct);
            await DropOffAsync(client, box, ct);
            shiftId = await OpenDrawerAsync(client, ct);
            var quote = await PickUpQuoteAsync(client, booking.Booking.OrderNo, ct);
            var hold = await client.PostAsJsonAsync("/api/tos/holds", new { containerNo = box, holdCode = "CUSTOMS", reason = "Red line" }, ct);
            Assert.Equal(HttpStatusCode.Created, hold.StatusCode);

            var saved = await SaveAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(),
                truck = new { plate = "70-8888" },
                rows = new[] { PickUp(booking.Containers.Single().BookingContainerId, box) },
                payment = new { payments = new[] { new { channel = "CASH", amount = quote.Total } }, expectedTotal = quote.Total },
            }, ct);
            Assert.Equal(HttpStatusCode.Conflict, saved.StatusCode);
            var body = await saved.Content.ReadAsStringAsync(ct);
            Assert.Contains("HOLD", body);
            Assert.Contains("\"charged\":false", body);
            Assert.DoesNotContain(await TestDatabase.ChargesAsync(carrierRef), c => c.Status == "PAID");
        }
        finally { await CleanAsync(carrierRef, shiftId, box); }
    }

    /// <summary>A2: the money could not be taken (no drawer open), so the blind order the Save raised is cancelled: no orphan.</summary>
    [Fact]
    public async Task A_blind_order_is_undone_when_the_money_cannot_be_taken()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        await TestDatabase.GateChargeOnBlindGateInAsync(add: true);
        try
        {
            var saved = await SaveAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(),
                truck = new { plate = "70-6666" },
                rows = new[]
                {
                    new
                    {
                        blind = new { containerNo = box, lineCode = "MAEU", customerCode = "CUS-TAE", equipmentTypeCode = "20GP", carrierRef },
                        move = new { containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m },
                    },
                },
                payment = new { payments = new[] { new { channel = "CASH", amount = 107m } }, expectedTotal = 107m },
            }, ct);
            Assert.Equal(HttpStatusCode.Conflict, saved.StatusCode);   // no drawer open (or the price differs): nothing taken
            var preflight = await PreflightAsync(client, box, "IN", ct);
            Assert.Contains(preflight.Findings, f => f.Code == "NO_ASSIGNMENT");   // the blind order did not stay behind
        }
        finally
        {
            await TestDatabase.GateChargeOnBlindGateInAsync(add: false);
            await CleanAsync(carrierRef, null, box);
        }
    }

    /// <summary>A4: the damage the clerk ticked is saved with the move: the GATE_IN survey and its damage hold come out of the one Save.</summary>
    [Fact]
    public async Task Damage_is_surveyed_with_the_move_in_the_same_Save()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        object Row(params object[] damages) => new
        {
            blind = new { containerNo = box, lineCode = "MAEU", customerCode = "CUS-TAE", equipmentTypeCode = "20GP", carrierRef },
            move = new { containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m },
            damages,
        };
        try
        {
            // An invented code is refused before anything is raised or charged.
            var invented = await SaveAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-2079" }, rows = new[] { Row(new { damageCode = "SMASHED" }) },
            }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, invented.StatusCode);
            Assert.Contains("rows[0].damages[0].damageCode", await invented.Content.ReadAsStringAsync(ct));
            Assert.Contains((await PreflightAsync(client, box, "IN", ct)).Findings, f => f.Code == "NO_ASSIGNMENT");

            var saved = await SaveAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-2079" },
                rows = new[] { Row(new { damageCode = "HO", locationCode = "DRR" }, new { damageCode = "SC" }) },
            }, ct);
            Assert.True(saved.StatusCode == HttpStatusCode.Created, $"save returned {(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync(ct)}");
            var row = Assert.Single((await saved.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!.Rows);
            Assert.True(row.Status == "GATED", $"{row.Status}: {row.Reason}");
            Assert.NotNull(row.SurveyId);
            Assert.Contains("DAMAGE", row.HoldsApplied!);

            var survey = (await client.GetFromJsonAsync<SurveyResponse>($"{Gate}/surveys/{row.SurveyId}", ct))!;
            Assert.Equal(("GATE_IN", row.GateTransactionId), (survey.SurveyType, survey.GateTransactionId));
            Assert.Equal(new[] { "HO", "SC" }, survey.Damages.Select(d => d.DamageCode));
            Assert.Equal("DRR", survey.Damages[0].LocationCode);
            Assert.False(survey.IsServiceable);
        }
        finally { await CleanAsync(carrierRef, null, box); }
    }

    /// <summary>
    /// A11 (owner D3, Vector GateIn.cs:1123): an IMPORT FULL box arrives as another type than booked. Without the
    /// clerk's confirmation the Save is 409 TYPE_MISMATCH naming both; confirmed, the box moves to the booking's
    /// line of that type (a new line here) and gates in.
    /// </summary>
    [Fact]
    public async Task A_box_of_another_type_is_refused_until_the_clerk_confirms_then_the_booking_follows_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        var box = NewBox();
        try
        {
            var booking = await BookAsync(client, carrierRef, box, ct);   // one 20GP place
            await EventuallyAsync(() => PreflightAsync(client, box, "IN", ct), v => v.Decision == "ALLOWED", $"{box} allowed in", ct);
            object Body(bool accept) => new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-1123" },
                rows = new[]
                {
                    new
                    {
                        bookingContainerId = booking.Containers.Single().BookingContainerId,
                        equipmentTypeCode = "40GP", acceptTypeChange = accept,
                        move = new
                        {
                            containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT",
                            grossWeightKg = 22000m, tareWeightKg = 3700m, maxGrossWeightKg = 30480m, cargoWeightKg = 18300m,
                            seals = new object[] { new { sealNo = $"ZZT-{box[^4..]}", sealType = "LINE", isIntact = true } },
                        },
                    },
                },
            };

            var asked = await SaveAsync(client, Guid.NewGuid().ToString(), Body(accept: false), ct);
            Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
            var question = await asked.Content.ReadAsStringAsync(ct);
            Assert.Contains("TYPE_MISMATCH", question);
            Assert.Contains("20GP", question);
            Assert.Contains("40GP", question);

            var saved = await SaveAsync(client, Guid.NewGuid().ToString(), Body(accept: true), ct);
            Assert.True(saved.StatusCode == HttpStatusCode.Created, $"save returned {(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync(ct)}");
            var row = Assert.Single((await saved.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!.Rows);
            Assert.True(row.Status == "GATED", $"{row.Status}: {row.Reason}");

            var after = (await client.GetFromJsonAsync<BookingDetailResponse>($"/api/tos/bookings/{booking.Booking.BookingId}", ct))!;
            var line = Assert.Single(after.Requirements);                 // the 20GP place went with the box
            Assert.Equal(("40GP", (short)1), (line.EquipmentTypeCode, line.Qty));
            Assert.Equal(line.EquipmentRequirementId, after.Containers.Single().EquipmentRequirementId);
            var eir = (await client.GetFromJsonAsync<GateTransactionResponse>($"{Gate}/transactions/{row.GateTransactionId}", ct))!;
            Assert.Equal("40GP", eir.EquipmentTypeCode);
        }
        finally { await CleanAsync(carrierRef, null, box); }
    }

    /// <summary>A6: a box on no order comes in EMPTY; a laden one is refused before anything is raised (Vector GateIn.cs:1191).</summary>
    [Fact]
    public async Task A_blind_box_with_cargo_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var box = NewBox();
        var saved = await SaveAsync(client, Guid.NewGuid().ToString(), new
        {
            branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-1191" },
            rows = new[]
            {
                new
                {
                    blind = new { containerNo = box, lineCode = "MAEU", customerCode = "CUS-TAE", equipmentTypeCode = "20GP" },
                    move = new { containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 18000m },
                },
            },
        }, ct);
        Assert.Equal(HttpStatusCode.Conflict, saved.StatusCode);
        Assert.Contains("BLIND_FULL", await saved.Content.ReadAsStringAsync(ct));
        var preflight = await PreflightAsync(client, box, "IN", ct);
        Assert.Contains(preflight.Findings, f => f.Code == "NO_ASSIGNMENT");   // no order was raised
    }

    /// <summary>A6: 1×40' or 2×20' each way (Vector GateIn.cs:1366).</summary>
    [Fact]
    public async Task A_truck_carries_at_most_45_feet_each_way()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var (a, b) = (NewBox(), NewBox());
        object Blind(string box) => new
        {
            blind = new { containerNo = box, lineCode = "MAEU", customerCode = "CUS-TAE", equipmentTypeCode = "40GP" },
            move = new { containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT", tareWeightKg = 3700m, maxGrossWeightKg = 30480m },
        };
        var saved = await SaveAsync(client, Guid.NewGuid().ToString(), new
        {
            branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-4545" }, rows = new[] { Blind(a), Blind(b) },
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        Assert.Contains("1×40' or 2×20'", await saved.Content.ReadAsStringAsync(ct));
    }
}
