namespace Gecko.Revenue.Domain;

/// <summary>
/// The workflow of one tariff VERSION, and what it means on a given day.
///
///   DRAFT ──submit──▶ PENDING ──approve──▶ APPROVED   (immutable from here)
///     │                  ├──reject───▶ REJECTED
///     └──withdraw──┬─────┘
///                  ▼
///              WITHDRAWN
///
/// Only DRAFT is editable. A price change to an APPROVED version is a new
/// version (PLAN.md §4.1): what was agreed on 3 October stays readable as it
/// was, without a temporal query.
///
/// ACTIVE / SCHEDULED / EXPIRED / SUPERSEDED are never stored — they depend on
/// the day and on later versions, and a stored copy would need a job to keep
/// it true (the identity three-state lesson).
/// </summary>
public static class ScheduleLifecycle
{
    public const string Scheduled = "SCHEDULED";
    public const string Active = "ACTIVE";
    public const string Expired = "EXPIRED";
    public const string Superseded = "SUPERSEDED";

    public static bool IsEditable(string status) => status == ScheduleStatuses.Draft;
    public static bool CanSubmit(string status) => status == ScheduleStatuses.Draft;
    public static bool CanDecide(string status) => status == ScheduleStatuses.Pending;
    public static bool CanWithdraw(string status) => status is ScheduleStatuses.Draft or ScheduleStatuses.Pending;

    /// <summary>
    /// What a version is on <paramref name="today"/> — a BRANCH-local date.
    /// <paramref name="effectiveUntil"/> and <paramref name="fullySuperseded"/>
    /// come from tariff.vw_schedule_effective and only exist for APPROVED rows.
    /// </summary>
    public static string Describe(string status, DateOnly effectiveFrom, DateOnly? effectiveUntil, bool fullySuperseded, DateOnly today)
    {
        if (status != ScheduleStatuses.Approved) return status;
        if (fullySuperseded) return Superseded;
        if (today < effectiveFrom) return Scheduled;
        if (effectiveUntil is { } until && today > until) return Expired;
        return Active;
    }

    /// <summary>
    /// Maker-checker (PLAN.md Q7): the approver is neither the person who drafted
    /// the version nor the person who asked for its approval. Unknown makers
    /// (fixture or migrated rows, created_by NULL) do not block approval.
    /// </summary>
    public static bool IsIndependentApprover(Guid approver, Guid? createdBy, Guid? submittedBy) =>
        approver != createdBy && approver != submittedBy;
}

/// <summary>
/// scope_rank as the database computes it (04_tariff_schedule.sql), for
/// callers that need it before a row exists. A test holds the two together.
/// Lower wins: the most specific agreement prices the shipment.
/// </summary>
public static class ScheduleScope
{
    public static byte Rank(string scheduleType, bool hasBranch, bool hasAgent, bool hasForwarder, bool hasCustomer, bool hasBooking) =>
        (scheduleType, hasAgent, hasForwarder, hasCustomer) switch
        {
            (ScheduleTypes.Spot, _, _, _) when hasBooking => 1,
            (ScheduleTypes.Public, _, _, _) => hasBranch ? (byte)9 : (byte)10,
            (_, true, true, true) => 2,
            (_, true, false, true) => 3,
            (_, false, true, true) => 4,
            (_, false, false, true) => 5,
            (_, true, true, false) => 6,
            (_, true, false, false) => 7,
            (_, false, true, false) => 8,
            _ => 99,
        };
}
