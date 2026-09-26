namespace Gecko.SharedKernel;

/// <summary>
/// The <c>bpm</c> claim — permissions a user holds only AT NAMED BRANCHES
/// (gecko_tos PLAN Q11). <c>prm</c> stays what it always was: tenant-wide grants.
///
/// Wire format, one claim per distinct permission set:
/// <code>tos.booking.view,tos.booking.manage@3f1c…,8a02…</code>
/// Left of the last '@' is WHAT, right of it is WHERE — the same convention the
/// <c>rol</c> claim already uses (<c>OPS_MANAGER@branchId</c>).
///
/// Grouped, not one claim per pair, because the pair form does not fit: SCT's
/// OPS_MANAGER carries 40 permissions, so a manager over three branches would
/// add ~6.7 KB of claims to every request and run into the 8 KB header limit.
/// Branches that share a permission set share one claim, which is the normal
/// case (the same role granted at several depots).
/// </summary>
public static class BranchPermissionClaim
{
    public static string Format(IEnumerable<string> permissions, IEnumerable<Guid> branches) =>
        $"{string.Join(',', permissions)}@{string.Join(',', branches)}";

    /// <summary>Parses one claim value. A malformed claim grants nothing — it never throws.</summary>
    public static bool TryParse(string? value, out string[] permissions, out Guid[] branches)
    {
        permissions = [];
        branches = [];
        if (string.IsNullOrWhiteSpace(value)) return false;

        var at = value.LastIndexOf('@');
        if (at <= 0 || at == value.Length - 1) return false;

        permissions = value[..at].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var ids = new List<Guid>();
        foreach (var part in value[(at + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Guid.TryParse(part, out var id)) ids.Add(id);

        branches = [.. ids];
        return permissions.Length > 0 && branches.Length > 0;
    }
}

/// <summary>
/// What the caller may do, and where. Built from the access token's <c>prm</c>
/// and <c>bpm</c> claims and nothing else — a branch in a query string is a
/// request, never an authorization.
/// </summary>
public interface ICallerPermissions
{
    /// <summary>Held over the whole tenant. This is what a tenant-wide endpoint requires.</summary>
    bool HasTenantWide(string permission);

    /// <summary>Held tenant-wide OR at one or more branches — the door check for a branch-scoped endpoint.</summary>
    bool HasAnywhere(string permission);

    /// <summary>Held for THIS branch's rows.</summary>
    bool HasAt(string permission, Guid branchId);

    /// <summary>The branches the permission covers, or <c>null</c> for "every branch" (a tenant-wide grant).</summary>
    IReadOnlyCollection<Guid>? BranchesFor(string permission);
}

/// <summary>The immutable answer for one request. Kept out of Gecko.Data so it can be unit-tested without HTTP.</summary>
public sealed class CallerPermissions : ICallerPermissions
{
    private readonly HashSet<string> _tenantWide;
    private readonly Dictionary<string, HashSet<Guid>> _byBranch;

    public static readonly CallerPermissions None = new([], []);

    public CallerPermissions(IEnumerable<string> tenantWide, IEnumerable<string> branchClaims)
    {
        _tenantWide = new HashSet<string>(tenantWide, StringComparer.Ordinal);
        _byBranch = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);

        foreach (var claim in branchClaims)
        {
            if (!BranchPermissionClaim.TryParse(claim, out var permissions, out var branches)) continue;

            foreach (var permission in permissions)
            {
                if (!_byBranch.TryGetValue(permission, out var set)) _byBranch[permission] = set = [];
                foreach (var branch in branches) set.Add(branch);
            }
        }
    }

    public bool HasTenantWide(string permission) => _tenantWide.Contains(permission);

    public bool HasAnywhere(string permission) => _tenantWide.Contains(permission) || _byBranch.ContainsKey(permission);

    public bool HasAt(string permission, Guid branchId) =>
        _tenantWide.Contains(permission) || (_byBranch.TryGetValue(permission, out var branches) && branches.Contains(branchId));

    public IReadOnlyCollection<Guid>? BranchesFor(string permission)
    {
        if (_tenantWide.Contains(permission)) return null;
        return _byBranch.TryGetValue(permission, out var branches) ? branches : [];
    }
}
