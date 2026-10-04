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

    // Booking types: Vector's four (owner 2026-10-04, gecko_master 29).
    public const string Import = "IMPORT";
    public const string Export = "EXPORT";
    public const string Repo = "REPO";
    public const string Internal = "INTERNAL";

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

    // ── who picks up / drops off the box (Vector P/U Mode / D/O Mode, BookingEntry.cs) ──

    /// <summary>
    /// Vector's lookups, by booking direction (BookingEntry.cs:436-441): DropOffMode for an
    /// IMPORT, PickupMode for an EXPORT, RepoMode for everything else. The SQL CHECK
    /// ck_booking_container__handover_mode (gecko_tos 18) pins the same ten codes.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> HandoverModes = new Dictionary<string, string[]>
    {
        ["IMPORT"] = ["DO_OWN", "DO_OTHER", "DO_ONLY", "DO_CUS"],
        ["EXPORT"] = ["PU_OWN", "PU_OTHER", "PU_ONLY", "PU_PORT"],
        ["REPO"] = ["REPO_OWN", "REPO_OTHER"],
    };

    /// <summary>Which of Vector's three lists a booking's direction selects.</summary>
    public static string HandoverFamily(string? directionCode) =>
        directionCode?.Contains("IMPORT", StringComparison.OrdinalIgnoreCase) == true ? "IMPORT"
        : directionCode?.Contains("EXPORT", StringComparison.OrdinalIgnoreCase) == true ? "EXPORT"
        : "REPO";

    /// <summary>A mode from another booking type's list (a D/O mode on an export) is a contradiction, not a choice.</summary>
    public static string? HandoverModeProblem(string? directionCode, string mode)
    {
        var family = HandoverFamily(directionCode);
        return HandoverModes[family].Contains(mode, StringComparer.Ordinal)
            ? null
            : $"'{mode}' is not a mode of this booking. Use one of: {string.Join(", ", HandoverModes[family])}.";
    }

    /// <summary>
    /// Vector's booking checks, with the two modes that relax them (BookingEntry.cs:626-655):
    /// an EXPORT box must be in this yard and EMPTY — unless it is picked up elsewhere
    /// (PU_OTHER); an IMPORT box must not already be in this yard — unless the customer
    /// drops it off (DO_CUS). They apply only when a mode is SAID: a box assigned with
    /// no mode is judged as before this field existed.
    /// </summary>
    /// <param name="inYardHere">The box has an open stay at the booking's depot.</param>
    /// <param name="fullEmptyInYard">FULL / EMPTY of that stay; null when it is not here.</param>
    public static string? HandoverRefusal(string? directionCode, string mode, bool inYardHere, string? fullEmptyInYard) =>
        (HandoverFamily(directionCode), mode, inYardHere) switch
        {
            ("EXPORT", not "PU_OTHER", false) => "is not in this yard. An export box is picked up from the yard — use PU_OTHER if it is collected elsewhere.",
            ("EXPORT", not "PU_OTHER", true) when fullEmptyInYard != "EMPTY" => "is not EMPTY in the yard. Only an empty box is released to an export booking.",
            ("IMPORT", not "DO_CUS", true) => "is already in this yard. An import box arrives on its booking — use DO_CUS if the customer drops it off.",
            _ => null,
        };

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

    /// <summary>
    /// What the desktop makes mandatory on the header (Vector BookingEntry.cs:168-207, 2548), on
    /// Vector's four booking types (owner 2026-10-04): the customer on every booking; the loading
    /// and destination ports on an IMPORT or EXPORT whose order type requires a vessel schedule.
    /// </summary>
    public static IReadOnlyList<(string Field, string Message)> MissingHeaderFields(
        string? bookingTypeCode, bool requiresVesselSchedule, bool hasCustomer, bool hasLoadingPort, bool hasDestinationPort)
    {
        var missing = new List<(string, string)>();
        if (bookingTypeCode is null) return missing;   // unknown order type: reported on its own
        if (!hasCustomer) missing.Add(("customerCode", "The customer is required on every booking."));
        if (requiresVesselSchedule && bookingTypeCode is Import or Export)
        {
            var kind = bookingTypeCode == Import ? "an import" : "an export";
            if (!hasLoadingPort) missing.Add(("polPortCode", $"The loading port is required on {kind} booking."));
            if (!hasDestinationPort) missing.Add(("fpdPortCode", $"The destination port is required on {kind} booking."));
        }
        return missing;
    }
}
