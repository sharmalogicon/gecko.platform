using Gecko.MasterData.Contracts;

namespace Gecko.Tos.Domain;

/// <summary>What the barrier decided, and why. One finding per reason, never a bare "refused".</summary>
public enum GateSeverity
{
    /// <summary>Worth saying, changes nothing.</summary>
    Info,

    /// <summary>Allowed, and the clerk may go ahead, but must be told first (gate hours: outside opening hours).</summary>
    Warn,

    /// <summary>Allowed, but only by someone who may override it, with a typed reason.</summary>
    Override,

    /// <summary>Refused. No override exists at the barrier (PLAN Q5).</summary>
    Block,
}

public sealed record GateFinding(string Code, string Message, GateSeverity Severity);

/// <summary>
/// The rules the barrier applies (PLAN §5.2, §5.3, Q5, Q6, Q8), as pure functions.
///
/// They are pure because the barrier has a 3-second budget and because these are
/// the decisions that get argued about afterwards: "why was my box refused" has
/// to be answerable from a test, not from a log.
/// </summary>
public static class GateRules
{
    public const string In = "IN";
    public const string Out = "OUT";
    public const string Full = "FULL";
    public const string Empty = "EMPTY";

    public static readonly IReadOnlySet<string> Directions = new HashSet<string>(StringComparer.Ordinal) { In, Out };

    /// <summary>A movement's own direction and load state decide the transaction's — not the clerk (§5.3).</summary>
    public static GateFinding? DirectionMatches(OrderTypeStepRef step, string requestedDirection) =>
        string.Equals(step.Direction, requestedDirection, StringComparison.Ordinal)
            ? null
            : new GateFinding(
                "WRONG_DIRECTION",
                $"The next step for this box is {step.MovementCode}, which goes {step.Direction}. This gate is {requestedDirection}.",
                GateSeverity.Block);

    /// <summary>
    /// A hold stops the move when its MDM blocking scope covers this direction
    /// (PLAN Q5 — a hard refusal; the only way out is a release, on record).
    /// </summary>
    /// <remarks>
    /// One exception, Vector's (GateOut.cs:1099-1109, 1272; owner 2026-10-01): a
    /// gate-OUT on a movement that releases damaged boxes (MDM allow_damaged_release)
    /// lets the DAMAGE hold through — said, not hidden. Only that one: every other
    /// hold still refuses (an expired CSC plate is technical too, and is not damage),
    /// and so does the damage hold on any other movement.
    /// </remarks>
    /// <param name="autoApplyOnEvent">The hold's MDM auto-apply event; SURVEY_DAMAGED marks the damage hold.</param>
    public static GateFinding? HoldBlocks(string holdCode, string? blockingScope, string? description, string direction,
        string? autoApplyOnEvent = null, bool movementReleasesDamaged = false)
    {
        if (blockingScope is null || !HoldRules.Blocks(blockingScope, direction)) return null;

        return ReleasedByMovement(autoApplyOnEvent, direction, movementReleasesDamaged)
            ? new GateFinding(
                "HOLD_RELEASED_BY_MOVEMENT",
                $"{holdCode} ({description ?? blockingScope}) is the damage hold, and this movement releases damaged boxes: it does not stop the box.",
                GateSeverity.Info)
            : new GateFinding(
                "HOLD",
                $"{holdCode} ({description ?? blockingScope}) stops this box moving {direction}. Release it first — there is no override at the barrier.",
                GateSeverity.Block);
    }

    /// <summary>
    /// What marks a hold as THE damage hold, whatever a tenant calls it (SCT: DAMAGE,
    /// KORAKIT: DMG): it is the one a damage survey applies (equipment.hold.auto_apply_on_event).
    /// </summary>
    public const string DamageSurveyEvent = "SURVEY_DAMAGED";

    /// <summary>The damage hold, going OUT, on a movement flagged to release damaged boxes.</summary>
    public static bool ReleasedByMovement(string? autoApplyOnEvent, string direction, bool movementReleasesDamaged) =>
        movementReleasesDamaged && direction == Out && string.Equals(autoApplyOnEvent, DamageSurveyEvent, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// §5.2: a FULL export box gating IN after its yard cut-off. Late is allowed,
    /// unaccounted-for late is not — Vector let 686 through on a bit in 2025.
    /// </summary>
    public static GateFinding? LateGate(DateTimeOffset transactionAt, string cutoffKind, DateTimeOffset cutoffAt, bool covered,
        string coveredBy = "an approved late gate") =>
        transactionAt <= cutoffAt
            ? null
            : covered
                ? new GateFinding(
                    "LATE_APPROVED",
                    $"Gated in after the {cutoffKind} cut-off ({cutoffAt:u}), covered by {coveredBy}.",
                    GateSeverity.Info)
                : new GateFinding(
                    "LATE",
                    $"The {cutoffKind} cut-off was {cutoffAt:u} and it is now {transactionAt:u}. A supervisor may let it in with a reason.",
                    GateSeverity.Override);

    /// <summary>Q6: 13 of Vector's 194K boxes fail the digit and still arrive. Allowed, never anonymously.</summary>
    /// <remarks>Owner 2026-10-08: a depot that does not enforce it hears nothing about it — no note at all.</remarks>
    public static GateFinding? CheckDigit(string containerNo, bool isValid, bool enforced, int? expected) =>
        isValid || !enforced
            ? null
            : new GateFinding(
                "CHECK_DIGIT",
                expected is null
                    ? $"{containerNo} is not an ISO 6346 number. A supervisor may accept it with a reason."
                    : $"{containerNo} fails the ISO 6346 check digit (expected {expected}). A supervisor may accept it with a reason.",
                GateSeverity.Override);

    /// <summary>Q8: a release that has expired is refused at the gate; extending it is an edit to the booking.</summary>
    public static GateFinding? Expired(string orderNo, DateOnly? validTo, DateOnly today) =>
        validTo is { } end && end < today
            ? new GateFinding(
                "BOOKING_EXPIRED",
                $"Booking {orderNo} was valid until {end:yyyy-MM-dd}. Extend the booking before the box can move.",
                GateSeverity.Block)
            : null;

    /// <summary>D-5: the yard has ONE answer. A box already inside cannot gate in again, and one outside cannot gate out.</summary>
    public static GateFinding? YardState(string containerNo, string direction, bool isInYard) => (direction, isInYard) switch
    {
        (In, true) => new GateFinding("ALREADY_IN_YARD",
            $"{containerNo} is already in the yard. If it left without an EIR, correct that first — the yard has one answer, not two.",
            GateSeverity.Block),
        (Out, false) => new GateFinding("NOT_IN_YARD",
            $"{containerNo} is not in this yard, so it cannot leave it.",
            GateSeverity.Block),
        _ => null,
    };

    /// <summary>
    /// The four yard rules (owner 2026-10-06; Vector GateIn.cs:2998-3017): a drop-off is NOT in any yard (one in
    /// another depot's yard is named, so it is gated out there first); a pick-up IS in this yard, and FULL or EMPTY
    /// there as the step says.
    /// </summary>
    /// <param name="yardBranchCode">The depot whose yard holds the box, when it is in one.</param>
    /// <param name="yardFullEmpty">What the open stay says the box is (FULL / EMPTY).</param>
    public static IEnumerable<GateFinding> Yard(string containerNo, string direction, string? stepFullEmpty,
        bool inYard, bool atThisDepot, string? yardBranchCode, string? yardFullEmpty)
    {
        if (direction == In && inYard)
            yield return atThisDepot
                ? YardState(containerNo, In, true)!
                : new GateFinding("ALREADY_IN_YARD",
                    $"{containerNo} is in the yard at {yardBranchCode ?? "another depot"}. Gate it out there before it comes in here.",
                    GateSeverity.Block);
        if (direction == Out && !(inYard && atThisDepot))
            yield return inYard
                ? new GateFinding("NOT_IN_YARD",
                    $"{containerNo} is in the yard at {yardBranchCode ?? "another depot"}, not here. It can only leave from there.",
                    GateSeverity.Block)
                : YardState(containerNo, Out, false)!;
        if (direction == Out && inYard && atThisDepot && stepFullEmpty is Full or Empty && yardFullEmpty is Full or Empty
            && yardFullEmpty != stepFullEmpty)
            yield return new GateFinding("LOAD_MISMATCH",
                $"{containerNo} is {yardFullEmpty} in the yard; this is a {stepFullEmpty} pick-up.",
                GateSeverity.Block);
    }

    /// <summary>
    /// Vector GateIn.cs:3025 / GateOut.cs:1030 (owner 2026-10-06: refuse): a box whose registry owner is known and is not
    /// the booking's line (the box operator) does not move on that booking.
    /// </summary>
    public static GateFinding? OwnerMismatch(string containerNo, Guid? boxOwner, Guid bookingLine, string lineCode, string orderNo, bool refuse = true) =>
        boxOwner is { } owner && owner != bookingLine
            ? refuse
                ? new GateFinding("OWNER_MISMATCH",
                    $"{containerNo} belongs to another owner than {orderNo}'s line ({lineCode}). It cannot move on this booking.", GateSeverity.Block)
                : new GateFinding("OWNER_MISMATCH",
                    $"{containerNo} belongs to another owner than {orderNo}'s line ({lineCode}).", GateSeverity.Warn)
            : null;

    // ── gate-out release gates (gate-in-vector-parity Part B §B2) ───────────────

    /// <summary>
    /// B2.2 (Vector GateOut.cs:1112-1127): a container designated to certain ports
    /// (the registry's fixed-port list) leaves on an EXPORT booking only when the
    /// booking's discharge port is one of them. A box with no list goes anywhere.
    /// </summary>
    public static GateFinding? FixedPort(string containerNo, IReadOnlyCollection<string>? fixedPortCodes, string orderNo, string? podPortCode) =>
        fixedPortCodes is not { Count: > 0 } || (podPortCode is not null && fixedPortCodes.Contains(podPortCode, StringComparer.OrdinalIgnoreCase))
            ? null
            : new GateFinding("FIXED_PORT",
                $"{containerNo} is designated to {string.Join(", ", fixedPortCodes)} only, and {orderNo} discharges at {podPortCode ?? "no port"}. " +
                "Pick another box, or change the container's fixed ports in master data.",
                GateSeverity.Block);

    /// <summary>
    /// B2.3 (Vector GateOut.cs:1139): a gate-out must be LATER than the box's
    /// previous recorded move. A backdated gate-out that lands on or before it is
    /// refused — the yard's history would run backwards.
    /// </summary>
    public static GateFinding? BeforePreviousMove(string containerNo, DateTimeOffset at, DateTimeOffset previousMoveAt) =>
        at > previousMoveAt
            ? null
            : new GateFinding("BEFORE_PREVIOUS_MOVE",
                $"{containerNo}'s previous move is recorded at {previousMoveAt:u}. A gate-out at {at:u} must be later than that.",
                GateSeverity.Block);

    /// <summary>
    /// B2.4 (Vector GateOut.cs:1149, 1306): a FULL EXPORT box may not leave before
    /// its vessel call's laden release date. No date is no restriction — the rule
    /// fails open, so a call nobody dated never strands a truck.
    /// </summary>
    public static GateFinding? BeforeLadenRelease(string containerNo, string? callRef, DateTimeOffset at, DateTimeOffset? ladenReleaseAt) =>
        ladenReleaseAt is not { } release || at >= release
            ? null
            : new GateFinding("BEFORE_LADEN_RELEASE",
                $"{containerNo} is a full export box and {callRef ?? "its vessel call"} releases laden boxes from {release:u}. It cannot leave before then.",
                GateSeverity.Block);

    /// <summary>The gate rules the MDM step switched on, checked against what the clerk actually recorded.</summary>
    public static IEnumerable<GateFinding> Observations(OrderTypeStepRef step, decimal? grossWeightKg, int sealCount)
    {
        if (step.CheckGrossWeight && grossWeightKg is null or <= 0)
            yield return new GateFinding("WEIGHT_REQUIRED",
                $"{step.MovementCode} weighs the box. Record the gross weight — Vector has 374,000 lines with a zero in it.",
                GateSeverity.Block);

        if (step.CheckSealNo && sealCount == 0)
            yield return new GateFinding("SEAL_REQUIRED",
                $"{step.MovementCode} checks the seal. Record at least one seal number.",
                GateSeverity.Block);
    }

    /// <summary>
    /// Gate hours (TIER3_DESIGN_NOTES §1): a move while the depot's gate is shut is
    /// a WARNING, never a refusal (owner decision 2026-09-30) — the clerk is told
    /// when it opens next and may still proceed. A depot with no gate hours
    /// (<paramref name="status"/> null) and an open gate say nothing.
    /// </summary>
    public static GateFinding? OutsideHours(GateHoursStatus? status)
    {
        if (status is null || status.IsOpen) return null;
        var next = status.NextOpensAt is { } n
            ? $" It opens next {n.ToString("ddd d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture)}."
            : " No opening is set in the next 14 days.";
        var why = status.State switch
        {
            "HOLIDAY" => $"Today is a public holiday ({status.Note}) and the gate is closed.",
            "CLOSED_DATE" => $"The gate is closed today ({status.Note}).",
            _ => $"The gate is outside its opening hours ({status.LocalAt.ToString("ddd HH:mm", System.Globalization.CultureInfo.InvariantCulture)}).",
        };
        return new GateFinding(status.State == "OUTSIDE_HOURS" ? "OUTSIDE_GATE_HOURS" : "GATE_CLOSED_DAY",
            why + next + " The move may still go ahead.", GateSeverity.Warn);
    }

    // ── trip type (Vector Gate In parity, gate-in-vector-parity.md §2) ─────────

    public const string DropOffContainer = "DROP_OFF_CONT";
    public const string PickUpContainer = "PICK_UP_CONT";

    /// <summary>
    /// Vector's trip type (lookup TruckActivityIndicator, bound at GateIn.cs:329 and
    /// GateOut.cs:242) and the direction it implies: "DROP-OFF CONT" is the IN
    /// movement indicator and everything else OUT (GateIn.cs:2191); Gate Out
    /// selects "PICK-UP CONT" for its moves (GateOut.cs:1461). The two CARGO trips
    /// of that lookup (DROP-OFF / PICK-UP CARGO, GateIn.cs:2218-2223) move cargo,
    /// not a container, so they are not gate-transaction trips here — KORAKIT has
    /// none of them in 552,132 truck movement lines. The SQL CHECK
    /// ck_gate_transaction__trip_type (gecko_tos 15) pins the same pairs.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TripTypeDirections =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DropOffContainer] = In,
            [PickUpContainer] = Out,
        };

    /// <summary>
    /// What the TRUCK did on a visit, in the PICKUP_DROPOFF_MODE vocabulary (gecko_master 12),
    /// DERIVED from its recorded moves and never stored or declared: the trip type is the
    /// move's, the mode is the visit's, and a second statement of it would be a second truth.
    /// Only moves that stand count (a voided EIR never happened).
    /// </summary>
    public static string VisitMode(int movesIn, int movesOut) => (movesIn > 0, movesOut > 0) switch
    {
        (true, true) => "PICKUP_DROPOFF",
        (true, false) => "DROPOFF",
        (false, true) => "PICKUP",
        _ => "NONE",
    };

    /// <summary>A trip type that says the opposite of the direction is a contradiction, not a hint.</summary>
    public static string? TripTypeContradiction(string tripType, string direction) =>
        TripTypeDirections.TryGetValue(tripType, out var expected) && expected != direction
            ? $"{tripType} is a {(expected == In ? "gate-in" : "gate-out")} trip; this transaction is {direction}."
            : null;

    /// <summary>
    /// The mandatory-field matrix of Vector's SetMandatoryByTrip (GateIn.cs:146-197,
    /// gate-in-vector-parity.md §2.1), enforced on EVERY gate transaction (owner,
    /// 2026-10-01 — the trip type itself is required, so this always runs):
    /// <list type="bullet">
    /// <item>DROP-OFF CONT → tare weight, max gross weight (GateIn.cs:171-177)</item>
    /// <item>DROP-OFF CONT + FULL → cargo weight, seal 1 (GateIn.cs:179-188)</item>
    /// <item>DROP-OFF CONT + FULL + booking type EXPORT → customs permit no (GateIn.cs:181-182)</item>
    /// <item>PICK-UP CONT → nothing extra (the box comes from the yard)</item>
    /// </list>
    /// Agent, booking and customer are the booking's own (the barrier refuses a box
    /// with no booking), and the container number is always required by the request.
    /// Keys are the request fields, so the 400 lands on the right input.
    /// </summary>
    public static Dictionary<string, string[]> MissingForTrip(
        string tripType, string fullEmpty, bool isExport,
        decimal? tareWeightKg, decimal? maxGrossWeightKg, decimal? cargoWeightKg, string? customsPermitNo, int sealCount)
    {
        var errors = new Dictionary<string, string[]>();
        if (tripType != DropOffContainer) return errors;   // PICK-UP: nothing extra (GateIn.cs:171 is the only branch)

        if (tareWeightKg is null or <= 0)
            errors["tareWeightKg"] = ["A drop-off records the box's tare weight."];
        if (maxGrossWeightKg is null)
            errors["maxGrossWeightKg"] = ["A drop-off records the box's max gross weight."];
        if (fullEmpty == Full)
        {
            if (cargoWeightKg is null)
                errors["cargoWeightKg"] = ["A full drop-off records the cargo weight."];
            if (sealCount == 0)
                errors["seals"] = ["A full drop-off records at least one seal."];
            if (isExport && string.IsNullOrWhiteSpace(customsPermitNo))
                errors["customsPermitNo"] = ["A full export drop-off records the customs permit number."];
        }
        return errors;
    }

    // ── the truck against what was paid (GATE_CHARGING_DESIGN §7.4, §7.5) ──────

    /// <summary>
    /// The coupon carries the truck category and the haulier the window priced with
    /// (gecko_tos 16). A truck of another category is a WARNING — the gate charge is
    /// priced on it — and another haulier is INFO: the cash already paid stands and
    /// the credit side is billed on the gate's haulier (owner 2026-10-01). Neither
    /// refuses the box. A null is "none": the any-truck rate, no haulier.
    /// A coupon no cash was taken on (automatic, waived) was priced on no truck
    /// category, so only its haulier is compared: another haulier may owe cash.
    /// </summary>
    public static IEnumerable<GateFinding> NotAsPaid(string couponRef, bool cashPaid, string? paidTruckCategory, string? truckCategory,
        string? paidHaulier, string? haulier)
    {
        if (cashPaid && !string.Equals(paidTruckCategory, truckCategory, StringComparison.OrdinalIgnoreCase))
            yield return new GateFinding("TRUCK_CATEGORY_NOT_AS_PAID",
                $"This truck is {truckCategory ?? "of no category"}; coupon {couponRef} was priced for {paidTruckCategory ?? "any truck"}. " +
                "The gate charge may differ. The cash already paid stands.",
                GateSeverity.Warn);

        if (!string.Equals(paidHaulier, haulier, StringComparison.OrdinalIgnoreCase))
            yield return new GateFinding("HAULIER_NOT_AS_PAID",
                $"This truck's haulier is {haulier ?? "not given"}; coupon {couponRef} was priced for {paidHaulier ?? "no haulier"}. " +
                "The cash already paid stands; what is billed later goes by the haulier at the gate.",
                GateSeverity.Info);
    }

    /// <summary>The one decision, from every finding.</summary>
    public static string Decide(IReadOnlyCollection<GateFinding> findings) =>
        findings.Any(f => f.Severity == GateSeverity.Block) ? "BLOCKED"
        : findings.Any(f => f.Severity == GateSeverity.Override) ? "NEEDS_OVERRIDE"
        : "ALLOWED";

    /// <summary>Steps before the one being done: required steps block, optional ones are skipped on record (§5.3).</summary>
    public const string SkipReason = "SUPERSEDED_BY_GATE";
}
