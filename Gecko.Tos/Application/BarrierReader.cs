using Gecko.MasterData.Contracts;
using Gecko.SharedKernel;
using Gecko.Tos.Domain;
using Gecko.Tos.Infrastructure.Persistence;
using Gecko.Tos.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Application;

/// <summary>Everything the barrier knows about one box at one instant (PLAN §5.4).</summary>
internal sealed record BarrierView
{
    public required string ContainerNo { get; init; }
    public required string Direction { get; init; }
    public required DateTimeOffset At { get; init; }
    public required List<GateFinding> Findings { get; init; }

    public Booking? Booking { get; init; }
    public BookingContainer? Assignment { get; init; }
    public MovementPlan? Step { get; init; }
    public OrderTypeStepRef? StepRules { get; init; }
    public List<MovementPlan> StepsToSkip { get; init; } = [];
    public EquipmentRequirement? Requirement { get; init; }
    public ContainerVisit? OpenVisit { get; init; }
    public List<(VwActiveHold Hold, HoldRef? Definition)> Holds { get; init; } = [];

    public string? CutoffKind { get; init; }
    public DateTimeOffset? CutoffAt { get; init; }
    public bool IsLate { get; init; }
    public CutoffException? CoveringException { get; init; }

    public GateAuthorization? Coupon { get; init; }
    public bool IsCheckDigitValid { get; init; }
    public ContainerRef? Registry { get; init; }

    public string Decision => GateRules.Decide(Findings);

    /// <summary>The overrides a caller would have to supply and hold a permission for.</summary>
    public bool NeedsLateOverride => Findings.Any(f => f.Code == "LATE");
    public bool NeedsCheckDigitOverride => Findings.Any(f => f.Code == "CHECK_DIGIT");
}

/// <summary>
/// The barrier read (PLAN §5.4): given a container number and a direction, five
/// indexed seeks — all in gecko_tos, no cross-context call — answer whether the
/// box may move and what the EIR will say.
///
/// The one thing that does leave the database is the MDM lookup for the step's
/// gate rules and the holds' blocking scopes. §5.4 wants that served from an
/// in-process cache; today it is a bulk call per request, which is honest about
/// where the remaining latency is and changes nothing about the answer.
/// </summary>
internal sealed class BarrierReader(TosDbContext db, IMasterDataReferences master, BranchClock clock)
{
    public async Task<BarrierView> ReadAsync(Guid branchId, string rawContainerNo, string direction, DateTimeOffset at, CancellationToken ct)
    {
        var containerNo = ContainerNumber.Normalise(rawContainerNo);
        var findings = new List<GateFinding>();

        if (!ContainerNumber.IsWellFormed(containerNo))
        {
            findings.Add(new GateFinding("NOT_A_CONTAINER_NUMBER",
                $"'{rawContainerNo}' is not a container number (4 letters ending U/J/Z, then 7 digits).", GateSeverity.Block));
            return new BarrierView { ContainerNo = containerNo, Direction = direction, At = at, Findings = findings };
        }

        var checkDigitValid = ContainerNumber.IsValid(containerNo);
        var enforceDigit = await master.GetBoolSettingAsync(TosSettingKeys.EnforceCheckDigit, branchId, false, ct);
        if (GateRules.CheckDigit(containerNo, checkDigitValid, enforceDigit, ContainerNumber.CheckDigitOf(containerNo)) is { } digit)
            findings.Add(digit);

        // Gate hours (TIER3 §1): a warning at the depot the barrier is AT, never a refusal.
        if (GateRules.OutsideHours(await master.GateHoursStatusAsync(branchId, at, ct)) is { } closed)
            findings.Add(closed);

        // ── 1. the active assignment ────────────────────────────────────────
        var assignment = await db.BookingContainers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ContainerNo == containerNo && x.EndedAt == null, ct);

        // ── 4. the holds (read even with no booking: a customs hold on an
        //       unknown box is exactly the case the clerk must be told about) ─
        var active = await db.VwActiveHolds.AsNoTracking().Where(h => h.ContainerNo == containerNo).ToListAsync(ct);
        var holdDefinitions = await master.HoldsAsync(active.Select(h => h.HoldCode), ct);
        var holds = active.Select(h => (Hold: h, Definition: holdDefinitions.GetValueOrDefault(h.HoldCode))).ToList();
        // Judged once the step is known (a movement that releases damaged boxes lets
        // the damage hold through), but said here, where the clerk expects them.
        var holdsAt = findings.Count;
        void JudgeHolds(bool movementReleasesDamaged) =>
            findings.InsertRange(holdsAt, holds
                .Select(h => GateRules.HoldBlocks(h.Hold.HoldCode, h.Definition?.BlockingScope, h.Definition?.DescriptionEn, direction,
                    h.Definition?.AutoApplyOnEvent, movementReleasesDamaged))
                .OfType<GateFinding>());

        // ── 5. the yard ─────────────────────────────────────────────────────
        var openVisit = await db.ContainerVisits.AsNoTracking()
            .SingleOrDefaultAsync(v => v.ContainerNo == containerNo && v.GateOutTransactionId == null, ct);

        var registry = (await master.ContainersAsync([containerNo], ct)).GetValueOrDefault(containerNo);

        if (assignment is null)
        {
            JudgeHolds(movementReleasesDamaged: false);
            // Q1: the ad-hoc gate is not a refusal — it is a WALK_IN booking the
            // clerk creates first. The barrier says so rather than just "no".
            findings.Add(new GateFinding("NO_ASSIGNMENT",
                $"{containerNo} is not on an open booking. Raise the booking (or a walk-in) and assign the box first.",
                GateSeverity.Block));
            return new BarrierView
            {
                ContainerNo = containerNo, Direction = direction, At = at, Findings = findings,
                Holds = holds, OpenVisit = openVisit, IsCheckDigitValid = checkDigitValid, Registry = registry,
            };
        }

        var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.BookingId == assignment.BookingId, ct);
        var requirement = await db.EquipmentRequirements.AsNoTracking()
            .SingleOrDefaultAsync(r => r.EquipmentRequirementId == assignment.EquipmentRequirementId, ct);

        if (booking.BranchId != branchId)
            findings.Add(new GateFinding("WRONG_BRANCH",
                $"{containerNo} is booked at another depot on {booking.OrderNo}. It cannot be gated here.", GateSeverity.Block));

        if (booking.Status != BookingRules.Open)
            findings.Add(new GateFinding("BOOKING_NOT_OPEN",
                $"Booking {booking.OrderNo} is {booking.Status}.", GateSeverity.Block));

        var branch = (await clock.BranchesAsync([booking.BranchId], ct)).GetValueOrDefault(booking.BranchId);
        var today = branch is null ? DateOnly.FromDateTime(at.UtcDateTime) : clock.Today(branch);
        if (GateRules.Expired(booking.OrderNo, booking.ValidTo, today) is { } expired) findings.Add(expired);

        // ── 2. the next step, and 3. its MDM gate rules ─────────────────────
        var pending = await db.MovementPlans.AsNoTracking()
            .Where(p => p.BookingContainerId == assignment.BookingContainerId && p.Status == "PENDING")
            .OrderBy(p => p.SequenceNo).ToListAsync(ct);

        var plan = (await master.OrderTypePlansAsync([booking.OrderTypeCode], ct)).GetValueOrDefault(booking.OrderTypeCode);
        var rulesBySource = plan?.Steps.ToDictionary(s => s.OrderTypeMovementId) ?? [];

        MovementPlan? step = null;
        OrderTypeStepRef? stepRules = null;
        var toSkip = new List<MovementPlan>();

        foreach (var candidate in pending)
        {
            var rules = rulesBySource.GetValueOrDefault(candidate.OrderTypeMovementId);
            if (rules is not null && string.Equals(rules.Direction, direction, StringComparison.Ordinal))
            {
                step = candidate;
                stepRules = rules;
                break;
            }

            // §5.3: an earlier REQUIRED step cannot be jumped; an optional one is
            // skipped on record, with a reason, never silently.
            if (candidate.IsRequired)
            {
                findings.Add(new GateFinding("STEP_OUT_OF_ORDER",
                    $"{candidate.MovementCode} (step {candidate.SequenceNo}) is still pending and required. It has to happen first.",
                    GateSeverity.Block));
                break;
            }
            toSkip.Add(candidate);
        }

        JudgeHolds(stepRules?.AllowDamagedRelease ?? false);

        if (step is null && !findings.Any(f => f.Code == "STEP_OUT_OF_ORDER"))
            findings.Add(new GateFinding("NO_PENDING_STEP",
                pending.Count == 0
                    ? $"Every step on {booking.OrderNo} is done for {containerNo}."
                    : $"Nothing pending for {containerNo} goes {direction} on {booking.OrderNo}.",
                GateSeverity.Block));

        if (step is not null && stepRules is not null)
        {
            if (GateRules.YardState(containerNo, direction, openVisit is not null) is { } yard) findings.Add(yard);

            // ── 6. the cut-off (§5.2) ───────────────────────────────────────
            // EXPORT only. An import box gating in FULL is coming OFF a ship that
            // has already sailed; judging it against that ship's yard cut-off
            // marks every import in the country late.
            var isFull = string.Equals(stepRules.FullEmpty, GateRules.Full, StringComparison.Ordinal);
            var isExport = booking.DirectionCode.Contains("EXPORT", StringComparison.OrdinalIgnoreCase);

            // ── 7. the gate-out release gates (Vector GateOut.cs:1139-1171) ──
            if (direction == GateRules.Out)
            {
                if (isExport && GateRules.FixedPort(containerNo, registry?.FixedPortCodes, booking.OrderNo, booking.PodPortCode) is { } wrongPort)
                    findings.Add(wrongPort);

                if (openVisit is not null && GateRules.BeforePreviousMove(containerNo, at, openVisit.LastEventAt) is { } early)
                    findings.Add(early);

                if (isFull && isExport && booking.VesselCallId is { } sailingOn)
                {
                    var call = await db.VesselCalls.AsNoTracking().Where(c => c.VesselCallId == sailingOn)
                        .Select(c => new { c.CallRef, c.LadenReleaseAt }).SingleOrDefaultAsync(ct);
                    if (GateRules.BeforeLadenRelease(containerNo, call?.CallRef, at, call?.LadenReleaseAt) is { } held)
                        findings.Add(held);
                }
            }

            if (direction == GateRules.In && isFull && isExport && booking.VesselCallId is { } callId)
            {
                var kind = CutoffKindFor(booking, requirement);
                var effective = await CutoffLookup.EffectiveAsync(db, callId, booking.LinePartyId, booking.BranchId, ct);
                // A CFS booking works to the CFS cut-off when the call states one, else to the yard's.
                if (CutoffRules.IsCfs(kind) && effective.All(c => c.Kind != kind))
                    kind = "YARD_" + kind["CFS_".Length..];
                if (effective.SingleOrDefault(c => c.Kind == kind) is { } cutoff)
                {
                    var covering = at > cutoff.At
                        ? await db.CutoffExceptions.AsNoTracking().FirstOrDefaultAsync(e =>
                            e.BookingId == booking.BookingId && e.CutoffKind == kind && e.RevokedAt == null
                            && e.AllowedUntil >= at
                            && (e.EquipmentRequirementId == null || e.EquipmentRequirementId == assignment.EquipmentRequirementId), ct)
                        : null;

                    // Vector "Allow Late Gate-In": set on the booking by someone who may override cut-offs.
                    if (GateRules.LateGate(at, kind, cutoff.At, covering is not null || booking.AllowLateGateIn,
                            covering is null && booking.AllowLateGateIn ? "the booking's Allow Late Gate-In" : "an approved late gate") is { } late)
                        findings.Add(late);

                    return await FinishAsync(new BarrierView
                    {
                        ContainerNo = containerNo, Direction = direction, At = at, Findings = findings,
                        Booking = booking, Assignment = assignment, Step = step, StepRules = stepRules,
                        StepsToSkip = toSkip, Requirement = requirement, OpenVisit = openVisit, Holds = holds,
                        CutoffKind = kind, CutoffAt = cutoff.At, IsLate = at > cutoff.At, CoveringException = covering,
                        IsCheckDigitValid = checkDigitValid, Registry = registry,
                    }, ct);
                }
            }
        }

        return await FinishAsync(new BarrierView
        {
            ContainerNo = containerNo, Direction = direction, At = at, Findings = findings,
            Booking = booking, Assignment = assignment, Step = step, StepRules = stepRules,
            StepsToSkip = toSkip, Requirement = requirement, OpenVisit = openVisit, Holds = holds,
            IsCheckDigitValid = checkDigitValid, Registry = registry,
        }, ct);
    }

    /// <summary>The coupon (ADR-007) — read LOCALLY, which is the whole point of copying it here.</summary>
    private async Task<BarrierView> FinishAsync(BarrierView view, CancellationToken ct)
    {
        if (view.Booking is null || view.Step is null) return view;

        var coupon = await db.GateAuthorizations.AsNoTracking().FirstOrDefaultAsync(a =>
            a.BookingId == view.Booking.BookingId
            && (a.ContainerNo == null || a.ContainerNo == view.ContainerNo)
            && a.MovementCode == view.Step.MovementCode
            && a.ConsumedByGateTransactionId == null && a.RevokedAt == null
            && a.ValidFrom <= view.At && a.ValidUntil >= view.At, ct);

        // PLAN_BILLING §4.2 step 4: a BILLABLE movement moves on a coupon. Revenue
        // issues one when the cash is paid, or at once when nothing cash is due,
        // so the rule is the same for cash and credit. The barrier reads it here,
        // locally; it never asks Revenue whether the box is paid (ADR-007).
        if (coupon is null && view.StepRules is { IsBillable: true })
        {
            var enforce = await master.GetBoolSettingAsync(TosSettingKeys.RequireCouponForCash, view.Booking.BranchId, true, ct);
            view.Findings.Add(new GateFinding("NO_COUPON",
                enforce
                    ? $"{view.ContainerNo} {view.Step.MovementCode} on {view.Booking.OrderNo} is not paid. Send the driver to the cash window."
                    : $"{view.ContainerNo} {view.Step.MovementCode} on {view.Booking.OrderNo} has no coupon (warning only at this depot).",
                enforce ? GateSeverity.Block : GateSeverity.Info));
        }

        return view with { Coupon = coupon };
    }

    /// <summary>
    /// Which cut-off applies (Q2): DG outranks reefer, reefer outranks dry. The
    /// requirement line knows whether this box is dangerous or refrigerated;
    /// the booking's cargo class is the fallback.
    /// </summary>
    private static string CutoffKindFor(Booking booking, EquipmentRequirement? requirement)
    {
        if (IsDangerous(booking, requirement))
            return "YARD_DG";

        var cfs = booking.OrderTypeCode.Contains("CFS", StringComparison.OrdinalIgnoreCase);
        if (requirement?.ReeferSetTempC is not null
            || booking.CargoClassCode.Contains("REEFER", StringComparison.OrdinalIgnoreCase))
            return cfs ? "CFS_REEFER" : "YARD_REEFER";

        return cfs ? "CFS_DRY" : "YARD_DRY";
    }

    /// <summary>One definition of "this box is dangerous": the cut-off and the price must agree.</summary>
    public static bool IsDangerous(Booking booking, EquipmentRequirement? requirement) =>
        requirement?.UnNumber is { Length: > 0 } || requirement?.ImdgClass is { Length: > 0 }
        || booking.CargoClassCode.Contains("DG", StringComparison.OrdinalIgnoreCase);
}
