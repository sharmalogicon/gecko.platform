using System.Net;
using System.Net.Http.Json;
using Gecko.Data;
using Gecko.Revenue.Endpoints.Window;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Walk-in trucks (WALK_IN_ORDERS_PLAN §3, owner 2026-10-12): a customer's truck drops off or picks up with no paperwork.
/// The clerk answers Drop off / Pick up and Empty / Full; the gate Save raises ONE booking of the walk-in order type those
/// answers name, for the truck. On FIXTURE data (gecko_master dev_12: SCT's WALK MTY IN / OUT, WALK FULL IN / OUT), never
/// KORAKIT. No money here (the coupon rule is off for SCT-LCB01 by default): the payment rule has its own tests.
/// Every booking a test raises carries its tag in the remarks, which is how it is cleaned up.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class WalkInApiTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewTag() => $"ZZWALK-{Guid.NewGuid():N}"[..20];

    private static string NewBox()
    {
        var ten = $"ZZWU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    private static object DropOff(string box, string fullEmpty, string tag, string customer = "CUS-TAE") => new
    {
        walkIn = new { fullEmpty, customerCode = customer, lineCode = "MAEU", equipmentTypeCode = "20GP", remarks = tag },
        move = fullEmpty == "FULL"
            ? (object)new
            {
                containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 12000m,
                grossWeightKg = 14200m, seals = new object[] { new { sealNo = $"ZZW-{box[^4..]}", sealType = "SHIPPER", isIntact = true } },
            }
            : new { containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m },
    };

    private static object PickUp(string box, string fullEmpty, string tag, string customer = "CUS-TAE") => new
    {
        walkIn = new { fullEmpty, customerCode = customer, remarks = tag },
        move = new { containerNo = box, direction = "OUT", tripType = "PICK_UP_CONT" },
    };

    private static async Task<HttpResponseMessage> SaveAsync(HttpClient client, string key, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Gate}/trips") { Content = JsonContent.Create(body) };
        request.Headers.Add(Idempotency.Header, key);
        return await client.SendAsync(request, ct);
    }

    private static async Task<TripSaveResponse> SavedAsync(HttpClient client, string key, object body, CancellationToken ct)
    {
        var saved = await SaveAsync(client, key, body, ct);
        Assert.True(saved.StatusCode == HttpStatusCode.Created, $"save returned {(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync(ct)}");
        return (await saved.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!;
    }

    private static async Task<BookingSummaryResponse> OrderAsync(HttpClient client, string orderNo, CancellationToken ct) =>
        Assert.Single((await client.GetFromJsonAsync<PagedResult<BookingSummaryResponse>>(
            $"/api/tos/bookings?branchId={SctLcb01}&search={Uri.EscapeDataString(orderNo)}", ct))!.Items, b => b.OrderNo == orderNo);

    private static async Task<List<WalkInBoxResponse>> WalkInBoxesAsync(HttpClient client, string fullEmpty, string box, CancellationToken ct) =>
        (await client.GetFromJsonAsync<PagedResult<WalkInBoxResponse>>(
            $"{Gate}/walk-in-boxes?branchId={SctLcb01}&fullEmpty={fullEmpty}&search={box}", ct))!.Items.ToList();

    [Fact]
    public async Task The_gate_offers_one_walk_in_order_type_per_drop_off_or_pick_up_of_a_full_or_an_empty_box()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);

        var offered = (await client.GetFromJsonAsync<List<WalkInOrderTypeResponse>>($"{Gate}/walk-in-order-types", ct))!;
        Assert.Contains(offered, o => o is { OrderTypeCode: "WALK MTY IN", Direction: "IN", FullEmpty: "EMPTY" });
        Assert.Contains(offered, o => o is { OrderTypeCode: "WALK MTY OUT", Direction: "OUT", FullEmpty: "EMPTY" });
        Assert.Contains(offered, o => o is { OrderTypeCode: "WALK FULL IN", Direction: "IN", FullEmpty: "FULL" });
        Assert.Contains(offered, o => o is { OrderTypeCode: "WALK FULL OUT", Direction: "OUT", FullEmpty: "FULL" });
        Assert.Equal(offered.Count, offered.Select(o => (o.Direction, o.FullEmpty)).Distinct().Count());

        // A walk-in order type's booking is raised by the gate, never by hand.
        var byHand = await client.PostAsJsonAsync("/api/tos/bookings", new
        {
            branchId = SctLcb01, orderTypeCode = "WALK MTY IN", lineCode = "MAEU", customerCode = "CUS-TAE",
            requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, byHand.StatusCode);
        Assert.Contains("orderTypeCode", await byHand.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// The whole life of two walk-in empties: dropped off together (ONE booking for the truck), offered to a walk-in pick-up,
    /// picked up by another truck (its own walk-in booking, planned at gate in, released at gate out).
    /// </summary>
    [Fact]
    public async Task Two_empties_dropped_off_by_one_truck_are_one_walk_in_booking_and_a_later_truck_picks_one_up()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var tag = NewTag();
        var (boxA, boxB) = (NewBox(), NewBox());
        try
        {
            var dropKey = Guid.NewGuid().ToString();
            object dropBody = new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-5151" },
                rows = new[] { DropOff(boxA, "EMPTY", tag), DropOff(boxB, "EMPTY", tag) },
            };
            var dropped = await SavedAsync(client, dropKey, dropBody, ct);
            Assert.All(dropped.Rows, r => Assert.True(r.Status == "GATED", $"{r.ContainerNo} {r.Status}: {r.Reason}"));
            var orderNo = Assert.Single(dropped.Rows.Select(r => r.OrderNo).Distinct());   // one booking for the truck
            var order = await OrderAsync(client, orderNo, ct);
            Assert.Equal(("WALK MTY IN", "CUS-TAE", "WALK_IN", 2), (order.OrderTypeCode, order.CustomerCode, order.Source, order.QtyRequired));
            Assert.Null(order.CarrierRef);

            // A retry is the same answer, not a second booking.
            var again = await SavedAsync(client, dropKey, dropBody, ct);
            Assert.Equal(dropped.Rows.Select(r => r.EirNo), again.Rows.Select(r => r.EirNo));

            // In the yard, on no open order: offered to a walk-in empty pick-up, not to a full one.
            Assert.Contains(await WalkInBoxesAsync(client, "EMPTY", boxA, ct), b => b.ContainerNo == boxA && b.LineCode == "MAEU");
            Assert.DoesNotContain(await WalkInBoxesAsync(client, "FULL", boxA, ct), b => b.ContainerNo == boxA);

            // Another truck comes for boxA: planned at gate in on its own walk-in booking, released at gate out.
            var pickBody = new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-5252" },
                rows = new[] { PickUp(boxA, "EMPTY", tag, customer: "CUS-TAE") },
            };
            var planned = await SavedAsync(client, Guid.NewGuid().ToString(), pickBody, ct);
            var plannedRow = Assert.Single(planned.Rows);
            Assert.True(plannedRow.Status == "PLANNED", $"{plannedRow.Status}: {plannedRow.Reason}");
            Assert.Equal("WALK MTY OUT", (await OrderAsync(client, plannedRow.OrderNo, ct)).OrderTypeCode);
            Assert.DoesNotContain(await WalkInBoxesAsync(client, "EMPTY", boxA, ct), b => b.ContainerNo == boxA);   // now on an order

            var released = await SavedAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truckVisitId = planned.TruckVisitId,
                rows = new[] { new { bookingContainerId = plannedRow.BookingContainerId, move = new { containerNo = boxA, direction = "OUT", tripType = "PICK_UP_CONT" } } },
            }, ct);
            Assert.True(Assert.Single(released.Rows).Status == "GATED", released.Rows.Single().Reason);
            Assert.Equal("COMPLETED", (await OrderAsync(client, plannedRow.OrderNo, ct)).Progress);
        }
        finally
        {
            await TestDatabase.RemoveWalkInAsync(tag);
            await TestDatabase.RemoveBoxReservationsAsync(boxA, boxB);
        }
    }

    [Fact]
    public async Task A_full_box_is_dropped_off_and_picked_up_by_walk_in_trucks_and_a_box_on_an_order_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var tag = NewTag();
        var box = NewBox();
        try
        {
            var dropped = await SavedAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-5353" }, rows = new[] { DropOff(box, "FULL", tag) },
            }, ct);
            Assert.True(dropped.Rows.Single().Status == "GATED", dropped.Rows.Single().Reason);
            Assert.Equal("WALK FULL IN", (await OrderAsync(client, dropped.Rows.Single().OrderNo, ct)).OrderTypeCode);

            // An EMPTY pick-up of a FULL box is refused before anything is recorded.
            var wrongLoad = await SaveAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-5454" }, rows = new[] { PickUp(box, "EMPTY", tag) },
            }, ct);
            Assert.Equal(HttpStatusCode.Conflict, wrongLoad.StatusCode);
            Assert.Contains("LOAD_MISMATCH", await wrongLoad.Content.ReadAsStringAsync(ct));

            var planned = await SavedAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-5454" }, rows = new[] { PickUp(box, "FULL", tag) },
            }, ct);
            Assert.Equal("PLANNED", planned.Rows.Single().Status);
            Assert.Equal("WALK FULL OUT", (await OrderAsync(client, planned.Rows.Single().OrderNo, ct)).OrderTypeCode);

            // The box is on that order now: a second walk-in truck cannot take it.
            var onOrder = await SaveAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-5555" }, rows = new[] { PickUp(box, "FULL", tag, customer: "CUS-TAE") },
            }, ct);
            Assert.Equal(HttpStatusCode.Conflict, onOrder.StatusCode);
        }
        finally
        {
            await TestDatabase.RemoveWalkInAsync(tag);
            await TestDatabase.RemoveBoxReservationsAsync(box);
        }
    }

    /// <summary>R5 (max boxes per truck) and R3 (release grades) on the walk-in order type; both off again afterwards.</summary>
    [Fact]
    public async Task The_walk_in_order_type_caps_the_boxes_per_truck_and_releases_only_its_grades()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var tag = NewTag();
        var (boxA, boxB) = (NewBox(), NewBox());
        try
        {
            await TestDatabase.SetSctOrderTypeRuleAsync("WALK MTY IN", "max_boxes_per_truck", (short)1);
            var capped = await SaveAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-5656" },
                rows = new[] { DropOff(boxA, "EMPTY", tag), DropOff(boxB, "EMPTY", tag) },
            }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, capped.StatusCode);
            Assert.Contains("at most 1", await capped.Content.ReadAsStringAsync(ct));
            await TestDatabase.SetSctOrderTypeRuleAsync("WALK MTY IN", "max_boxes_per_truck", null);

            // Dropped off with no grade keyed; WALK MTY OUT then releases grade A only.
            await SavedAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-5757" }, rows = new[] { DropOff(boxA, "EMPTY", tag) },
            }, ct);
            await TestDatabase.SetSctOrderTypeRuleAsync("WALK MTY OUT", "release_grade_codes", "A");
            Assert.DoesNotContain(await WalkInBoxesAsync(client, "EMPTY", boxA, ct), b => b.ContainerNo == boxA);
            var graded = await SaveAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-5858" }, rows = new[] { PickUp(boxA, "EMPTY", tag) },
            }, ct);
            Assert.Equal(HttpStatusCode.Conflict, graded.StatusCode);
            Assert.Contains("GRADE_NOT_ALLOWED", await graded.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            await TestDatabase.SetSctOrderTypeRuleAsync("WALK MTY IN", "max_boxes_per_truck", null);
            await TestDatabase.SetSctOrderTypeRuleAsync("WALK MTY OUT", "release_grade_codes", null);
            await TestDatabase.RemoveWalkInAsync(tag);
            await TestDatabase.RemoveBoxReservationsAsync(boxA, boxB);
        }
    }

    private static Task<WindowBookingResponse?> PreviewAsync(HttpClient client, string orderTypeCode, string customer, CancellationToken ct) =>
        client.GetFromJsonAsync<WindowBookingResponse>(
            $"/api/revenue/window/preview?branchId={SctLcb01}&orderTypeCode={Uri.EscapeDataString(orderTypeCode)}&lineCode=MAEU&customerCode={customer}&equipmentTypeCode=20GP", ct);

    /// <summary>
    /// R2 (owner 2026-10-12): the walk-in fixture's rule is CREDIT_FOR_CREDIT_CUSTOMERS. A credit customer owes nothing at the
    /// window (its gate charge is billed later); a cash customer pays it; CREDIT_WITHIN_LIMIT switches a credit customer at its
    /// limit to cash, never refuses it.
    /// </summary>
    [Fact]
    public async Task The_payment_rule_bills_a_credit_customer_and_takes_cash_from_the_others()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        try
        {
            var credit = (await PreviewAsync(client, "WALK MTY IN", "CUS-TAE", ct))!;
            Assert.Equal(0m, credit.Total);
            var box = Assert.Single(credit.Boxes);
            Assert.Contains(box.BilledLater ?? [], l => l is { ChargeCode: "GATETRIP", PaymentTermCode: "CREDIT" });
            Assert.Contains(box.Tried, t => t.Outcome == "CUSTOMER_ON_CREDIT");

            await TestDatabase.SetSctCustomerCreditAsync("CUS-CEM", "CASH", null, null);
            var cash = (await PreviewAsync(client, "WALK MTY IN", "CUS-CEM", ct))!;
            Assert.True(cash.Total > 0);
            Assert.Contains(cash.Boxes.Single().Due, l => l is { ChargeCode: "GATETRIP", PaymentTermCode: "CASH" });

            await TestDatabase.SetSctOrderTypeRuleAsync("WALK MTY IN", "payment_rule", "CREDIT_WITHIN_LIMIT");
            await TestDatabase.SetSctCustomerCreditAsync("CUS-ESL", "CREDIT", 0m, "THB");
            var atLimit = (await PreviewAsync(client, "WALK MTY IN", "CUS-ESL", ct))!;
            Assert.True(atLimit.Total > 0);
            Assert.Contains(atLimit.Boxes.Single().Tried, t => t.Note != null && t.Note.Contains("limit"));
        }
        finally
        {
            await TestDatabase.SetSctCustomerCreditAsync("CUS-CEM", "CREDIT", 1000000m, "THB");
            await TestDatabase.SetSctCustomerCreditAsync("CUS-ESL", "CREDIT", 2000000m, "THB");
            await TestDatabase.SetSctOrderTypeRuleAsync("WALK MTY IN", "payment_rule", "CREDIT_FOR_CREDIT_CUSTOMERS");
        }
    }

    /// <summary>A credit customer's walk-in truck passes a barrier that demands a coupon: nothing is paid, the coupon is automatic.</summary>
    [Fact]
    public async Task A_credit_customers_walk_in_truck_is_gated_with_no_cash_taken()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var tag = NewTag();
        var box = NewBox();
        await TestDatabase.RequireCouponAtAsync(SctLcb01, true);
        try
        {
            var saved = await SavedAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-6363" },
                rows = new[] { DropOff(box, "EMPTY", tag) },
                payment = new { payments = Array.Empty<object>(), expectedTotal = 0m },
            }, ct);
            var row = saved.Rows.Single();
            Assert.True(row.Status == "GATED", $"{row.Status}: {row.Reason}");
            Assert.True(saved.Receipt is null || saved.Receipt.Total == 0m);
        }
        finally
        {
            await TestDatabase.RequireCouponAtAsync(SctLcb01, null);
            await TestDatabase.RemoveWalkInAsync(tag);
            await TestDatabase.RemoveBoxReservationsAsync(box);
        }
    }

    private static object NoNumberDropOff(string tag) => new
    {
        walkIn = new { fullEmpty = "EMPTY", customerCode = "CUS-TAE", lineCode = "MAEU", equipmentTypeCode = "20GP", remarks = tag },
        move = new { direction = "IN", tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m },
    };

    /// <summary>
    /// Owner 2026-10-18 (WALK_IN_NO_CONTAINER_NO_FOR_API, gecko_tos 31): a walk-in drop-off with no readable number is gated in
    /// under a NOID number the gate issues — one each, kept on a retry, an ordinary box afterwards, renumbered to its real one.
    /// </summary>
    [Fact]
    public async Task A_walk_in_box_with_no_number_is_given_one_and_renumbered_later()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var tag = NewTag();
        var real = NewBox();
        var minted = new List<string>();
        try
        {
            var key = Guid.NewGuid().ToString();
            object body = new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-6464" },
                rows = new[] { NoNumberDropOff(tag), NoNumberDropOff(tag) },
            };
            var saved = await SavedAsync(client, key, body, ct);
            Assert.All(saved.Rows, r => Assert.True(r is { Status: "GATED", ContainerNoWasMinted: true }, $"{r.ContainerNo} {r.Status}: {r.Reason}"));
            minted.AddRange(saved.Rows.Select(r => r.ContainerNo));
            Assert.All(minted, n => Assert.True(ContainerNumber.IsPlaceholder(n), n));
            Assert.Equal(2, minted.Distinct().Count());                                   // one number each
            Assert.Single(saved.Rows.Select(r => r.OrderNo).Distinct());                  // one walk-in booking for the truck

            // A retry is the same answer: the same numbers, not new ones.
            var again = await SavedAsync(client, key, body, ct);
            Assert.Equal(minted, again.Rows.Select(r => r.ContainerNo));

            // An ordinary box from here: offered to a walk-in pick-up; not put in the registry.
            Assert.Contains(await WalkInBoxesAsync(client, "EMPTY", minted[0], ct), b => b.ContainerNo == minted[0]);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/master/containers/{minted[0]}", ct)).StatusCode);

            // The real number later: the stay and the EIR move with it, and the real box is registered.
            var renumbered = await client.PostAsJsonAsync($"/api/tos/containers/{minted[0]}/renumber",
                new { newContainerNo = real, reason = "Plate cleaned: number readable" }, ct);
            Assert.True(renumbered.StatusCode == HttpStatusCode.OK, await renumbered.Content.ReadAsStringAsync(ct));
            var moved = (await renumbered.Content.ReadFromJsonAsync<RenumberContainerResponse>(ct))!;
            Assert.True(moved.Moved["containerVisits"] == 1 && moved.Moved["gateTransactions"] == 1,
                string.Join(", ", moved.Moved.Select(m => $"{m.Key}={m.Value}")));
            Assert.Contains(await WalkInBoxesAsync(client, "EMPTY", real, ct), b => b.ContainerNo == real);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/master/containers/{real}", ct)).StatusCode);
        }
        finally
        {
            await TestDatabase.RemoveWalkInAsync(tag);
            await TestDatabase.RemoveBoxReservationsAsync([.. minted, real]);
            await TestDatabase.RemoveRegistryAsync(real);
        }
    }

    /// <summary>Only a walk-in drop-off may come with no number: a pick-up, a booked box and a blind row still name theirs.</summary>
    [Fact]
    public async Task Only_a_walk_in_drop_off_may_come_with_no_number()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var cases = new (string Name, object Row, string Field)[]
        {
            ("walk-in pick-up", new { walkIn = new { fullEmpty = "EMPTY", customerCode = "CUS-TAE" }, move = new { direction = "OUT", tripType = "PICK_UP_CONT" } }, "rows[0].move.containerNo"),
            ("booked drop-off", new { bookingContainerId = Guid.NewGuid(), move = new { direction = "IN", tripType = "DROP_OFF_CONT" } }, "rows[0].move.ContainerNo"),
            ("blind drop-off", new { blind = new { lineCode = "MAEU", customerCode = "CUS-TAE" }, move = new { direction = "IN", tripType = "DROP_OFF_CONT" } }, "rows[0].move.ContainerNo"),
        };
        foreach (var (name, row, field) in cases)
        {
            var response = await SaveAsync(client, Guid.NewGuid().ToString(), new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-6565" }, rows = new[] { row },
            }, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{name}: {(int)response.StatusCode} {text}");
            Assert.Contains(field, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_walk_in_row_names_its_load_and_customer()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var box = NewBox();
        var response = await SaveAsync(client, Guid.NewGuid().ToString(), new
        {
            branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-5959" },
            rows = new[]
            {
                new { walkIn = new { fullEmpty = "HALF", customerCode = (string?)"CUS-TAE" }, move = new { containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT" } },
                new { walkIn = new { fullEmpty = "EMPTY", customerCode = (string?)null }, move = new { containerNo = NewBox(), direction = "IN", tripType = "DROP_OFF_CONT" } },
            },
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("rows[0].walkIn.fullEmpty", body);
        Assert.Contains("rows[1].walkIn.customerCode", body);
    }
}
