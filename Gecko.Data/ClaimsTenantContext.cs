using Gecko.SharedKernel;
using Microsoft.AspNetCore.Http;

namespace Gecko.Data;

/// <summary>
/// Reads the tenant and user from the validated access token of the current
/// request — or, outside a request, the tenant of the outbox message being
/// handled (<see cref="OutboxTenantScope"/>), with no user.
/// </summary>
public sealed class ClaimsTenantContext(IHttpContextAccessor accessor, OutboxTenantScope message) : ITenantContext
{
    public Guid? TenantId => ReadGuid(GeckoClaimTypes.TenantId) ?? message.TenantId;

    public Guid? UserId => ReadGuid(GeckoClaimTypes.UserId);

    private Guid? ReadGuid(string claimType)
    {
        var user = accessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return null;

        return Guid.TryParse(user.FindFirst(claimType)?.Value, out var value) ? value : null;
    }
}
