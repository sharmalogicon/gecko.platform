using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Gecko.Data;
using Gecko.Identity.Contracts;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Contracts;
using Gecko.SharedKernel;
using Gecko.Tos.Application;
using Gecko.Tos.Domain;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Infrastructure.Persistence;
using Gecko.Tos.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Endpoints.Gate;

/// <summary>
/// The gate's big Save (owner 2026-10-04, GATE_IN_BIG_SAVE.md §2; Vector GateIn.cs btnSave_Click): one
/// call for the whole truck — blind orders for the boxes that came with no paperwork, ONE receipt for the
/// truck's cash, the coupons, and an EIR per box — answered with everything the clerk prints.
///
/// It spans two databases, so it is a sequence of keyed steps, each safe to repeat, remembered in
/// gate.trip_save under the Idempotency-Key: a retry of a finished Save gets the same answer; a retry of an
/// interrupted one resumes it. Every box is checked at the barrier before any money is taken (a refusal = 409,
/// nothing charged); blind orders raised by a Save whose money is then refused are cancelled again. A box the
/// barrier refuses after payment (a hold put on in between) stays paid, its coupon valid, NOT_GATED (owner 2026-10-04).
/// </summary>
internal static class TripEndpoints
{
    /// <summary>How long the Save waits for the other context to catch up (a new order known to Revenue, a coupon known to TOS).</summary>
    private static readonly TimeSpan CatchUp = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapTripEndpoints(this RouteGroupBuilder tos)
    {
        tos.MapPost("/gate/trips", SaveAsync).WithTags("TOS — gate")
            .RequireBranchPermission(TosPermissions.GateCreate)
            .Validate<TripSaveRequest>()
            .WithSummary("Save the whole truck: blind orders, one receipt, coupons and an EIR per box")
            .WithDescription("Idempotency-Key required: a retry returns the same receipt and EIRs, or resumes an interrupted Save. Every box is checked at the barrier first: a refusal is 409 with the findings per row, nothing charged. A box refused after payment stays paid and comes back NOT_GATED.");
        return tos;
    }

    private static async Task<Results<Created<TripSaveResponse>, ValidationProblem, ProblemHttpResult>> SaveAsync(
        TripSaveRequest request, HttpContext http, TosDbContext db, BarrierReader barrier, IMasterDataReferences master,
        BranchClock clock, ITenantContext caller, ICallerPermissions scope, TimeProvider time, IUserDirectory users,
        ITruckCashier cashier, OutboxWake wake, CancellationToken ct)
    {
        var branchId = request.BranchId!.Value;
        var draftId = request.DraftId!.Value;
        if (!scope.HasAt(TosPermissions.GateCreate, branchId))
            return TosScope.OutsideYourBranches("That gate is at a depot you do not cover.");

        var (key, badKey) = Idempotency.KeyOf(http.Request);
        if (badKey is not null) return TosSupport.Invalid(Idempotency.Header, badKey);
        if (key is null) return TosSupport.Invalid(Idempotency.Header, "A Save takes money and records moves: send an Idempotency-Key (a new UUID per Save, the same one on a retry).");
        if (key.Length > 90) return TosSupport.Invalid(Idempotency.Header, "At most 90 characters for a Save (each box's move is keyed from it).");
        var hash = Idempotency.HashOf(request);

        // ── the Save's memory: replay a finished one, resume an interrupted one ──
        var save = await db.TripSaves.SingleOrDefaultAsync(t => t.IdempotencyKey == key, ct);
        if (save is not null)
        {
            if (!Idempotency.SameRequest(save.IdempotencyHash, hash)) return Idempotency.DifferentRequest(key);
            if (save.Status == "DONE")
                return TypedResults.Created($"/api/tos/gate/trips/{save.TripSaveId}", JsonSerializer.Deserialize<TripSaveResponse>(save.ResultJson!, Json)!);
        }

        // ── the rows ────────────────────────────────────────────────────────
        var errors = new Dictionary<string, List<string>>();
        var rows = request.Rows!;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if ((row.BookingContainerId is null) == (row.Blind is null))
                errors.Add($"rows[{i}]", "Either a booked box (bookingContainerId) or a box on no order (blind), not both.");
            if (row.Move is null) { errors.Add($"rows[{i}].move", "The gate fields of this box."); continue; }
            var invalid = new List<ValidationResult>();
            var move = Move(row, request, null);
            var emptyPickup = request.TruckVisitId is null && row.Move.Direction == GateRules.Out && row.BookingContainerId is not null
                              && string.IsNullOrWhiteSpace(row.Move.ContainerNo);   // the yard chooses the box; it is keyed at gate out
            if (!Validator.TryValidateObject(move, new ValidationContext(move), invalid, validateAllProperties: true))
                foreach (var v in invalid.Where(v => !(emptyPickup && v.MemberNames.Contains(nameof(GateTransactionRequest.ContainerNo)))))
                    errors.Add($"rows[{i}].move.{string.Join(",", v.MemberNames)}", v.ErrorMessage ?? "Invalid.");
            if (row.Blind is { } blind && !string.Equals(ContainerNumber.Normalise(blind.ContainerNo ?? ""), ContainerNumber.Normalise(row.Move.ContainerNo ?? ""), StringComparison.Ordinal))
                errors.Add($"rows[{i}].blind.containerNo", "The blind order and the move must name the same box.");
        }
        if (rows.Count(r => r.Move?.Direction == GateRules.In) > 2 || rows.Count(r => r.Move?.Direction == GateRules.Out) > 2)
            errors.Add("rows", "A truck carries at most two boxes each way.");

        // ── the truck: the open visit it is on (gate out), else the one this screen opened, else a new one ──
        TruckVisit? joined = null;
        if (request.TruckVisitId is { } named)
        {
            joined = await db.TruckVisits.AsNoTracking().SingleOrDefaultAsync(v => v.TruckVisitId == named, ct);
            if (joined is null || joined.BranchId != branchId)
                errors.Add("truckVisitId", "Not a truck visit at this depot.");
            else if (joined.GateOutAt is not null)
                errors.Add("truckVisitId", $"Visit {joined.VisitNo} ({joined.TruckPlate}) has already left the depot. A truck coming back is a new visit: send truck instead.");
        }
        else
        {
            var opened = await db.TripSaves.AsNoTracking()
                .Where(t => t.BranchId == branchId && t.DraftId == draftId && t.TruckVisitId != null && t.Status == "DONE" && t.IdempotencyKey != key)
                .OrderByDescending(t => t.UpdatedAt).Select(t => t.TruckVisitId).FirstOrDefaultAsync(ct);
            if (opened is { } draftVisit)
                joined = await db.TruckVisits.AsNoTracking()
                    .SingleOrDefaultAsync(v => v.TruckVisitId == draftVisit && v.GateOutAt == null && v.BranchId == branchId, ct);
        }
        if (joined is null && request.Truck is null && !errors.ContainsKey("truckVisitId"))
            errors.Add("truck", "Name the truck, or the open visit it is on (truckVisitId).");

        // ── gate out (owner 2026-10-06; Vector GateOut.cs): the truck in front of the clerk releases what it came for ──
        var gateOut = request.TruckVisitId is not null;
        var releasedHere = await db.GateTransactions.AsNoTracking()   // a resumed gate-out: rows this key already released
            .Where(g => g.IdempotencyKey != null && g.IdempotencyKey.StartsWith(key + "#")).Select(g => g.GateTransactionId).ToListAsync(ct);
        var plans = joined is null ? [] : await db.VisitPickups
            .Where(p => p.TruckVisitId == joined.TruckVisitId
                        && (p.Status == "PLANNED" || (p.Status == "RELEASED" && p.GateTransactionId != null && releasedHere.Contains(p.GateTransactionId.Value))))
            .ToListAsync(ct);
        if (gateOut && joined is not null)
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Move?.Direction != GateRules.Out || rows[i].Blind is not null)
                    errors.Add($"rows[{i}]", "At gate out a row releases a box this truck came to collect (planned at gate in). A drop-off is recorded at gate in.");
                else if (!plans.Any(p => p.BookingContainerId == rows[i].BookingContainerId))
                    errors.Add($"rows[{i}].bookingContainerId", $"Not a pick-up planned for {joined.TruckPlate} ({joined.VisitNo}) at gate in.");
            }
        // Whose holds these are: at gate out the truck's own (its pick-ups are held for its visit), else this screen's.
        var holder = gateOut && joined is not null ? joined.TruckVisitId : draftId;
        var asHeld = request with { DraftId = holder };
        if (rows.Any(r => r.Damages is { Count: > 0 }))
        {
            var codes = await master.SurveyCodesAsync(ct);
            for (var i = 0; i < rows.Count; i++)
            {
                var damages = rows[i].Damages ?? [];
                if (damages.Count > 0 && rows[i].Move?.Direction != GateRules.In)
                    errors.Add($"rows[{i}].damages", "Damage is surveyed as the box comes in (a drop-off).");
                for (var j = 0; j < damages.Count; j++)
                {
                    if (!codes.DamageCodes.ContainsKey(damages[j].DamageCode.Clean() ?? ""))
                        errors.Add($"rows[{i}].damages[{j}].damageCode", $"'{damages[j].DamageCode}' is not a damage code. CEDEX codes come from master data.");
                    if (damages[j].LocationCode.Clean() is { } location && !codes.Locations.Contains(location))
                        errors.Add($"rows[{i}].damages[{j}].locationCode", $"'{damages[j].LocationCode}' is not a damage location.");
                    if (damages[j].ComponentCode.Clean() is { } component && !codes.Components.Contains(component))
                        errors.Add($"rows[{i}].damages[{j}].componentCode", $"'{damages[j].ComponentCode}' is not a component.");
                    if (damages[j].Quantity <= 0) errors.Add($"rows[{i}].damages[{j}].quantity", "At least one.");
                }
            }
        }
        if (errors.Count > 0) return TosSupport.Invalid(errors);

        if (save is null)
        {
            save = new TripSave
            {
                TenantId = caller.TenantId(), BranchId = branchId, DraftId = draftId, IdempotencyKey = key, IdempotencyHash = hash,
                Status = "IN_PROGRESS", CreatedBy = caller.UserId(), UpdatedBy = caller.UserId(),
            };
            db.TripSaves.Add(save);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException e) when (e.InnerException?.Message.Contains("uq_trip_save__idempotency_key") == true)
            {
                return TosSupport.Conflict($"A Save with {Idempotency.Header} {key} is already running.", "Repeat it in a moment.");
            }
        }

        // ── 0. what this call writes before the money is taken back if it stops (A2) ──
        var raisedNow = new List<Guid>();                                        // blind orders raised by THIS call
        var nominatedNow = new List<(Guid BookingContainerId, string ContainerNo)>();   // numbers it wrote onto empty places
        async Task UndoAsync(string why)
        {
            db.ChangeTracker.Clear();
            foreach (var (id, number) in nominatedNow) await GateNominations.UndoAsync(db, id, number, time, ct);
            foreach (var id in raisedNow) await BookingEndpoints.UndoBlindOrderAsync(db, id, why, caller, time, ct);
            db.ChangeTracker.Clear();
        }
        async Task<Results<Created<TripSaveResponse>, ValidationProblem, ProblemHttpResult>> StopAsync(
            Results<Created<TripSaveResponse>, ValidationProblem, ProblemHttpResult> answer)
        {
            await UndoAsync("Gate Save stopped before payment.");
            return answer;
        }

        // ── 1a. a box keyed on an EMPTY booking place: written onto it, or onto the next free place like it ──
        // (owner 2026-10-06; Vector Operation.usp_BookingContainerMovement). Under the booking lock, so two Saves
        // in the same instant never write one place twice; a place named by the booking is never overwritten.
        var now = time.GetUtcNow();
        var lineIds = rows.Select(r => r.BookingContainerId).ToArray();
        var switched = new Dictionary<int, GateFindingResponse>();
        var placeRefusals = new List<object>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].BookingContainerId is not { } wanted) continue;
            var number = ContainerNumber.Normalise(rows[i].Move!.ContainerNo);
            var place = await GateNominations.PlaceAsync(db, wanted, ct);
            if (place is null || place.BranchId != branchId)
                return await StopAsync(TosSupport.Invalid($"rows[{i}].bookingContainerId", "Not a booked box at this depot."));
            if (number.Length == 0) continue;   // an empty pick-up planned without its box
            if (gateOut && place.ContainerNo is { } planned && planned != number && place.Source == GateNominations.Source)
            {
                // The yard loaded another empty box than the one planned: the plan's box comes off, this one goes on.
                foreach (var h in await BoxReservations.LiveAsync(db, null, planned, now, ct)) BoxReservations.Release(h, BoxReservations.Removed, caller.UserId(), now);
                await db.SaveChangesAsync(ct);
                await GateNominations.UndoAsync(db, place.BookingContainerId, planned, time, ct);
                place = (await GateNominations.PlaceAsync(db, wanted, ct))!;
            }
            if (place.ContainerNo == number || (place.ContainerNo is not null && place.Source != GateNominations.Source)) continue;

            var blocks = (await GateNominations.CheckAsync(db, master, clock, place, number, holder, now, ct))
                .Where(f => f.Severity == GateSeverity.Block).ToList();
            var choice = blocks.Count > 0 ? null : await GateNominations.NominateAsync(db, master, wanted, number, holder, caller, time, ct);
            if (choice is { Place: { } chosen })
            {
                if (choice.Written) nominatedNow.Add((chosen.BookingContainerId, number));
                lineIds[i] = chosen.BookingContainerId;
                if (choice.Note is { } note) switched[i] = new GateFindingResponse("PLACE_SWITCHED", note, "INFO");
                continue;
            }
            if (choice is not null) blocks.Add(new GateFinding(choice.Code!, choice.Problem!, GateSeverity.Block));
            placeRefusals.Add(new { index = i, containerNo = number,
                findings = blocks.Select(f => new GateFindingResponse(f.Code, f.Message, f.Severity.ToString().ToUpperInvariant())).ToList() });
        }
        if (placeRefusals.Count > 0)
            return await StopAsync(TypedResults.Problem(title: "Some boxes cannot go on their booking.",
                detail: "Nothing was charged and nothing was recorded. Fix or remove these boxes, then Save again.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["rows"] = placeRefusals, ["charged"] = false }));

        // ── 1. no box another truck's clerk holds ───────────────────────────
        for (var i = 0; i < rows.Count; i++)
        {
            var number = ContainerNumber.Normalise(rows[i].Move!.ContainerNo);
            if (await BoxReservations.HeldByOtherAsync(db, lineIds[i], number.Length == 0 ? null : number, holder, now, ct) is { } held)
            {
                var name = (await users.DisplayNamesAsync([held.ReservedBy], ct)).GetValueOrDefault(held.ReservedBy);
                return await StopAsync(TosSupport.Conflict(BoxReservations.Finding(held, name).Message, $"rows[{i}]: remove it from this truck, or ask that clerk to."));
            }
        }

        // ── 1b. every box past the barrier BEFORE any money is taken (A1) ──
        var refusals = new List<object>();
        var doneBefore = await db.GateTransactions.AsNoTracking()   // an interrupted Save resumed: its moves are already made
            .Where(g => g.IdempotencyKey != null && g.IdempotencyKey.StartsWith(key + "#")).Select(g => g.IdempotencyKey!).ToListAsync(ct);
        for (var i = 0; i < rows.Count; i++)
        {
            if (doneBefore.Contains($"{key}#{i}") || string.IsNullOrWhiteSpace(rows[i].Move!.ContainerNo)) continue;
            var blocks = await GateEndpoints.PrecheckAsync(Move(rows[i], asHeld, null), rows[i].Blind is not null, barrier, master, time, ct);
            if (blocks.Count > 0)
                refusals.Add(new { index = i, containerNo = ContainerNumber.Normalise(rows[i].Move!.ContainerNo),
                    findings = blocks.Select(f => new GateFindingResponse(f.Code, f.Message, f.Severity.ToString().ToUpperInvariant())).ToList() });
        }
        if (refusals.Count > 0)
            return await StopAsync(TypedResults.Problem(title: "Some boxes cannot go through the gate.",
                detail: "Nothing was charged and nothing was recorded. Fix or remove these boxes, then Save again.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["rows"] = refusals, ["charged"] = false }));

        // ── 1c. one truck carries 1×40' or 2×20' each way (Vector GateIn.cs:1366) ──
        var aboard = joined is null ? [] : await db.GateTransactions.AsNoTracking()   // boxes the joined visit already moved
            .Where(g => g.TruckVisitId == joined.TruckVisitId && g.Status == "COMPLETED"
                        && !(g.IdempotencyKey != null && g.IdempotencyKey.StartsWith(key + "#")))
            .Select(g => new Aboard(g.Direction, g.EquipmentTypeCode)).ToListAsync(ct);
        if (await FeetAsync(db, master, rows, aboard, ct) is { } feet) return await StopAsync(feet);

        // ── 1d. the booked boxes, before any blind order is raised ──────────
        var lines = new (Guid BookingContainerId, Guid BookingId, string OrderNo, string ContainerNo)[rows.Count];
        var mismatches = new List<object>();
        var typeChanges = new Dictionary<Guid, string>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (lineIds[i] is not { } lineId) continue;
            var line = await (
                from x in db.BookingContainers.AsNoTracking()
                join b in db.Bookings on x.BookingId equals b.BookingId
                join q in db.EquipmentRequirements on x.EquipmentRequirementId equals q.EquipmentRequirementId
                where x.BookingContainerId == lineId
                select new { x.BookingContainerId, x.BookingId, b.OrderNo, b.BranchId, x.ContainerNo, b.BookingTypeCode, b.OrderTypeCode, q.EquipmentTypeCode })
                .SingleOrDefaultAsync(ct);
            if (line is null || line.BranchId != branchId)
                return await StopAsync(TosSupport.Invalid($"rows[{i}].bookingContainerId", "Not a booked box at this depot."));
            var number = ContainerNumber.Normalise(rows[i].Move!.ContainerNo);
            if (number.Length == 0)   // an empty pick-up planned without its box: the yard chooses it
            {
                lines[i] = (line.BookingContainerId, line.BookingId, line.OrderNo, line.ContainerNo ?? "");
                continue;
            }
            if (line.ContainerNo != number)
                return await StopAsync(TosSupport.Invalid($"rows[{i}].move.containerNo",
                    $"That place on {line.OrderNo} is for {line.ContainerNo}, not {number}. Pick an empty place, or check the number."));
            lines[i] = (line.BookingContainerId, line.BookingId, line.OrderNo, line.ContainerNo);

            // ── A11: the box is another type than booked (Vector GateIn.cs:1123) ──
            var keyed = rows[i].EquipmentTypeCode.Clean();
            if (keyed is null || string.Equals(keyed, line.EquipmentTypeCode, StringComparison.OrdinalIgnoreCase)) continue;
            var next = await db.MovementPlans.AsNoTracking().Where(p => p.BookingContainerId == lineId && p.Status == "PENDING")
                .OrderBy(p => p.SequenceNo).Select(p => p.OrderTypeMovementId).FirstOrDefaultAsync(ct);
            var step = (await master.OrderTypePlansAsync([line.OrderTypeCode], ct)).GetValueOrDefault(line.OrderTypeCode)?
                .Steps.FirstOrDefault(s => s.OrderTypeMovementId == next);
            var importFullIn = line.BookingTypeCode == "IMPORT" && step is { Direction: GateRules.In, FullEmpty: GateRules.Full };
            if (importFullIn && rows[i].AcceptTypeChange) typeChanges[lineId] = keyed;
            else
                mismatches.Add(new
                {
                    index = i, containerNo = number, orderNo = line.OrderNo, bookedType = line.EquipmentTypeCode, keyedType = keyed,
                    canChange = importFullIn,
                    message = importFullIn
                        ? $"{number} is booked as {line.EquipmentTypeCode} but is {keyed}. Do you want to proceed? The booking will follow the box."
                        : $"{number} is booked as {line.EquipmentTypeCode} but is {keyed}. Only an IMPORT FULL drop-off changes the booking at the gate: correct {line.OrderNo} first.",
                });
        }
        if (mismatches.Count > 0)
            return await StopAsync(TypedResults.Problem(title: "A box is another type than booked.",
                detail: "Nothing was charged and nothing was recorded. Confirm the change (acceptTypeChange) where it is allowed, then Save again.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = "TYPE_MISMATCH", ["rows"] = mismatches, ["charged"] = false }));
        foreach (var (lineId, type) in typeChanges)
            if (await BookingEndpoints.ChangeBoxTypeAsync(db, master, lineId, type, caller, time, ct) is { } refused)
                return await StopAsync(TosSupport.Conflict(refused, "Nothing was charged and nothing was recorded."));
        db.ChangeTracker.Clear();

        // ── 2. blind orders (the same box answers with the same order) ──────
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Blind is not { } blind) continue;
            var raised = await BookingEndpoints.RaiseBlindOrderAsync(
                new BlindOrderRequest(branchId, blind.ContainerNo!, blind.LineCode!, blind.CustomerCode, blind.EquipmentTypeCode,
                    blind.CarrierRef, blind.AgentCode, request.Truck?.HaulierCode ?? joined?.HaulierPartyCode, blind.Remarks, draftId),
                db, master, users, clock, caller, scope, ct);
            db.ChangeTracker.Clear();
            var order = raised.Result switch
            {
                CreatedAtRoute<BookingDetailResponse> c => c.Value,
                Ok<BookingDetailResponse> o => o.Value,
                _ => null,
            };
            if (order is null)
            {
                await UndoAsync("Gate Save stopped before payment.");
                return Prefixed(raised.Result, $"rows[{i}].blind");
            }
            if (raised.Result is CreatedAtRoute<BookingDetailResponse>) raisedNow.Add(order.Booking.BookingId);
            var box = order.Containers.Single(c => c.EndedAt is null);
            lines[i] = (box.BookingContainerId, order.Booking.BookingId, order.Booking.OrderNo, box.ContainerNo!);
        }

        // ── 3. the money: one receipt for the truck ─────────────────────────
        TruckPaymentResult? payment = null;
        if (request.Payment is { } pay)
        {
            var bookingIds = lines.Select(l => l.BookingId).Distinct().ToList();
            if (!await CatchUpAsync(wake, () => cashier.KnowsBookingsAsync(bookingIds, ct), ct))
            {
                await UndoAsync("Gate Save: the cash window had not heard of the order; nothing was charged.");
                return TosSupport.Conflict("The cash window has not heard of the new order yet.", "Nothing was charged or gated. Save again in a moment.");
            }
            // A box whose type was just changed (A11, or by an interrupted try of this Save) is priced as that type.
            var keyedTypes = rows.Select((r, i) => (r, i)).Where(x => x.r.BookingContainerId is not null && x.r.EquipmentTypeCode.Clean() is not null)
                .ToDictionary(x => lines[x.i].BookingContainerId, x => x.r.EquipmentTypeCode.Clean()!);
            // A number written onto an empty place a moment ago travels to the window by the outbox: its coupon must name the box.
            var numbers = nominatedNow.ToDictionary(n => n.BookingContainerId, n => n.ContainerNo);
            if (numbers.Count > 0 && !await CatchUpAsync(wake, () => cashier.KnowsBoxNumbersAsync(numbers, ct), ct))
            {
                await UndoAsync("Gate Save: the cash window had not heard of the box keyed on the booking; nothing was charged.");
                return TosSupport.Conflict("The cash window has not heard of the box keyed on the booking yet.", "Nothing was charged or gated. Save again in a moment.");
            }
            if (keyedTypes.Count > 0 && !await CatchUpAsync(wake, () => cashier.KnowsBoxTypesAsync(keyedTypes, ct), ct))
            {
                await UndoAsync("Gate Save: the cash window had not heard of the type change; nothing was charged.");
                return TosSupport.Conflict("The cash window has not heard of the box's new type yet.", "The booking already follows the box; nothing was charged or gated. Save again in a moment.");
            }

            payment = await cashier.PayAsync(new TruckPaymentRequest(
                branchId, caller.UserId(), key,
                lines.GroupBy(l => l.BookingId).Select(g => new TruckBookingBoxes(g.Key, g.Select(l => l.BookingContainerId).ToList())).ToList(),
                request.Truck?.TruckCategoryCode ?? joined?.TruckCategoryCode, request.Truck?.HaulierCode ?? joined?.HaulierPartyCode, request.Vas,
                pay.Payer, pay.Payments ?? [], pay.ExpectedTotal, pay.WithholdingTax), ct);
            if (payment.Outcome == TruckPaymentOutcome.Refused)
            {
                var problem = payment.Problem!;
                await UndoAsync($"Gate Save: payment refused ({problem.Title}).");
                return TypedResults.Problem(title: problem.Title, detail: problem.Detail, statusCode: problem.Status,
                    extensions: new Dictionary<string, object?> { ["field"] = problem.Field is null ? null : $"payment.{problem.Field}", ["charged"] = false });
            }

            if (payment.Receipt is { } receipt && save.ReceiptId is null)
            {
                save.ReceiptId = receipt.ReceiptId;
                save.ReceiptNo = receipt.ReceiptNo;
                save.UpdatedAt = time.GetUtcNow();
                db.TripSaves.Update(save);   // detached by the blind orders' ChangeTracker.Clear()
                await db.SaveChangesAsync(ct);
            }

            // The coupons travel by the outbox (ADR-007): the barrier must hold them before the boxes move.
            var refs = payment.Coupons.Select(c => c.CouponRef).ToList();
            await CatchUpAsync(wake, async () =>
                await db.GateAuthorizations.AsNoTracking().CountAsync(a => refs.Contains(a.CouponRef), ct) >= refs.Count, ct);
        }

        // ── 4. the moves: drop-offs before pick-ups, one EIR each ───────────
        var results = new TripRowResponse?[rows.Count];
        Guid? visitId = joined?.TruckVisitId;
        string? visitNo = joined?.VisitNo;
        foreach (var i in Enumerable.Range(0, rows.Count).OrderBy(i => rows[i].Move!.Direction == GateRules.In ? 0 : 1).ThenBy(i => i))
        {
            if (!gateOut && rows[i].Move!.Direction == GateRules.Out) continue;   // a pick-up: planned below, moved at gate out
            var move = Move(rows[i], asHeld, visitId);
            var recorded = await GateEndpoints.RecordCoreAsync(move, $"{key}#{i}", db, barrier, master, clock, caller, scope, time, users, ct);
            db.ChangeTracker.Clear();
            var coupon = payment?.Coupons.FirstOrDefault(c => c.BookingContainerId == lines[i].BookingContainerId)?.CouponRef;
            if (recorded.Result is Created<GateTransactionResponse> { Value: { } eir })
            {
                visitId ??= eir.TruckVisitId;
                visitNo ??= eir.VisitNo;
                var (surveyId, holds, surveyProblem) = rows[i].Damages is { Count: > 0 } damages
                    ? await SurveyAsync(eir, move, damages, db, master, caller, scope, time, ct)
                    : (null, null, null);
                IReadOnlyList<GateFindingResponse> said = [.. eir.Findings ?? [], .. new[] { surveyProblem, switched.GetValueOrDefault(i) }.OfType<GateFindingResponse>()];
                if (gateOut && plans.FirstOrDefault(p => p.BookingContainerId == rows[i].BookingContainerId && p.Status == "PLANNED") is { } plan)
                {
                    db.VisitPickups.Attach(plan);
                    plan.Status = "RELEASED";
                    plan.GateTransactionId = eir.GateTransactionId;
                    plan.ReleasedAt = eir.TransactionAt;
                    plan.ContainerNo = eir.ContainerNo;
                    plan.UpdatedAt = time.GetUtcNow();
                    plan.UpdatedBy = caller.UserId();
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                }
                results[i] = new TripRowResponse(i, eir.ContainerNo, lines[i].BookingContainerId, eir.OrderNo, "GATED",
                    eir.EirNo, eir.GateTransactionId, $"/api/tos/gate/transactions/{eir.GateTransactionId}/eir.pdf", coupon, null,
                    said, surveyId, holds);
            }
            else
            {
                var (reason, findings) = Why(recorded.Result);
                results[i] = new TripRowResponse(i, lines[i].ContainerNo, lines[i].BookingContainerId, lines[i].OrderNo, "NOT_GATED",
                    null, null, null, coupon, reason, switched.GetValueOrDefault(i) is { } moved ? [.. findings, moved] : findings);
            }
        }

        // ── 5. gate in: the pick-ups, planned on the truck's visit and held for it until it leaves ──
        if (!gateOut)
            foreach (var i in Enumerable.Range(0, rows.Count).Where(i => rows[i].Move!.Direction == GateRules.Out))
            {
                if (visitId is null)   // a truck that came only to collect
                {
                    var branch = (await clock.BranchesAsync([branchId], ct))[branchId];
                    var (opened, invalid) = await GateEndpoints.NewVisitAsync(request.Truck!, branchId, branch, time.GetUtcNow(), db, master, clock, caller, ct);
                    if (invalid is not null) return invalid;
                    opened!.GateInAt = opened.ArrivedAt;
                    await db.SaveChangesAsync(ct);
                    (visitId, visitNo) = (opened.TruckVisitId, opened.VisitNo);
                    db.ChangeTracker.Clear();
                }
                var number = ContainerNumber.Normalise(rows[i].Move!.ContainerNo ?? "") is { Length: > 0 } n ? n : null;
                var at = time.GetUtcNow();
                var plan = await db.VisitPickups.SingleOrDefaultAsync(p => p.BookingContainerId == lines[i].BookingContainerId && p.Status == "PLANNED", ct);
                if (plan is null)
                {
                    plan = new VisitPickup
                    {
                        TenantId = caller.TenantId(), BranchId = branchId, TruckVisitId = visitId.Value, BookingContainerId = lines[i].BookingContainerId,
                        ContainerNo = number, Status = "PLANNED", PlannedAt = at, PlannedBy = caller.UserId(), TripSaveId = save.TripSaveId,
                        CreatedBy = caller.UserId(), UpdatedBy = caller.UserId(),
                    };
                    db.VisitPickups.Add(plan);
                }
                // The box (or the empty place) stays this truck's until it leaves with it.
                var holds = await BoxReservations.LiveAsync(db, lines[i].BookingContainerId, number, at, ct);
                var hold = holds.FirstOrDefault(h => h.DraftId == draftId || h.DraftId == visitId);
                if (hold is null)
                {
                    hold = new BoxReservation
                    {
                        TenantId = caller.TenantId(), BranchId = branchId, BookingContainerId = lines[i].BookingContainerId, ContainerNo = number,
                        ReservedBy = caller.UserId(), ReservedAt = at, CreatedBy = caller.UserId(),
                    };
                    db.BoxReservations.Add(hold);
                }
                hold.DraftId = visitId.Value;
                hold.ExpiresAt = at + BoxReservations.PickupHold;
                hold.UpdatedAt = at;
                hold.UpdatedBy = caller.UserId();
                string? reason = null;
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException e) when (e.InnerException?.Message is { } m && (m.Contains("uq_visit_pickup__planned") || m.Contains("uq_box_reservation__live")))
                {
                    reason = "Another truck was planned for this box a moment ago.";
                }
                db.ChangeTracker.Clear();
                var coupon = payment?.Coupons.FirstOrDefault(c => c.BookingContainerId == lines[i].BookingContainerId)?.CouponRef;
                results[i] = new TripRowResponse(i, number ?? "", lines[i].BookingContainerId, lines[i].OrderNo, reason is null ? "PLANNED" : "NOT_PLANNED",
                    null, null, null, coupon, reason, switched.GetValueOrDefault(i) is { } moved ? [moved] : [], VisitPickupId: reason is null ? plan.VisitPickupId : null);
            }

        // ── 6. gate out: nothing left to collect, the truck has left ──
        DateTimeOffset? truckLeftAt = null;
        if (gateOut && visitId is { } leaving
            && !await db.VisitPickups.AnyAsync(p => p.TruckVisitId == leaving && p.Status == "PLANNED", ct))
        {
            var visitRow = await db.TruckVisits.SingleAsync(v => v.TruckVisitId == leaving, ct);
            if (visitRow.GateOutAt is null)
            {
                visitRow.GateOutAt = time.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }
            truckLeftAt = visitRow.GateOutAt;
            db.ChangeTracker.Clear();
        }

        var answer = new TripSaveResponse(save.TripSaveId, visitId, visitNo,
            payment?.Receipt is { } r
                ? new TripReceiptResponse(r.ReceiptId, r.ReceiptNo, r.Subtotal, r.Tax, r.Total, r.WithholdingTax, r.Total - r.WithholdingTax,
                    r.CurrencyCode, $"/api/revenue/window/receipts/{r.ReceiptId}/receipt.pdf", $"/api/revenue/window/receipts/{r.ReceiptId}/coupon.pdf")
                : null,
            results.Select(x => x!).ToList(),
            visitId is { } truckVisit ? $"/api/tos/gate/visits/{truckVisit}/truck-in.pdf" : null,
            truckLeftAt);

        save.Status = "DONE";
        save.TruckVisitId = visitId;
        save.ResultJson = JsonSerializer.Serialize(answer, Json);
        save.UpdatedAt = time.GetUtcNow();
        save.UpdatedBy = caller.UserId();
        db.TripSaves.Update(save);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/tos/gate/trips/{save.TripSaveId}", answer);
    }

    /// <summary>
    /// The row's damages as the GATE_IN survey of its EIR (A4). A resumed Save finds the survey it already wrote.
    /// The codes were checked before any money was taken, so a refusal here is told on the row, not thrown.
    /// </summary>
    private static async Task<(Guid? SurveyId, IReadOnlyList<string>? Holds, GateFindingResponse? Problem)> SurveyAsync(
        GateTransactionResponse eir, GateTransactionRequest move, IReadOnlyList<SurveyDamageRequest> damages, TosDbContext db,
        IMasterDataReferences master, ITenantContext caller, ICallerPermissions scope, TimeProvider time, CancellationToken ct)
    {
        if (await db.Surveys.AsNoTracking().Where(s => s.GateTransactionId == eir.GateTransactionId)
                .Select(s => (Guid?)s.SurveyId).FirstOrDefaultAsync(ct) is { } done)
            return (done, [], null);
        var made = await SurveyEndpoints.CreateAsync(
            new SaveSurveyRequest(eir.ContainerNo, "GATE_IN", eir.GateTransactionId, eir.TransactionAt, null,
                move.ConditionCode, move.GradeCode, null, damages),
            db, master, caller, scope, time, ct);
        db.ChangeTracker.Clear();
        return made.Result is Created<SurveyResponse> { Value: { } survey }
            ? (survey.SurveyId, survey.HoldsApplied, null)
            : (null, null, new GateFindingResponse("SURVEY_NOT_RECORDED",
                $"The box is in, but its damage was not recorded: {Why(made.Result).Reason} Record it on the survey screen.", "WARN"));
    }

    /// <summary>A truck carries 1×40' or 2×20' each way: the boxes' lengths in one direction add up to 45 ft at most.</summary>
    private sealed record Aboard(string Direction, string? EquipmentTypeCode);

    private static async Task<ValidationProblem?> FeetAsync(TosDbContext db, IMasterDataReferences master, IReadOnlyList<TripRowRequest> rows,
        IReadOnlyList<Aboard> aboard, CancellationToken ct)
    {
        var lineIds = rows.Select(r => r.BookingContainerId).OfType<Guid>().ToList();
        var booked = await (
            from x in db.BookingContainers.AsNoTracking().Where(x => lineIds.Contains(x.BookingContainerId))
            join r in db.EquipmentRequirements on x.EquipmentRequirementId equals r.EquipmentRequirementId
            select new { x.BookingContainerId, r.EquipmentTypeCode }).ToDictionaryAsync(x => x.BookingContainerId, x => x.EquipmentTypeCode, ct);
        var blindNumbers = rows.Where(r => r.Blind is not null && r.Blind.EquipmentTypeCode is null)
            .Select(r => ContainerNumber.Normalise(r.Blind!.ContainerNo ?? "")).ToList();
        var registry = blindNumbers.Count == 0 ? new Dictionary<string, ContainerRef>() : (await master.ContainersAsync(blindNumbers, ct)).ToDictionary();
        string? TypeOf(TripRowRequest r) => r.BookingContainerId is { } id ? r.EquipmentTypeCode.Clean() ?? booked.GetValueOrDefault(id)
            : r.Blind?.EquipmentTypeCode?.Trim().ToUpperInvariant() ?? registry.GetValueOrDefault(ContainerNumber.Normalise(r.Blind?.ContainerNo ?? ""))?.EquipmentTypeCode;
        var types = rows.Select(TypeOf).ToList();
        var sizes = await master.EquipmentTypesAsync(types.Concat(aboard.Select(a => a.EquipmentTypeCode)).OfType<string>().Distinct(), ct);
        int Feet(string? type) => type is not null && sizes.GetValueOrDefault(type) is { } e && int.TryParse(e.SizeCode, out var ft) ? ft : 0;
        foreach (var way in new[] { GateRules.In, GateRules.Out })
        {
            var already = aboard.Where(a => a.Direction == way).ToList();
            var feet = rows.Select((r, i) => (r, i)).Where(x => x.r.Move?.Direction == way).Sum(x => Feet(types[x.i])) + already.Sum(a => Feet(a.EquipmentTypeCode));
            var count = rows.Count(r => r.Move?.Direction == way) + already.Count;
            if (feet > 45 || count > 2)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["rows"] = [already.Count == 0
                        ? $"A truck carries 1×40' or 2×20' each way; these boxes going {way} add up to {feet} ft."
                        : $"A truck carries 1×40' or 2×20' each way; with the {already.Count} box(es) this truck already moved {way}, that is {count} box(es), {feet} ft."],
                });
        }
        return null;
    }

    /// <summary>The row's move as the gate records it: this depot, this draft, and the truck — named on the first box, then its visit.</summary>
    private static GateTransactionRequest Move(TripRowRequest row, TripSaveRequest request, Guid? visitId) =>
        row.Move! with
        {
            BranchId = request.BranchId,
            DraftId = request.DraftId,
            TruckVisitId = visitId,
            Truck = visitId is null ? request.Truck : null,
        };

    /// <summary>Wakes the outbox dispatchers and waits until <paramref name="ready"/> holds (or <see cref="CatchUp"/> passes).</summary>
    private static async Task<bool> CatchUpAsync(OutboxWake wake, Func<Task<bool>> ready, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + CatchUp;
        while (true)
        {
            if (await ready()) return true;
            if (DateTimeOffset.UtcNow >= until) return false;
            wake.Wake();
            await Task.Delay(200, ct);
        }
    }

    private static (string Reason, IReadOnlyList<GateFindingResponse> Findings) Why(IResult result) => result switch
    {
        ProblemHttpResult p => (
            string.Join(" ", new[] { p.ProblemDetails.Title, p.ProblemDetails.Detail }.Where(t => !string.IsNullOrWhiteSpace(t))),
            p.ProblemDetails.Extensions.TryGetValue("findings", out var f) && f is IEnumerable<GateFindingResponse> list ? list.ToList() : []),
        ValidationProblem v => (string.Join(" ", v.ProblemDetails.Errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}")), []),
        _ => ("The gate could not record this box.", []),
    };

    /// <summary>A step's 400 / 409, with its fields placed under the row they belong to.</summary>
    private static Results<Created<TripSaveResponse>, ValidationProblem, ProblemHttpResult> Prefixed(IResult result, string prefix) => result switch
    {
        ValidationProblem v => TypedResults.ValidationProblem(v.ProblemDetails.Errors.ToDictionary(e => $"{prefix}.{e.Key}", e => e.Value)),
        ProblemHttpResult p => TypedResults.Problem(title: p.ProblemDetails.Title, detail: p.ProblemDetails.Detail, statusCode: p.StatusCode,
            extensions: new Dictionary<string, object?> { ["field"] = prefix }),
        _ => TosSupport.Conflict("The blind order could not be raised."),
    };
}
