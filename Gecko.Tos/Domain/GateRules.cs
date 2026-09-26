using Gecko.MasterData.Contracts;

namespace Gecko.Tos.Domain;

/// <summary>What the barrier decided, and why. One finding per reason, never a bare "refused".</summary>
public enum GateSeverity
{
    /// <summary>Worth saying, changes nothing.</summary>
    Info,

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
    public static GateFinding? HoldBlocks(string holdCode, string? blockingScope, string? description, string direction) =>
        blockingScope is not null && HoldRules.Blocks(blockingScope, direction)
            ? new GateFinding(
                "HOLD",
                $"{holdCode} ({description ?? blockingScope}) stops this box moving {direction}. Release it first — there is no override at the barrier.",
                GateSeverity.Block)
            : null;

    /// <summary>
    /// §5.2: a FULL export box gating IN after its yard cut-off. Late is allowed,
    /// unaccounted-for late is not — Vector let 686 through on a bit in 2025.
    /// </summary>
    public static GateFinding? LateGate(DateTimeOffset transactionAt, string cutoffKind, DateTimeOffset cutoffAt, bool covered) =>
        transactionAt <= cutoffAt
            ? null
            : covered
                ? new GateFinding(
                    "LATE_APPROVED",
                    $"Gated in after the {cutoffKind} cut-off ({cutoffAt:u}), covered by an approved late gate.",
                    GateSeverity.Info)
                : new GateFinding(
                    "LATE",
                    $"The {cutoffKind} cut-off was {cutoffAt:u} and it is now {transactionAt:u}. A supervisor may let it in with a reason.",
                    GateSeverity.Override);

    /// <summary>Q6: 13 of Vector's 194K boxes fail the digit and still arrive. Allowed, never anonymously.</summary>
    public static GateFinding? CheckDigit(string containerNo, bool isValid, bool enforced, int? expected) =>
        isValid
            ? null
            : enforced
                ? new GateFinding(
                    "CHECK_DIGIT",
                    $"{containerNo} fails the ISO 6346 check digit (expected {expected}). A supervisor may accept it with a reason.",
                    GateSeverity.Override)
                : new GateFinding(
                    "CHECK_DIGIT_RECORDED",
                    $"{containerNo} fails the ISO 6346 check digit (expected {expected}); the depot does not enforce it, so it is recorded and allowed.",
                    GateSeverity.Info);

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

    /// <summary>The one decision, from every finding.</summary>
    public static string Decide(IReadOnlyCollection<GateFinding> findings) =>
        findings.Any(f => f.Severity == GateSeverity.Block) ? "BLOCKED"
        : findings.Any(f => f.Severity == GateSeverity.Override) ? "NEEDS_OVERRIDE"
        : "ALLOWED";

    /// <summary>Steps before the one being done: required steps block, optional ones are skipped on record (§5.3).</summary>
    public const string SkipReason = "SUPERSEDED_BY_GATE";
}
