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
            if (!Validator.TryValidateObject(move, new ValidationContext(move), invalid, validateAllProperties: true))
                foreach (var v in invalid) errors.Add($"rows[{i}].move.{string.Join(",", v.MemberNames)}", v.ErrorMessage ?? "Invalid.");
            if (row.Blind is { } blind && !string.Equals(ContainerNumber.Normalise(blind.ContainerNo ?? ""), ContainerNumber.Normalise(row.Move.ContainerNo ?? ""), StringComparison.Ordinal))
                errors.Add($"rows[{i}].blind.containerNo", "The blind order and the move must name the same box.");
        }
        if (rows.Count(r => r.Move?.Direction == GateRules.In) > 2 || rows.Count(r => r.Move?.Direction == GateRules.Out) > 2)
            errors.Add("rows", "A truck carries at most two boxes each way.");
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

        // ── 1. no box another truck's clerk holds ───────────────────────────
        var now = time.GetUtcNow();
        for (var i = 0; i < rows.Count; i++)
        {
            var number = ContainerNumber.Normalise(rows[i].Move!.ContainerNo);
            if (await BoxReservations.HeldByOtherAsync(db, rows[i].BookingContainerId, number, draftId, now, ct) is { } held)
            {
                var name = (await users.DisplayNamesAsync([held.ReservedBy], ct)).GetValueOrDefault(held.ReservedBy);
                return TosSupport.Conflict(BoxReservations.Finding(held, name).Message, $"rows[{i}]: remove it from this truck, or ask that clerk to.");
            }
        }

        // ── 1b. every box past the barrier BEFORE any money is taken (A1) ──
        var refusals = new List<object>();
        for (var i = 0; i < rows.Count; i++)
        {
            var blocks = await GateEndpoints.PrecheckAsync(Move(rows[i], request, null), rows[i].Blind is not null, barrier, master, time, ct);
            if (blocks.Count > 0)
                refusals.Add(new { index = i, containerNo = ContainerNumber.Normalise(rows[i].Move!.ContainerNo),
                    findings = blocks.Select(f => new GateFindingResponse(f.Code, f.Message, f.Severity.ToString().ToUpperInvariant())).ToList() });
        }
        if (refusals.Count > 0)
            return TypedResults.Problem(title: "Some boxes cannot go through the gate.",
                detail: "Nothing was charged and nothing was recorded. Fix or remove these boxes, then Save again.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["rows"] = refusals, ["charged"] = false });

        // ── 1c. one truck carries 1×40' or 2×20' each way (Vector GateIn.cs:1366) ──
        if (await FeetAsync(db, master, rows, ct) is { } feet) return feet;

        // ── 1d. the booked boxes, before any blind order is raised ──────────
        var lines = new (Guid BookingContainerId, Guid BookingId, string OrderNo, string ContainerNo)[rows.Count];
        var mismatches = new List<object>();
        var typeChanges = new Dictionary<Guid, string>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].BookingContainerId is not { } lineId) continue;
            var line = await (
                from x in db.BookingContainers.AsNoTracking()
                join b in db.Bookings on x.BookingId equals b.BookingId
                join q in db.EquipmentRequirements on x.EquipmentRequirementId equals q.EquipmentRequirementId
                where x.BookingContainerId == lineId
                select new { x.BookingContainerId, x.BookingId, b.OrderNo, b.BranchId, x.ContainerNo, b.BookingTypeCode, b.OrderTypeCode, q.EquipmentTypeCode })
                .SingleOrDefaultAsync(ct);
            if (line is null || line.BranchId != branchId)
                return TosSupport.Invalid($"rows[{i}].bookingContainerId", "Not a booked box at this depot.");
            var number = ContainerNumber.Normalise(rows[i].Move!.ContainerNo);
            if (line.ContainerNo is null)
                return TosSupport.Invalid($"rows[{i}].bookingContainerId", $"That place on {line.OrderNo} has no container number yet: nominate {number} on it first.");
            if (line.ContainerNo != number)
                return TosSupport.Invalid($"rows[{i}].move.containerNo", $"{line.OrderNo}'s box is {line.ContainerNo}, not {number}.");
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
            return TypedResults.Problem(title: "A box is another type than booked.",
                detail: "Nothing was charged and nothing was recorded. Confirm the change (acceptTypeChange) where it is allowed, then Save again.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = "TYPE_MISMATCH", ["rows"] = mismatches, ["charged"] = false });
        foreach (var (lineId, type) in typeChanges)
            if (await BookingEndpoints.ChangeBoxTypeAsync(db, master, lineId, type, caller, time, ct) is { } refused)
                return TosSupport.Conflict(refused, "Nothing was charged and nothing was recorded.");
        db.ChangeTracker.Clear();

        // ── 2. blind orders (the same box answers with the same order) ──────
        var raisedNow = new List<Guid>();   // raised by THIS call: undone if the money is not taken (A2)
        async Task UndoAsync(string why)
        {
            foreach (var id in raisedNow) await BookingEndpoints.UndoBlindOrderAsync(db, id, why, caller, time, ct);
            db.ChangeTracker.Clear();
        }
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Blind is not { } blind) continue;
            var raised = await BookingEndpoints.RaiseBlindOrderAsync(
                new BlindOrderRequest(branchId, blind.ContainerNo!, blind.LineCode!, blind.CustomerCode, blind.EquipmentTypeCode,
                    blind.CarrierRef, blind.AgentCode, request.Truck!.HaulierCode, blind.Remarks, draftId),
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
            if (keyedTypes.Count > 0 && !await CatchUpAsync(wake, () => cashier.KnowsBoxTypesAsync(keyedTypes, ct), ct))
            {
                await UndoAsync("Gate Save: the cash window had not heard of the type change; nothing was charged.");
                return TosSupport.Conflict("The cash window has not heard of the box's new type yet.", "The booking already follows the box; nothing was charged or gated. Save again in a moment.");
            }

            payment = await cashier.PayAsync(new TruckPaymentRequest(
                branchId, caller.UserId(), key,
                lines.GroupBy(l => l.BookingId).Select(g => new TruckBookingBoxes(g.Key, g.Select(l => l.BookingContainerId).ToList())).ToList(),
                request.Truck!.TruckCategoryCode, request.Truck.HaulierCode, request.Vas,
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
        Guid? visitId = null;
        string? visitNo = null;
        foreach (var i in Enumerable.Range(0, rows.Count).OrderBy(i => rows[i].Move!.Direction == GateRules.In ? 0 : 1).ThenBy(i => i))
        {
            var move = Move(rows[i], request, visitId);
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
                results[i] = new TripRowResponse(i, eir.ContainerNo, lines[i].BookingContainerId, eir.OrderNo, "GATED",
                    eir.EirNo, eir.GateTransactionId, $"/api/tos/gate/transactions/{eir.GateTransactionId}/eir.pdf", coupon, null,
                    surveyProblem is null ? eir.Findings ?? [] : [.. eir.Findings ?? [], surveyProblem], surveyId, holds);
            }
            else
            {
                var (reason, findings) = Why(recorded.Result);
                results[i] = new TripRowResponse(i, lines[i].ContainerNo, lines[i].BookingContainerId, lines[i].OrderNo, "NOT_GATED",
                    null, null, null, coupon, reason, findings);
            }
        }

        var answer = new TripSaveResponse(save.TripSaveId, visitId, visitNo,
            payment?.Receipt is { } r
                ? new TripReceiptResponse(r.ReceiptId, r.ReceiptNo, r.Subtotal, r.Tax, r.Total, r.WithholdingTax, r.Total - r.WithholdingTax,
                    r.CurrencyCode, $"/api/revenue/window/receipts/{r.ReceiptId}/receipt.pdf", $"/api/revenue/window/receipts/{r.ReceiptId}/coupon.pdf")
                : null,
            results.Select(x => x!).ToList(),
            visitId is { } truckVisit ? $"/api/tos/gate/visits/{truckVisit}/truck-in.pdf" : null);

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
    private static async Task<ValidationProblem?> FeetAsync(TosDbContext db, IMasterDataReferences master, IReadOnlyList<TripRowRequest> rows, CancellationToken ct)
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
        var sizes = await master.EquipmentTypesAsync(types.OfType<string>().Distinct(), ct);
        foreach (var way in new[] { GateRules.In, GateRules.Out })
        {
            var feet = rows.Select((r, i) => (r, i)).Where(x => x.r.Move?.Direction == way)
                .Sum(x => types[x.i] is { } t && sizes.GetValueOrDefault(t) is { } e && int.TryParse(e.SizeCode, out var ft) ? ft : 0);
            if (feet > 45)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                    { ["rows"] = [$"A truck carries 1×40' or 2×20' each way; these boxes going {way} add up to {feet} ft."] });
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
