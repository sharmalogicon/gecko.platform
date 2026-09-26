namespace Gecko.SharedKernel;

/// <summary>
/// The JWT claim contract (ADR-006). Every module reads these, so they live in
/// the shared kernel rather than in Identity.
///
///   { sub, tid, typ:"operator", brn:[branchIds], mod:["TOS","NOTIFICATION"], rol:[...] }
/// </summary>
public static class GeckoClaimTypes
{
    public const string UserId = "sub";
    public const string TenantId = "tid";
    public const string UserType = "typ";
    public const string Branch = "brn";
    public const string Module = "mod";
    public const string Role = "rol";
    public const string Permission = "prm";

    /// <summary>Permissions held only at named branches — see <see cref="BranchPermissionClaim"/>.</summary>
    public const string BranchPermission = "bpm";
}
