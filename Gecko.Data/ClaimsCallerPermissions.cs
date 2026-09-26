using Gecko.SharedKernel;
using Microsoft.AspNetCore.Http;

namespace Gecko.Data;

/// <summary>
/// Reads <c>prm</c> and <c>bpm</c> off the request's access token once and
/// answers every scope question from that snapshot — the claims cannot change
/// mid-request, and a module asking twice must not pay for the parse twice.
/// </summary>
public sealed class ClaimsCallerPermissions(IHttpContextAccessor accessor) : ICallerPermissions
{
    private CallerPermissions? _resolved;

    private CallerPermissions Resolved => _resolved ??= Build();

    private CallerPermissions Build()
    {
        var user = accessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return CallerPermissions.None;

        return new CallerPermissions(
            user.FindAll(GeckoClaimTypes.Permission).Select(c => c.Value),
            user.FindAll(GeckoClaimTypes.BranchPermission).Select(c => c.Value));
    }

    public bool HasTenantWide(string permission) => Resolved.HasTenantWide(permission);

    public bool HasAnywhere(string permission) => Resolved.HasAnywhere(permission);

    public bool HasAt(string permission, Guid branchId) => Resolved.HasAt(permission, branchId);

    public IReadOnlyCollection<Guid>? BranchesFor(string permission) => Resolved.BranchesFor(permission);
}
