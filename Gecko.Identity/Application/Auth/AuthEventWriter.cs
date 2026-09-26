using Gecko.Identity.Infrastructure.Persistence;
using Gecko.Identity.Infrastructure.Persistence.Entities;

namespace Gecko.Identity.Application.Auth;

/// <summary>Where a request came from — carried into every audit.auth_event row.</summary>
public sealed record ClientInfo(string? IpAddress, string? UserAgent)
{
    public string? UserAgentTruncated => UserAgent is { Length: > 400 } ua ? ua[..400] : UserAgent;
}

/// <summary>
/// Appends to audit.auth_event on the system connection (the row may have no tenant:
/// a failed login for an unknown email is exactly the evidence worth keeping).
/// A null failureReason means SUCCESS.
/// </summary>
public sealed class AuthEventWriter(IdentitySystemDbContext system)
{
    public async Task RecordAsync(
        ClientInfo client, string eventType, string? failureReason,
        Guid? tenantId = null, Guid? userId = null, string? email = null, CancellationToken ct = default)
    {
        system.AuthEvents.Add(new AuthEvent
        {
            TenantId = tenantId,
            UserId = userId,
            EmailAttempted = email,
            EventType = eventType,
            Outcome = failureReason is null ? "SUCCESS" : "FAILURE",
            FailureReason = failureReason,
            IpAddress = client.IpAddress,
            UserAgent = client.UserAgentTruncated,
        });
        await system.SaveChangesAsync(ct);
    }
}
