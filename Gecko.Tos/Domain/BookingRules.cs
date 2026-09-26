using Gecko.MasterData.Contracts;

namespace Gecko.Tos.Domain;

/// <summary>
/// Booking rules that are decisions, not database facts (PLAN §4.2, §5.1, §10),
/// as pure functions.
/// </summary>
public static class BookingRules
{
    // Stored status — only what a person decides.
    public const string Open = "OPEN";
    public const string Cancelled = "CANCELLED";
    public const string Closed = "CLOSED";

    // Derived progress (booking.vw_booking_progress + EXPIRED, which needs the branch's calendar).
    public const string NotStarted = "NOT_STARTED";
    public const string InProgress = "IN_PROGRESS";
    public const string Completed = "COMPLETED";
    public const string Expired = "EXPIRED";

    public static readonly IReadOnlySet<string> Statuses = new HashSet<string> { Open, Cancelled, Closed };
    public static readonly IReadOnlySet<string> Progresses = new HashSet<string> { NotStarted, InProgress, Completed, Expired, Cancelled, Closed };

    /// <summary>
    /// EXPIRED is not in the view: the database does not know a branch's time
    /// zone. An open booking whose release validity ended BEFORE today (the
    /// depot's today) and that is not finished is expired — the gate will refuse
    /// it (Q8). A completed one is simply completed.
    /// </summary>
    public static string EffectiveProgress(string viewProgress, DateOnly? validTo, DateOnly branchToday) =>
        viewProgress is NotStarted or InProgress && validTo is { } to && to < branchToday ? Expired : viewProgress;

    /// <summary>
    /// The steps a newly assigned box must go through: the order type's list as
    /// it stands NOW, snapshotted — a depot that edits EXP CY/CY in October does
    /// not change the path of a box already half-way through (§4.2).
    /// </summary>
    public static IReadOnlyList<OrderTypeStepRef> PlanFor(OrderTypePlanRef orderType) =>
        orderType.Steps.OrderBy(s => s.SequenceNo).ToList();

    /// <summary>
    /// Why a box cannot go onto a requirement line, or null. <paramref name="usedOnLine"/>
    /// counts boxes active now or finished on that line — an unassigned box frees its place.
    /// </summary>
    public static string? CannotAssign(int qty, int usedOnLine, string requirementType, string? registryType)
    {
        if (registryType is not null && !string.Equals(registryType, requirementType, StringComparison.OrdinalIgnoreCase))
            return $"The registry says this box is a {registryType}; the line asks for {requirementType}.";
        if (usedOnLine >= qty)
            return $"The line is full: {qty} box(es) asked for, {usedOnLine} already on it. Raise the quantity first.";
        return null;
    }

    /// <summary>Q10: a quantity may not drop below the boxes already on the line.</summary>
    public static string? CannotSetQty(int newQty, int usedOnLine) =>
        newQty < usedOnLine
            ? $"{usedOnLine} box(es) are already on this line; unassign some before lowering the quantity to {newQty}."
            : null;

    /// <summary>Q7: once a box has done a step the booking has history — close it, do not cancel it.</summary>
    public static string? CannotCancel(string status, int stepsDone) =>
        status != Open ? $"Only an OPEN booking can be cancelled; this one is {status}."
        : stepsDone > 0 ? $"{stepsDone} gate step(s) are already done. Close the booking instead — cancelling would pretend they never happened."
        : null;
}
