using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gecko.Data;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Gate;
using Gecko.Tos.Endpoints.Holds;
using Gecko.Tos.Endpoints.Vessels;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// The barrier (PLAN §5.2–§5.5, Phase 5) through the real host.
///
/// What is being proved is the sentence the whole phase rests on: a gate event
/// is ONE transaction. After a gate-in, the step is DONE, the yard has exactly
/// one open row for that box, the journal says why, the outbox carries the
/// message the customer will see — and after a void, none of it is true any more.
///
/// Every test books with a ZZG- carrier ref and removes its gate rows in finally.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class GateApiTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private const string Bookings = "/api/tos/bookings";
    private const string Prefix = "ZZG-";

    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    /// <summary>Free SCT registry boxes, not on any fixture booking.</summary>
    private const string BoxA = "AKLU6018567", BoxB = "AKLU6019856", BoxC = "APZU4230891";

    private static string NewRef() => $"{Prefix}{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static object ImportDo(string carrierRef, params string[] boxes) => new
    {
        branchId = SctLcb01,
        orderTypeCode = "IMP CY/CY",      // FULL_IN > FULL_OUT > MTY_IN
        lineCode = "MAEU",
        customerCode = "CUS-TAE",
        carrierRef,
        validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
        requirements = new object[] { new { equipmentTypeCode = "20GP", qty = boxes.Length } },
        containers = boxes.Select(b => new { containerNo = b }).ToArray(),
    };

    private static async Task<BookingDetailResponse> BookAsync(HttpClient client, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(Bookings, body, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"booking returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!;
    }

    private static object GateIn(string containerNo, object? truck = null, Guid? truckVisitId = null, string? seal = null) => new
    {
        branchId = SctLcb01,
        containerNo,
        direction = "IN",
        tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 18000m, customsPermitNo = "ZZ-PERMIT-1",
        truckVisitId,
        truck = truckVisitId is null ? truck ?? new { plate = "70-1234", driverName = "Somchai P.", driverLicence = "1234567890123" } : null,
        grossWeightKg = 22150m,
        weightSource = "WEIGHBRIDGE",
        // FULL_IN checks the seal (MDM gate rule), so every gate-in here carries one.
        seals = new object[] { new { sealNo = seal ?? $"ZZ-{containerNo[^4..]}", sealType = "LINE", isIntact = true } },
        positionText = "A-03-2",
    };

    private static async Task<GatePreflightResponse> PreflightAsync(HttpClient client, string box, string direction, CancellationToken ct) =>
        (await client.GetFromJsonAsync<GatePreflightResponse>(
            $"{Gate}/preflight?branchId={SctLcb01}&containerNo={box}&direction={direction}", ct))!;

    private static async Task<GateTransactionResponse> RecordAsync(HttpClient client, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync($"{Gate}/transactions", body, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"gate returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<GateTransactionResponse>(ct))!;
    }

    // ── attachments (5.6) ───────────────────────────────────────────────────

    /// <summary>
    /// The damage photo is evidence: it is stored once, hashed, readable only
    /// inside its tenant, refused if it is not a picture or a PDF, and "deleting"
    /// it only hides it.
    /// </summary>
    [Fact]
    public async Task A_gate_photo_is_kept_hashed_and_seen_only_inside_its_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            await BookAsync(client, ImportDo(carrierRef, BoxA), ct);
            var eir = await RecordAsync(client, GateIn(BoxA, seal: "ZZ-PHOTO-01"), ct);

            // A real (1×1) PNG.
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");
            HttpContent Form(byte[] bytes, string contentType)
            {
                var form = new MultipartFormDataContent
                {
                    { new StringContent("GATE_TRANSACTION"), "ownerType" },
                    { new StringContent(eir.GateTransactionId.ToString()), "ownerId" },
                    { new StringContent("Dent, right side panel"), "caption" },
                };
                var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                form.Add(file, "file", "photo");
                return form;
            }

            var uploaded = await client.PostAsync($"{Gate}/attachments", Form(png, "image/png"), ct);
            Assert.True(uploaded.StatusCode == HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync(ct));
            var photo = (await uploaded.Content.ReadFromJsonAsync<AttachmentResponse>(ct))!;
            Assert.Equal((png.Length, "image/png", "Dent, right side panel"), ((int)photo.SizeBytes!, photo.ContentType, photo.Caption));
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)).ToLowerInvariant(), photo.Sha256);

            var listed = (await client.GetFromJsonAsync<List<AttachmentResponse>>(
                $"{Gate}/attachments?ownerType=GATE_TRANSACTION&ownerId={eir.GateTransactionId}", ct))!;
            Assert.Equal(photo.AttachmentId, Assert.Single(listed).AttachmentId);

            var content = await client.GetAsync($"{Gate}/attachments/{photo.AttachmentId}/content", ct);
            Assert.Equal("image/png", content.Content.Headers.ContentType!.MediaType);
            Assert.Equal(png, await content.Content.ReadAsByteArrayAsync(ct));

            // Not a picture, not a PDF: refused before a byte is stored.
            var exe = await client.PostAsync($"{Gate}/attachments", Form([0x4D, 0x5A, 0x90, 0x00], "application/x-msdownload"), ct);
            Assert.Equal(HttpStatusCode.BadRequest, exe.StatusCode);

            // Another tenant does not learn the photo exists.
            var other = await api.ClientForAsync(TosApiFactory.SssOwner);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Gate}/attachments/{photo.AttachmentId}/content", ct)).StatusCode);

            // Delete hides it; it is not destroyed.
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Gate}/attachments/{photo.AttachmentId}", ct)).StatusCode);
            Assert.Empty((await client.GetFromJsonAsync<List<AttachmentResponse>>(
                $"{Gate}/attachments?ownerType=GATE_TRANSACTION&ownerId={eir.GateTransactionId}", ct))!);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    // ── the read ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_barrier_answers_with_the_booking_the_step_and_the_rules_behind_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            var booking = await BookAsync(client, ImportDo(carrierRef, BoxA), ct);

            var view = await PreflightAsync(client, BoxA, "IN", ct);

            Assert.Equal("ALLOWED", view.Decision);
            // The only thing worth saying: nobody paid. SCT runs gate.require_coupon_for_cash
            // = false (gecko_master dev_08), so it is said, not enforced. Revenue may
            // already have issued the automatic coupon (FULL_IN owes no cash), in which
            // case there is nothing to say at all.
            Assert.All(view.Findings, f => Assert.Equal(("NO_COUPON", "INFO"), (f.Code, f.Severity)));
            Assert.Equal(booking.Booking.OrderNo, view.Booking!.OrderNo);

            // The step and its MDM gate rules — what the clerk's screen switches on.
            Assert.Equal(("FULL_IN", "IN", "FULL"), (view.NextStep!.MovementCode, view.NextStep.Direction, view.NextStep.FullEmpty));
            Assert.True(view.NextStep.CheckSealNo);
            Assert.True(view.IsCheckDigitValid);
            Assert.True(view.IsInRegistry);
            Assert.Null(view.InYard);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    [Fact]
    public async Task A_box_on_no_booking_is_refused_with_something_the_clerk_can_say_out_loud()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);

        var stranger = await PreflightAsync(client, "ZZZU1234564", "IN", ct);

        Assert.Equal("BLOCKED", stranger.Decision);
        var finding = Assert.Single(stranger.Findings);
        Assert.Equal("NO_ASSIGNMENT", finding.Code);
        Assert.Contains("walk-in", finding.Message, StringComparison.OrdinalIgnoreCase);

        var nonsense = await client.GetAsync($"{Gate}/preflight?branchId={SctLcb01}&containerNo=NOTABOX&direction=IN", ct);
        Assert.Equal(HttpStatusCode.OK, nonsense.StatusCode);
        Assert.Equal("NOT_A_CONTAINER_NUMBER",
            (await nonsense.Content.ReadFromJsonAsync<GatePreflightResponse>(ct))!.Findings.Single().Code);
    }

    // ── the write ───────────────────────────────────────────────────────────

    /// <summary>
    /// §5.5. Vector wrote these from five places and ended up with four answers to
    /// "what is in the yard" (V-4: 2,241 against 2,244). Here one POST moves all of
    /// it, and a void moves all of it back.
    /// </summary>
    [Fact]
    public async Task A_gate_event_writes_the_EIR_the_step_the_yard_the_journal_and_the_outbox_together()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            var booking = await BookAsync(client, ImportDo(carrierRef, BoxA), ct);
            var box = booking.Containers.Single();

            var eir = await RecordAsync(client, GateIn(BoxA, seal: "ZZ-SEAL-001"), ct);

            Assert.StartsWith("EIR-SCT-LCB01-", eir.EirNo, StringComparison.Ordinal);
            Assert.Equal(("IN", "FULL_IN", "FULL", (byte)1), (eir.Direction, eir.MovementCode, eir.FullEmpty, eir.PositionNo));
            Assert.Equal("ZZ-SEAL-001", Assert.Single(eir.Seals).SealNo);
            Assert.False(eir.BookingContainerCompleted);      // FULL_OUT and MTY_IN still to come
            Assert.NotNull(eir.ContainerVisitId);

            // ...and the yard list says so, with the gate-in read from the EIR itself.
            var stock = (await client.GetFromJsonAsync<PagedResult<YardContainerResponse>>(
                $"/api/tos/yard/containers?branchId={SctLcb01}&pageSize=200", ct))!;
            var inStock = Assert.Single(stock.Items, y => y.ContainerNo == BoxA);
            Assert.Equal((eir.EirNo, "FULL_IN", "FULL", 0), (inStock.GateInEirNo, inStock.GateInMovementCode, inStock.FullEmpty, inStock.DaysInYard));
            Assert.Equal("A-03-2", inStock.PositionText);

            // the step is DONE, and DONE means "a gate transaction says so" (D-4)
            var after = (await client.GetFromJsonAsync<BookingDetailResponse>($"{Bookings}/{booking.Booking.BookingId}", ct))!;
            var step = after.Containers.Single(c => c.ContainerNo == BoxA).Steps.OrderBy(s => s.SequenceNo).First();
            Assert.Equal("DONE", step.Status);

            // The yard now has ONE open row for the box, and a second gate-in is
            // refused — by the STEP, which is the more specific reason: the next
            // thing this box does is FULL_OUT.
            var inYard = await PreflightAsync(client, BoxA, "IN", ct);
            Assert.Equal("BLOCKED", inYard.Decision);
            Assert.Contains(inYard.Findings, f => f.Code == "STEP_OUT_OF_ORDER");
            Assert.NotNull(inYard.InYard);
            Assert.Equal("FULL", inYard.InYard.FullEmpty);

            await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
            {
                var visits = await db.ContainerVisits.CountAsync(v => v.ContainerNo == BoxA && v.GateOutTransactionId == null, ct);
                Assert.Equal(1, visits);

                var journal = await db.VisitEvents.SingleAsync(e => e.ReferenceId == eir.GateTransactionId, ct);
                Assert.Equal(("GATE_IN", eir.EirNo), (journal.EventType, journal.ToValue));
            }

            // the message the customer will get, queued in the same transaction
            var queued = await OutboxAsync(eir.GateTransactionId, ct);
            Assert.Equal("ContainerGatedIn", queued.MessageType);
            Assert.Equal(BoxA, queued.ContainerNo);

            // …carrying everything Revenue needs to price the move, because Revenue
            // may not come and read gecko_tos (ADR-007; PLAN_BILLING P-2)
            var priced = queued.Payload;
            Assert.Equal(booking.Booking.OrderTypeCode, priced.GetProperty("orderTypeCode").GetString());
            Assert.False(string.IsNullOrEmpty(priced.GetProperty("equipmentTypeCode").GetString()));
            Assert.Equal(booking.Booking.CargoClassCode, priced.GetProperty("cargoClassCode").GetString());
            Assert.Equal(JsonValueKind.False, priced.GetProperty("isDangerousGoods").ValueKind);
            Assert.NotEqual(Guid.Empty, priced.GetProperty("bookingContainerId").GetGuid());
            Assert.True(priced.TryGetProperty("agentPartyCode", out _));
            Assert.True(priced.TryGetProperty("grossWeightKg", out _));

            // ── the driver walks away with the printed EIR (5.6) ────────────
            var printed = await client.GetAsync($"{Gate}/transactions/{eir.GateTransactionId}/eir.pdf", ct);
            Assert.Equal(HttpStatusCode.OK, printed.StatusCode);
            Assert.Equal("application/pdf", printed.Content.Headers.ContentType!.MediaType);
            Assert.Equal($"{eir.EirNo}.pdf", printed.Content.Headers.ContentDisposition!.FileNameStar ?? printed.Content.Headers.ContentDisposition.FileName?.Trim('"'));
            var pdf = await printed.Content.ReadAsByteArrayAsync(ct);
            Assert.True(pdf.Length > 1000 && pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8), "not a PDF");

            // ── and the whole thing reverses ────────────────────────────────
            var voided = await client.PostAsJsonAsync($"{Gate}/transactions/{eir.GateTransactionId}/void",
                new { reason = "Wrong box — the driver had two on the trailer" }, ct);
            Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
            Assert.Equal("VOIDED", (await voided.Content.ReadFromJsonAsync<GateTransactionResponse>(ct))!.Status);

            // A voided EIR keeps its number and still prints (Q4) — marked VOID, never gone.
            var reprinted = await client.GetAsync($"{Gate}/transactions/{eir.GateTransactionId}/eir.pdf", ct);
            Assert.Equal(HttpStatusCode.OK, reprinted.StatusCode);
            Assert.NotEqual(pdf.Length, (await reprinted.Content.ReadAsByteArrayAsync(ct)).Length);

            // a void is news too: a charge raised from that move must be reversible
            var voidMessage = await OutboxAsync(eir.GateTransactionId, ct);
            Assert.Equal("GateTransactionVoided", voidMessage.MessageType);
            Assert.Equal(eir.EirNo, voidMessage.Payload.GetProperty("eirNo").GetString());
            Assert.Equal("Wrong box — the driver had two on the trailer", voidMessage.Payload.GetProperty("voidReason").GetString());

            var reopened = (await client.GetFromJsonAsync<BookingDetailResponse>($"{Bookings}/{booking.Booking.BookingId}", ct))!;
            Assert.Equal("PENDING", reopened.Containers.Single().Steps.OrderBy(s => s.SequenceNo).First().Status);

            var backOut = await PreflightAsync(client, BoxA, "IN", ct);
            Assert.Equal("ALLOWED", backOut.Decision);
            Assert.Null(backOut.InYard);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    [Fact]
    public async Task A_box_leaves_the_yard_on_its_next_step_and_the_stay_closes()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            await BookAsync(client, ImportDo(carrierRef, BoxA), ct);
            await RecordAsync(client, GateIn(BoxA, seal: "ZZ-SEAL-002"), ct);

            // IMP CY/CY: the next step is FULL_OUT, so the barrier expects OUT now.
            var next = await PreflightAsync(client, BoxA, "OUT", ct);
            Assert.Equal("ALLOWED", next.Decision);
            Assert.Equal("FULL_OUT", next.NextStep!.MovementCode);

            var outbound = await RecordAsync(client, new
            {
                branchId = SctLcb01,
                containerNo = BoxA,
                direction = "OUT",
                tripType = "PICK_UP_CONT",
                truck = new { plate = "70-5678", driverName = "Anucha S." },
                seals = new object[] { new { sealNo = "ZZ-SEAL-002", sealType = "LINE", isIntact = true } },
            }, ct);

            Assert.Equal(("OUT", "FULL_OUT"), (outbound.Direction, outbound.MovementCode));
            Assert.Equal("ContainerGatedOut", (await OutboxAsync(outbound.GateTransactionId, ct)).MessageType);

            // The stock list is assembled from the single sources, so the box leaves it
            // the moment the EIR says it did — no second copy to fall out of step (V-4).
            var stock = (await client.GetFromJsonAsync<PagedResult<YardContainerResponse>>(
                $"/api/tos/yard/containers?branchId={SctLcb01}&pageSize=200", ct))!;
            Assert.DoesNotContain(stock.Items, y => y.ContainerNo == BoxA);

            await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
            Assert.Equal(0, await db.ContainerVisits.CountAsync(v => v.ContainerNo == BoxA && v.GateOutTransactionId == null, ct));

            // Out of the yard, and the only step left (MTY_IN) comes back the other way.
            var again = await PreflightAsync(client, BoxA, "OUT", ct);
            Assert.Equal("BLOCKED", again.Decision);
            Assert.Null(again.InYard);
            Assert.Contains(again.Findings, f => f.Code == "STEP_OUT_OF_ORDER");
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    /// <summary>Q5: a hold that covers this move is a HARD refusal. There is no override at the barrier.</summary>
    [Fact]
    public async Task A_hold_that_covers_the_move_stops_it_and_nothing_at_the_barrier_can_lift_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            await BookAsync(client, ImportDo(carrierRef, BoxB), ct);

            var hold = await client.PostAsJsonAsync("/api/tos/holds",
                new { containerNo = BoxB, holdCode = "CUSTOMS", reason = "Red line: physical inspection" }, ct);
            Assert.Equal(HttpStatusCode.Created, hold.StatusCode);
            var applied = (await hold.Content.ReadFromJsonAsync<HoldResponse>(ct))!;

            var view = await PreflightAsync(client, BoxB, "IN", ct);
            Assert.Equal("BLOCKED", view.Decision);
            Assert.Contains(view.Findings, f => f.Code == "HOLD");
            Assert.True(Assert.Single(view.Holds, h => h.HoldCode == "CUSTOMS").BlocksThisMove);

            // Even the tenant owner cannot push it through: the only way out is a release.
            var refused = await client.PostAsJsonAsync($"{Gate}/transactions", GateIn(BoxB), ct);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("CUSTOMS", await refused.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

            var released = await client.PostAsJsonAsync($"/api/tos/holds/{applied.ContainerHoldId}/release",
                new { reason = "Customs release issued", releaseRef = "A0212026092200123", rowVersion = applied.RowVersion }, ct);
            Assert.Equal(HttpStatusCode.OK, released.StatusCode);

            Assert.Equal("ALLOWED", (await PreflightAsync(client, BoxB, "IN", ct)).Decision);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveHoldsAsync(BoxB);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    /// <summary>Drop one, take one — or twin 20s. Never three.</summary>
    [Fact]
    public async Task A_truck_carries_two_boxes_in_one_direction()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            await BookAsync(client, new
            {
                branchId = SctLcb01,
                orderTypeCode = "IMP CY/CY",
                lineCode = "MAEU",
                customerCode = "CUS-TAE",
                carrierRef,
                validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                requirements = new object[]
                {
                    new { equipmentTypeCode = "20GP", qty = 2 },
                    new { equipmentTypeCode = "40GP", qty = 1 },
                },
                containers = new object[] { new { containerNo = BoxA }, new { containerNo = BoxB }, new { containerNo = BoxC } },
            }, ct);

            var first = await RecordAsync(client, GateIn(BoxA), ct);
            var second = await RecordAsync(client, GateIn(BoxB, truckVisitId: first.TruckVisitId), ct);
            Assert.Equal(first.TruckVisitId, second.TruckVisitId);
            Assert.Equal((byte)2, second.PositionNo);

            var third = await client.PostAsJsonAsync($"{Gate}/transactions", GateIn(BoxC, truckVisitId: first.TruckVisitId), ct);
            Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
            Assert.Contains("A truck carries two", await third.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

            // The visit shows both boxes and stops its dwell clock on departure.
            var visit = (await client.GetFromJsonAsync<TruckVisitResponse>($"{Gate}/visits/{first.TruckVisitId}", ct))!;
            Assert.Equal(2, visit.Transactions.Count);
            Assert.Equal("ON_SITE", visit.Status);

            var departed = await client.PostAsJsonAsync($"{Gate}/visits/{first.TruckVisitId}/depart", new { }, ct);
            Assert.Equal(HttpStatusCode.OK, departed.StatusCode);
            var closed = (await departed.Content.ReadFromJsonAsync<TruckVisitResponse>(ct))!;
            Assert.Equal("DEPARTED", closed.Status);
            Assert.NotNull(closed.DwellMinutes);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    /// <summary>
    /// V-8, the whole reason the cut-off columns exist: in 2025 Vector let 686 boxes
    /// in after the yard cut-off on a bit, with nobody's name attached.
    /// </summary>
    [Fact]
    public async Task A_late_export_needs_the_permission_AND_a_reason_and_both_end_up_on_the_EIR()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var clerk = await api.ClientForAsync(TosApiFactory.SctGateLcb);
        var carrierRef = NewRef();
        try
        {
            // A call whose yard cut-off has already passed, and an export order type
            // whose FIRST step is FULL_IN — so the box meets the cut-off immediately.
            var call = (await client.GetFromJsonAsync<PagedResult<VesselCallSummaryResponse>>(
                $"/api/tos/vessel-calls?search=BLUEMERIDIAN-2634W", ct))!.Items.Single();

            await BookAsync(client, new
            {
                branchId = SctLcb01,
                orderTypeCode = "LADEN TO FACT 1",     // FULL_IN > FULL_OUT, EXPORT
                lineCode = "MAEU",
                customerCode = "CUS-TAE",
                carrierRef,
                vesselCallId = call.VesselCallId, polPortCode = "THLCH", podPortCode = "SGSIN",
                validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                requirements = new object[] { new { equipmentTypeCode = "40GP", qty = 1 } },
                containers = new object[] { new { containerNo = BoxC } },
            }, ct);

            var view = await PreflightAsync(client, BoxC, "IN", ct);
            Assert.Equal("NEEDS_OVERRIDE", view.Decision);
            var late = Assert.Single(view.Findings, f => f.Code == "LATE");
            Assert.Equal("OVERRIDE", late.Severity);
            Assert.True(view.Cutoff!.IsLate);
            Assert.Null(view.Cutoff.CoveredByExceptionId);

            // A clerk cannot decide this: they hold tos.gate.create but not the override.
            var byClerk = await clerk.PostAsJsonAsync($"{Gate}/transactions", GateIn(BoxC), ct);
            Assert.Equal(HttpStatusCode.Forbidden, byClerk.StatusCode);
            Assert.Contains("tos.cutoff.override", await byClerk.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

            // Nor can a supervisor wave it through without saying why.
            var noReason = await client.PostAsJsonAsync($"{Gate}/transactions", GateIn(BoxC), ct);
            Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
            Assert.Contains("686", await noReason.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

            var eir = await RecordAsync(client, new
            {
                branchId = SctLcb01, containerNo = BoxC, direction = "IN",
                tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 18000m, customsPermitNo = "ZZ-PERMIT-1",
                seals = new object[] { new { sealNo = "ZZ-LATE-1", sealType = "LINE", isIntact = true } },
                truck = new { plate = "70-9012" },
                lateOverrideReason = "Line agreed by phone; vessel still alongside",
            }, ct);

            // What it was judged against is ON the EIR — whatever the schedule does later.
            Assert.True(eir.IsLate);
            Assert.Equal("YARD_DRY", eir.CutoffKindApplied);
            Assert.NotNull(eir.CutoffAtApplied);
            Assert.Equal("Line agreed by phone; vessel still alongside", eir.LateOverrideReason);
            Assert.Null(eir.CutoffExceptionId);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    /// <summary>Vector "Allow Late Gate-In": set on the booking by someone who may override cut-offs, it covers the gate.</summary>
    [Fact]
    public async Task A_booking_that_allows_a_late_gate_in_lets_its_box_in_after_the_cut_off()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            var call = (await client.GetFromJsonAsync<PagedResult<VesselCallSummaryResponse>>(
                $"/api/tos/vessel-calls?search=BLUEMERIDIAN-2634W", ct))!.Items.Single();

            await BookAsync(client, new
            {
                branchId = SctLcb01,
                orderTypeCode = "LADEN TO FACT 1",
                lineCode = "MAEU",
                customerCode = "CUS-TAE",
                carrierRef,
                vesselCallId = call.VesselCallId, polPortCode = "THLCH", podPortCode = "SGSIN",
                validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                allowLateGateIn = true,
                requirements = new object[] { new { equipmentTypeCode = "40GP", qty = 1 } },
                containers = new object[] { new { containerNo = BoxC } },
            }, ct);

            var view = await PreflightAsync(client, BoxC, "IN", ct);
            Assert.NotEqual("NEEDS_OVERRIDE", view.Decision);
            var covered = Assert.Single(view.Findings, f => f.Code == "LATE_APPROVED");
            Assert.Contains("Allow Late Gate-In", covered.Message);
            Assert.DoesNotContain(view.Findings, f => f.Code == "LATE");
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    [Fact]
    public async Task Voiding_an_EIR_is_a_supervisors_act_not_a_clerks()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var clerk = await api.ClientForAsync(TosApiFactory.SctGateLcb);
        var carrierRef = NewRef();
        try
        {
            await BookAsync(client, ImportDo(carrierRef, BoxA), ct);
            var eir = await RecordAsync(client, GateIn(BoxA), ct);

            var refused = await clerk.PostAsJsonAsync($"{Gate}/transactions/{eir.GateTransactionId}/void",
                new { reason = "changed my mind" }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            // ...and they can still read the gate day they are working.
            Assert.Equal(HttpStatusCode.OK, (await clerk.GetAsync($"{Gate}/transactions?branchId={SctLcb01}", ct)).StatusCode);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    /// <summary>
    /// The EIR register: the list narrows by box, truck and booking; the one EIR
    /// carries what the detail page shows; its survey is found by the EIR; and a
    /// void that sends a rowVersion is refused when it is stale, then kept on record.
    /// </summary>
    [Fact]
    public async Task The_EIR_register_filters_shows_the_detail_and_voids_with_a_row_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            var booking = await BookAsync(client, ImportDo(carrierRef, BoxA), ct);
            var eir = await RecordAsync(client, GateIn(BoxA, truck: new { plate = "ZZ-7788" }), ct);

            async Task<PagedResult<GateTransactionSummaryResponse>> ListAsync(string query) =>
                (await client.GetFromJsonAsync<PagedResult<GateTransactionSummaryResponse>>($"{Gate}/transactions?{query}", ct))!;

            Assert.Contains((await ListAsync($"containerNo={BoxA}")).Items, t => t.GateTransactionId == eir.GateTransactionId);
            Assert.Contains((await ListAsync("truck=ZZ-778")).Items, t => t.GateTransactionId == eir.GateTransactionId);
            Assert.Single((await ListAsync($"bookingId={booking.Booking.BookingId}")).Items);
            Assert.DoesNotContain((await ListAsync($"containerNo={BoxA}&truck=NO-SUCH-PLATE")).Items, t => t.GateTransactionId == eir.GateTransactionId);

            var one = (await client.GetFromJsonAsync<GateTransactionResponse>($"{Gate}/transactions/{eir.GateTransactionId}", ct))!;
            Assert.Equal("A-03-2", one.PositionText);
            Assert.Null(one.VoidedAt);

            var survey = await client.PostAsJsonAsync($"{Gate}/surveys", new
            {
                containerNo = BoxA, surveyType = "GATE_IN", gateTransactionId = eir.GateTransactionId, surveyorName = "Gate",
            }, ct);
            Assert.Equal(HttpStatusCode.Created, survey.StatusCode);
            var surveys = (await client.GetFromJsonAsync<PagedResult<SurveyResponse>>(
                $"{Gate}/surveys?gateTransactionId={eir.GateTransactionId}", ct))!;
            Assert.Equal(eir.GateTransactionId, Assert.Single(surveys.Items).GateTransactionId);

            var garbled = await client.PostAsJsonAsync($"{Gate}/transactions/{eir.GateTransactionId}/void",
                new { reason = "wrong box keyed", rowVersion = "not base64!" }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, garbled.StatusCode);
            Assert.Contains("rowVersion", await garbled.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

            var stale = await client.PostAsJsonAsync($"{Gate}/transactions/{eir.GateTransactionId}/void",
                new { reason = "wrong box keyed", rowVersion = "AAAAAAAAAAE=" }, ct);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

            var voided = await client.PostAsJsonAsync($"{Gate}/transactions/{eir.GateTransactionId}/void",
                new { reason = "wrong box keyed", rowVersion = one.RowVersion }, ct);
            Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
            var after = (await voided.Content.ReadFromJsonAsync<GateTransactionResponse>(ct))!;
            Assert.Equal("VOIDED", after.Status);
            Assert.Equal("wrong box keyed", after.VoidReason);
            Assert.NotNull(after.VoidedAt);
            Assert.Contains((await ListAsync($"containerNo={BoxA}&status=VOIDED")).Items, t => t.GateTransactionId == eir.GateTransactionId);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveHoldsAsync(BoxA);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    /// <summary>
    /// V-16: Vector's damage table has ONE row in 2.7 years. Here a survey is a
    /// record, the CEDEX code decides whether the box is serviceable, and the depot's
    /// configured hold arrives by itself — which is what stops a holed box leaving.
    /// </summary>
    // ── Vector Gate In parity (gate-in-vector-parity.md §5) ─────────────────

    private static async Task ExpectFieldAsync(HttpResponseMessage response, string field, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400 on {field}, got {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"expected an error on '{field}': {body}");
    }

    /// <summary>A drop-off as the Vector form sends it: trip type, truck category and the capture fields.</summary>
    private static object DropOff(string containerNo, string tripType = "DROP_OFF_CONT", string direction = "IN",
        string truckCategory = "18_WHEEL", decimal? maxGrossWeightKg = 30480m, decimal? cargoWeightKg = 18000m,
        decimal? tareWeightKg = 2200m, bool withSeals = true) => new
    {
        branchId = SctLcb01,
        containerNo,
        direction,
        truck = new { plate = "70-9012", driverName = "Prasert K.", truckCategoryCode = truckCategory, haulierCode = "HAU-LCH" },
        grossWeightKg = 22150m,
        tareWeightKg,
        seals = withSeals ? new object[]
        {
            new { sealNo = $"ZZA-{containerNo[^4..]}", sealType = "AGENT", isIntact = true },
            new { sealNo = $"ZZC-{containerNo[^4..]}", sealType = "CUSTOMER", isIntact = true },
        } : [],
        tripType,
        materialCode = "STL",
        maxGrossWeightKg,
        cargoWeightKg,
        ventSetting = "25",
        humidityPct = 60.5m,
        gensetNo = "GS-001",
        clipOnNo = "CO-001",
        customsPermitNo = "A0011234567",
        paperlessCode = "PL-0001",
        nextLocationCode = "LCB",
    };

    [Fact]
    public async Task A_drop_off_stores_the_Vector_capture_fields_and_returns_them_on_the_EIR_and_the_visit()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            await BookAsync(client, ImportDo(carrierRef, BoxA), ct);

            var eir = await RecordAsync(client, DropOff(BoxA), ct);
            void Captured(GateTransactionResponse e)
            {
                Assert.Equal(("18_WHEEL", "DROP_OFF_CONT", "STL"), (e.TruckCategoryCode, e.TripType, e.MaterialCode));
                Assert.Equal((30480m, 18000m, 60.5m), (e.MaxGrossWeightKg!.Value, e.CargoWeightKg!.Value, e.HumidityPct!.Value));
                Assert.Equal(("25", "GS-001", "CO-001"), (e.VentSetting, e.GensetNo, e.ClipOnNo));
                Assert.Equal(("A0011234567", "PL-0001", "LCB"), (e.CustomsPermitNo, e.PaperlessCode, e.NextLocationCode));
                Assert.Equal(["AGENT", "CUSTOMER"], e.Seals.Select(s => s.SealType).Order());
            }
            Captured(eir);

            // S2: the gate event carries the truck facts Revenue prices the credit side with.
            var (messageType, payloadJson) = await TestDatabase.OutboxAsync(eir.GateTransactionId);
            Assert.Equal("ContainerGatedIn", messageType);
            using (var payload = JsonDocument.Parse(payloadJson))
            {
                var p = payload.RootElement;
                Assert.Equal(eir.TruckVisitId, p.GetProperty("truckVisitId").GetGuid());
                Assert.False(string.IsNullOrEmpty(p.GetProperty("visitNo").GetString()));
                Assert.Equal(0, p.GetProperty("visitBoxIndex").GetInt32());
                Assert.Equal(("18_WHEEL", "DROP_OFF_CONT", "HAU-LCH"), (p.GetProperty("truckCategoryCode").GetString(),
                    p.GetProperty("tripTypeCode").GetString(), p.GetProperty("visitHaulierPartyCode").GetString()));
            }
            Captured((await client.GetFromJsonAsync<GateTransactionResponse>($"{Gate}/transactions/{eir.GateTransactionId}", ct))!);
            Assert.Equal("18_WHEEL", (await client.GetFromJsonAsync<TruckVisitResponse>($"{Gate}/visits/{eir.TruckVisitId}", ct))!.TruckCategoryCode);

            // The trip type is required on every move (owner 2026-10-01): the old body is a 400 on it.
            object Outbound(string? tripType) => new
            {
                branchId = SctLcb01, containerNo = BoxA, direction = "OUT", tripType,
                truck = new { plate = "70-5678" },
                seals = new object[] { new { sealNo = "ZZ-SEAL-9", sealType = "LINE", isIntact = true } },
            };
            await ExpectFieldAsync(await client.PostAsJsonAsync($"{Gate}/transactions", Outbound(null), ct), "tripType", ct);

            // A pick-up needs nothing beyond the trip type, and simply has none of the drop-off fields.
            var outbound = await RecordAsync(client, Outbound("PICK_UP_CONT"), ct);
            Assert.Equal("PICK_UP_CONT", outbound.TripType);
            Assert.Null(outbound.TruckCategoryCode);
            Assert.Null(outbound.MaxGrossWeightKg);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    [Fact]
    public async Task An_unknown_truck_category_or_trip_type_and_a_trip_against_the_direction_are_400s_on_the_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            await BookAsync(client, ImportDo(carrierRef, BoxA), ct);
            var url = $"{Gate}/transactions";

            await ExpectFieldAsync(await client.PostAsJsonAsync(url, DropOff(BoxA, truckCategory: "3_WHEEL"), ct), "truck.truckCategoryCode", ct);
            await ExpectFieldAsync(await client.PostAsJsonAsync(url, DropOff(BoxA, tripType: "DROP-OFF CARGO"), ct), "tripType", ct);
            await ExpectFieldAsync(await client.PostAsJsonAsync(url, DropOff(BoxA, tripType: "PICK_UP_CONT"), ct), "tripType", ct);

            // Vector's matrix (GateIn.cs:146): a FULL drop-off needs max gross AND cargo weight.
            var missing = await client.PostAsJsonAsync(url, DropOff(BoxA, maxGrossWeightKg: null, cargoWeightKg: null), ct);
            await ExpectFieldAsync(missing, "maxGrossWeightKg", ct);
            await ExpectFieldAsync(await client.PostAsJsonAsync(url, DropOff(BoxA, cargoWeightKg: null), ct), "cargoWeightKg", ct);
            await ExpectFieldAsync(await client.PostAsJsonAsync(url, DropOff(BoxA, tareWeightKg: null), ct), "tareWeightKg", ct);
            await ExpectFieldAsync(await client.PostAsJsonAsync(url, DropOff(BoxA, withSeals: false), ct), "seals", ct);

            // None of the refusals left anything behind: the box gates in cleanly afterwards.
            Assert.Equal("ALLOWED", (await PreflightAsync(client, BoxA, "IN", ct)).Decision);
            await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
            Assert.Equal(0, await db.GateTransactions.CountAsync(g => g.ContainerNo == BoxA && g.Status == "COMPLETED"
                && db.Bookings.Any(b => b.BookingId == g.BookingId && b.CarrierRef == carrierRef), ct));
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }

    [Fact]
    public async Task A_survey_with_a_damage_that_makes_the_box_unserviceable_holds_it_by_itself()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            await BookAsync(client, ImportDo(carrierRef, BoxB), ct);
            var eir = await RecordAsync(client, GateIn(BoxB), ct);

            var response = await client.PostAsJsonAsync($"{Gate}/surveys", new
            {
                containerNo = BoxB,
                surveyType = "GATE_IN",
                gateTransactionId = eir.GateTransactionId,
                surveyorName = "Third-party surveyor",
                gradeCode = "C",
                isServiceable = true,                       // the surveyor's tick box...
                remarks = "Hole in the right door panel",
                damages = new object[]
                {
                    new { damageCode = "HO", locationCode = "DRR", componentCode = "DRG", lengthCm = 12.5, widthCm = 4.0, quantity = 1 },
                    new { damageCode = "SC", locationCode = "FEW", quantity = 3 },
                },
            }, ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var survey = (await response.Content.ReadFromJsonAsync<SurveyResponse>(ct))!;

            // ...loses to the CODE: HO makes a box unserviceable, SC does not.
            Assert.False(survey.IsServiceable);
            Assert.True(Assert.Single(survey.Damages, d => d.DamageCode == "HO").MakesUnserviceable);
            Assert.False(Assert.Single(survey.Damages, d => d.DamageCode == "SC").MakesUnserviceable);

            // The depot set DAMAGE to auto-apply on SURVEY_DAMAGED, so it did.
            Assert.Contains("DAMAGE", survey.HoldsApplied);

            // The hold is real and shown at the barrier. IMP CY/CY's FULL_OUT is a movement the
            // depot flagged "release damaged boxes" (MDM allow_damaged_release), and DAMAGE is the
            // damage hold — so, as in Vector (GateOut.cs:1099, 1272; owner 2026-10-01), it is
            // said and does not stop the customer's box leaving. Any other hold (CSC_EXP too), or
            // this one on an unflagged movement, still refuses (GateOutReleaseRuleTests).
            var leaving = await PreflightAsync(client, BoxB, "OUT", ct);
            Assert.DoesNotContain(leaving.Findings, f => f.Code == "HOLD");
            Assert.Equal("INFO", Assert.Single(leaving.Findings, f => f.Code == "HOLD_RELEASED_BY_MOVEMENT").Severity);
            Assert.True(leaving.NextStep!.AllowDamagedRelease);
            Assert.False(Assert.Single(leaving.Holds, h => h.HoldCode == "DAMAGE").BlocksThisMove);

            // A second survey of the same move is refused: that is a RE_SURVEY.
            var twice = await client.PostAsJsonAsync($"{Gate}/surveys", new
            {
                containerNo = BoxB, surveyType = "GATE_IN", gateTransactionId = eir.GateTransactionId, surveyorName = "Again",
            }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
            Assert.Contains("RE_SURVEY", await twice.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

            // An invented CEDEX code is refused, not stored as free text.
            var invented = await client.PostAsJsonAsync($"{Gate}/surveys", new
            {
                containerNo = BoxB, surveyType = "RE_SURVEY", surveyorName = "S",
                damages = new object[] { new { damageCode = "SMASHED" } },
            }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, invented.StatusCode);
        }
        finally
        {
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveHoldsAsync(BoxB);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    private sealed record QueuedMessage(string MessageType, string ContainerNo, JsonElement Payload);

    private static async Task<QueuedMessage> OutboxAsync(Guid gateTransactionId, CancellationToken ct)
    {
        await Task.Yield();
        var (messageType, payloadJson) = await TestDatabase.OutboxAsync(gateTransactionId);

        using var payload = JsonDocument.Parse(payloadJson);
        var root = payload.RootElement.Clone();
        return new QueuedMessage(messageType, root.GetProperty("containerNo").GetString()!, root);
    }
}
