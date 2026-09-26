namespace Gecko.Tos.Domain;

/// <summary>
/// Who may lift a hold, and what a hold stops (PLAN §4.3, D-6, §10.7).
///
/// MDM says WHY a hold exists and WHO owns it (<c>release_authority</c>); Identity
/// says what a user may do. This is the map between them, and it is a pure
/// function so the rule is tested without a token or a database.
///
/// Vector had neither: <c>IsHold</c> is a bit, so anyone with the screen could
/// clear a customs hold and leave nothing behind — 1,483 held boxes had already
/// gated out.
/// </summary>
public static class HoldRules
{
    public const string ReleaseOperations = "tos.hold.release.operations";
    public const string ReleaseFinance = "tos.hold.release.finance";
    public const string ReleaseLine = "tos.hold.release.line";

    public static readonly IReadOnlySet<string> Sources = new HashSet<string>(StringComparer.Ordinal) { "MANUAL", "EDI", "AUTO", "MIGRATED" };

    /// <summary>MDM <c>equipment.hold.blocking_scope</c> values.</summary>
    public static readonly IReadOnlySet<string> BlockingScopes = new HashSet<string>(StringComparer.Ordinal)
    {
        "ALL", "RELEASE", "LOAD", "GATE_IN", "GATE_OUT",
    };

    /// <summary>
    /// What a depot user needs to release a hold of this authority, and whether they
    /// must cite the document that authorises it.
    ///
    /// CUSTOMS and MNR are NOT the depot's to decide: customs releases arrive as a
    /// customs document and a damage hold is lifted when the repair is accepted. The
    /// EDI and MnR modules will own those. Until they exist, an operations user may
    /// RECORD the release — but only by quoting the reference, so the paper behind it
    /// is named on the row instead of being a shrug (PLAN Q-C1, open).
    /// </summary>
    public static (string Permission, bool ReferenceRequired) ReleaseRule(string releaseAuthority) => releaseAuthority switch
    {
        "DEPOT_FINANCE" => (ReleaseFinance, false),
        "LINE" => (ReleaseLine, false),
        "CUSTOMS" => (ReleaseOperations, true),
        "MNR" => (ReleaseOperations, true),
        _ => (ReleaseOperations, false),   // SUPERVISOR, DEPOT_OPERATIONS, and anything MDM adds
    };

    /// <summary>Why this authority needs a reference — shown to the user, not just logged.</summary>
    public static string? ReferenceReason(string releaseAuthority) => releaseAuthority switch
    {
        "CUSTOMS" => "A customs hold is lifted by customs. Quote the customs release reference you are acting on.",
        "MNR" => "A damage hold is lifted when the repair is accepted. Quote the repair or survey reference.",
        _ => null,
    };

    /// <summary>Does a hold with this scope stop this movement? Used at the barrier (Phase 5) and to warn now.</summary>
    public static bool Blocks(string blockingScope, string movementDirection) => blockingScope switch
    {
        "ALL" => true,
        "GATE_IN" => movementDirection is "IN",
        "GATE_OUT" => movementDirection is "OUT",
        "RELEASE" or "LOAD" => movementDirection is "OUT",
        _ => false,
    };
}
