using Gecko.Identity.Infrastructure.Auth;
using Gecko.Identity.Infrastructure.Persistence;
using Gecko.Identity.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gecko.Identity.Application.Auth;

public sealed record IssuedSession(string RefreshToken, DateTimeOffset ExpiresAt);

public enum RefreshStatus
{
    Succeeded,

    /// <summary>Unknown, expired, logged out, user no longer active, or a benign rotation race.</summary>
    Invalid,

    /// <summary>A token that was already rotated came back. Somebody else has a copy; the whole chain is now revoked.</summary>
    ReuseDetected,
}

public sealed record RefreshOutcome(RefreshStatus Status, IssuedAccessToken? AccessToken = null, IssuedSession? Session = null);

/// <summary>
/// Refresh tokens (ADR-006): 30 days, HttpOnly cookie, stored hashed, ROTATED on every use.
///
/// REUSE DETECTION. Each refresh revokes the presented token (ROTATED) and links it to
/// its replacement (replaced_by_id). If a ROTATED token is ever presented again, two
/// parties hold the same session — the user and a thief — and we cannot tell which is
/// which. So we revoke every token downstream of it: the thief holds the tip of the
/// chain, and revoking only the presented token would leave them logged in.
/// </summary>
public sealed class SessionService(
    IdentitySystemDbContext system,
    ClaimsBuilder claims,
    AccessTokenIssuer tokens,
    AuthEventWriter events,
    IOptions<RefreshTokenOptions> options,
    TimeProvider clock)
{
    public async Task<IssuedSession> StartAsync(Guid tenantId, Guid userId, ClientInfo client, CancellationToken ct)
    {
        var (row, raw) = NewToken(tenantId, userId, client);
        system.RefreshTokens.Add(row);
        await system.SaveChangesAsync(ct);
        return new IssuedSession(raw, row.ExpiresAt);
    }

    public async Task<RefreshOutcome> RefreshAsync(string presented, ClientInfo client, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hash = SecretTokens.Hash(presented);

        var current = await system.RefreshTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => new
            {
                t.TokenId, t.TenantId, t.UserId, t.ExpiresAt, t.RevokedAt, t.RevokedReason, t.ReplacedById,
                User = system.Users.Where(u => u.UserId == t.UserId).Select(u => new { u.Status, u.UserType, u.Email }).FirstOrDefault(),
                TenantStatus = system.Tenants.Where(x => x.TenantId == t.TenantId).Select(x => x.Status).FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);

        if (current is null)
        {
            await events.RecordAsync(client, "TOKEN_REFRESHED", "UNKNOWN_TOKEN", ct: ct);
            return new(RefreshStatus.Invalid);
        }

        async Task<RefreshOutcome> Refuse(string reason, RefreshStatus status = RefreshStatus.Invalid)
        {
            await events.RecordAsync(client, status == RefreshStatus.ReuseDetected ? "TOKEN_REUSE_DETECTED" : "TOKEN_REFRESHED",
                reason, current.TenantId, current.UserId, current.User?.Email, ct);
            return new(status);
        }

        if (current.RevokedAt is { } revokedAt)
        {
            if (current.RevokedReason != "ROTATED") return await Refuse("REVOKED");
            if (now - revokedAt < TimeSpan.FromSeconds(options.Value.RotationGraceSeconds)) return await Refuse("ROTATION_RACE");

            await RevokeDownstreamAsync(current.ReplacedById, now, ct);
            return await Refuse("REUSE_DETECTED", RefreshStatus.ReuseDetected);
        }

        if (current.ExpiresAt <= now) return await Refuse("EXPIRED");

        if (current.User is not { Status: "ACTIVE" } || current.TenantStatus != "ACTIVE")
        {
            await RevokeAsync(current.TokenId, "ADMIN_REVOKE", now, ct);
            return await Refuse(current.User?.Status == "DISABLED" ? "DISABLED" : "TENANT_SUSPENDED");
        }

        // Rotate. The conditional UPDATE is the lock: if a concurrent request rotated
        // this token first, zero rows change and this request loses the race.
        var (next, raw) = NewToken(current.TenantId, current.UserId, client);
        await using (var tx = await system.Database.BeginTransactionAsync(ct))
        {
            system.RefreshTokens.Add(next);
            await system.SaveChangesAsync(ct);

            var rotated = await system.RefreshTokens
                .Where(t => t.TokenId == current.TokenId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.RevokedAt, now)
                    .SetProperty(t => t.RevokedReason, "ROTATED")
                    .SetProperty(t => t.ReplacedById, next.TokenId), ct);

            if (rotated == 0)
            {
                await tx.RollbackAsync(ct);
                system.ChangeTracker.Clear();
                return await Refuse("ROTATION_RACE");
            }
            await tx.CommitAsync(ct);
        }

        var accessToken = tokens.Issue(await claims.BuildAsync(current.TenantId, current.UserId, current.User.UserType, ct));
        await events.RecordAsync(client, "TOKEN_REFRESHED", null, current.TenantId, current.UserId, current.User.Email, ct);

        return new(RefreshStatus.Succeeded, accessToken, new IssuedSession(raw, next.ExpiresAt));
    }

    /// <summary>Logout. Idempotent: an unknown or already-revoked token is not an error.</summary>
    public async Task EndAsync(string presented, ClientInfo client, CancellationToken ct)
    {
        var hash = SecretTokens.Hash(presented);
        var token = await system.RefreshTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .Select(t => new { t.TokenId, t.TenantId, t.UserId })
            .SingleOrDefaultAsync(ct);
        if (token is null) return;

        await RevokeAsync(token.TokenId, "LOGOUT", clock.GetUtcNow(), ct);
        await events.RecordAsync(client, "LOGOUT", null, token.TenantId, token.UserId, ct: ct);
    }

    private (RefreshToken Row, string Raw) NewToken(Guid tenantId, Guid userId, ClientInfo client)
    {
        var raw = SecretTokens.NewRefreshToken();
        return (new RefreshToken
        {
            TenantId = tenantId,
            UserId = userId,
            TokenHash = SecretTokens.Hash(raw),
            ExpiresAt = clock.GetUtcNow().AddDays(options.Value.RefreshTokenDays),
            IpAddress = client.IpAddress,
            UserAgent = client.UserAgentTruncated,
        }, raw);
    }

    private Task<int> RevokeAsync(Guid tokenId, string reason, DateTimeOffset now, CancellationToken ct) =>
        system.RefreshTokens
            .Where(t => t.TokenId == tokenId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.RevokedReason, reason), ct);

    /// <summary>Walks replaced_by_id from the reused token to the tip and revokes everything still live.</summary>
    private async Task RevokeDownstreamAsync(Guid? firstReplacement, DateTimeOffset now, CancellationToken ct)
    {
        var chain = new List<Guid>();
        for (var next = firstReplacement; next is { } id && chain.Count < 10_000;)
        {
            chain.Add(id);
            next = await system.RefreshTokens.Where(t => t.TokenId == id).Select(t => t.ReplacedById).FirstOrDefaultAsync(ct);
        }

        if (chain.Count > 0)
        {
            await system.RefreshTokens
                .Where(t => chain.Contains(t.TokenId) && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.RevokedReason, "REUSE_DETECTED"), ct);
        }
    }
}
