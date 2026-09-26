using Gecko.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Endpoints;

/// <summary>Permission codes as seeded in iam.permission (gecko_identity 16_tos_permissions.sql).</summary>
internal static class TosPermissions
{
    public const string VesselView = "tos.vessel.view";
    public const string VesselManage = "tos.vessel.manage";
    public const string BookingView = "tos.booking.view";
    public const string BookingManage = "tos.booking.manage";

    /// <summary>Separate from manage: cancelling releases every box on the booking (16_tos_permissions.sql).</summary>
    public const string BookingCancel = "tos.booking.cancel";

    public const string HoldView = "tos.hold.view";
    public const string HoldApply = "tos.hold.apply";

    /// <summary>Approving a late gate BEFORE the truck arrives (PLAN §4.2, D-2) — and at the barrier.</summary>
    public const string CutoffOverride = "tos.cutoff.override";

    public const string GateView = "tos.gate.view";
    public const string GateCreate = "tos.gate.create";

    /// <summary>
    /// The supervisor act at the barrier: accepting a container number whose ISO 6346
    /// check digit fails, and voiding an EIR (gecko_identity 17_gate_permissions.sql).
    /// GATE_CLERK does not hold it — the person who types the number is not the person
    /// who approves it being wrong.
    /// </summary>
    public const string GateOverride = "tos.gate.override";
}

/// <summary>
/// Branch scoping (PLAN Q11). A TOS permission may be held tenant-wide (<c>prm</c>)
/// or only at named branches (<c>bpm</c>) — a gate clerk works at one depot.
/// <c>RequireBranchPermission</c> is only the door; these helpers are the rows.
/// </summary>
internal static class TosScope
{
    /// <summary>True when the caller holds the permission over every branch — no row filter needed.</summary>
    public static bool IsTenantWide(this ICallerPermissions caller, string permission) => caller.HasTenantWide(permission);

    /// <summary>
    /// The branches to filter a list by, or <c>null</c> for "no filter" (tenant-wide).
    /// An empty set means the caller passed the door on another permission and sees nothing here.
    /// </summary>
    public static IReadOnlyCollection<Guid>? BranchFilter(this ICallerPermissions caller, string permission) =>
        caller.BranchesFor(permission);

    /// <summary>
    /// A write aimed at a branch the caller does not cover. 403, not 404: the caller
    /// named the branch themselves, so the answer leaks nothing they did not already type.
    /// </summary>
    public static ProblemHttpResult OutsideYourBranches(string detail) =>
        TypedResults.Problem(
            title: "That branch is outside your access.",
            detail: detail,
            statusCode: StatusCodes.Status403Forbidden);
}

internal static class TosSupport
{
    public static Guid TenantId(this ITenantContext caller) =>
        caller.TenantId ?? throw new InvalidOperationException("TOS endpoints require an authenticated tenant.");

    public static Guid UserId(this ITenantContext caller) =>
        caller.UserId ?? throw new InvalidOperationException("TOS endpoints require an authenticated user.");

    public static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    public static ValidationProblem Invalid(IDictionary<string, List<string>> errors) =>
        TypedResults.ValidationProblem(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));

    public static ProblemHttpResult Conflict(string title, string? detail = null) =>
        TypedResults.Problem(title: title, detail: detail, statusCode: StatusCodes.Status409Conflict);

    public static void Add(this IDictionary<string, List<string>> errors, string field, string message)
    {
        if (!errors.TryGetValue(field, out var list)) errors[field] = list = [];
        list.Add(message);
    }

    /// <summary>Same contract as MasterData and Revenue: a stale rowVersion is a 409, never last-write-wins.</summary>
    public static bool TrySetExpectedVersion<TEntity>(this DbContext db, TEntity entity, string? base64RowVersion)
        where TEntity : class
    {
        if (string.IsNullOrWhiteSpace(base64RowVersion)) return false;

        Span<byte> buffer = stackalloc byte[16];
        if (!Convert.TryFromBase64String(base64RowVersion, buffer, out var written) || written == 0) return false;

        db.Entry(entity).Property("RowVersion").OriginalValue = buffer[..written].ToArray();
        return true;
    }

    public static async Task<ProblemHttpResult?> SaveOrConflictAsync(this DbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(
                "The record changed since you loaded it.",
                "Re-read it and re-apply your change. The rowVersion you sent is no longer current.");
        }
    }

    public static string? Clean(this string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}
