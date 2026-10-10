using Gecko.Identity.Infrastructure.Auth;
using Gecko.Identity.Infrastructure.Persistence;
using Gecko.Identity.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gecko.Identity.Application.Auth;

public enum LoginStatus
{
    Succeeded,

    /// <summary>Wrong email or user name, wrong password, locked, disabled, invited, tenant not active — deliberately indistinguishable to the caller.</summary>
    Failed,

    /// <summary>The password was right (so revealing this leaks nothing) but must be changed before a token is issued.</summary>
    PasswordChangeRequired,
}

public sealed record LoginOutcome(LoginStatus Status, IssuedAccessToken? Token = null, IssuedSession? Session = null);

/// <param name="Login">An e-mail (it has an '@') or a user name (26_user_name.sql) — whichever the user has.</param>
public sealed record LoginAttempt(string Login, string Password, ClientInfo Client);

/// <summary>
/// POST /auth/login. The most security-sensitive code path on the platform.
///
/// Follows 06_rls_security.sql to the letter:
///   1. The account lookup is the ONLY query on system context, and filters on email_normalised
///      (or, for an identifier without '@', on user_name) alone.
///   2. It returns the minimum needed to authenticate.
///   3. After the password verifies, claims are read through a TENANT-scoped context (RLS applies).
///
/// Plus ADR-006: the password is ALWAYS hashed — against a dummy when the email is
/// unknown — so a 3 ms "no such user" vs 60 ms "wrong password" cannot enumerate accounts.
/// </summary>
public sealed class LoginService(
    IdentitySystemDbContext system,
    ClaimsBuilder claims,
    SessionService sessions,
    AuthEventWriter events,
    PasswordHasher hasher,
    AccessTokenIssuer tokens,
    IOptions<LockoutOptions> lockout,
    TimeProvider clock)
{
    public async Task<LoginOutcome> LoginAsync(LoginAttempt attempt, CancellationToken ct)
    {
        var email = attempt.Login.Trim().ToLowerInvariant();
        var now = clock.GetUtcNow();

        var account = await FindForAuthentication_SystemContext(email, ct);

        Task Record(string eventType, string? failureReason) =>
            events.RecordAsync(attempt.Client, eventType, failureReason, account?.TenantId, account?.UserId, email, ct);

        if (account?.Credential is null)
        {
            hasher.VerifyDummy(attempt.Password);
            // The caller sees the same 401 either way; the audit trail should not lie.
            await Record("LOGIN_FAILED", account is null ? "NO_SUCH_USER" : "NO_CREDENTIAL");
            return new(LoginStatus.Failed);
        }

        // Verify BEFORE branching on lock / status, so every failure path costs the same.
        var passwordOk = hasher.Verify(attempt.Password, account.Credential.PasswordHash, account.Credential.Algorithm, account.Credential.AlgorithmParams);

        if (account.LockedUntil > now)
        {
            await Record("LOGIN_FAILED", "LOCKED");
            return new(LoginStatus.Failed);
        }

        if (!passwordOk)
        {
            var locked = await RegisterFailedPasswordAsync(account, now, ct);
            await Record("LOGIN_FAILED", "BAD_PASSWORD");
            if (locked) await Record("ACCOUNT_LOCKED", "BAD_PASSWORD");
            return new(LoginStatus.Failed);
        }

        var refusal = account switch
        {
            { Status: not "ACTIVE" } => account.Status == "INVITED" ? "INVITED" : "DISABLED",
            { TenantStatus: not "ACTIVE" } => "TENANT_SUSPENDED",
            _ => null,
        };
        if (refusal is not null)
        {
            await Record("LOGIN_FAILED", refusal);
            return new(LoginStatus.Failed);
        }

        if (account.Credential.MustChange || account.Credential.ExpiresAt <= now)
        {
            await Record("LOGIN_FAILED", "PASSWORD_CHANGE_REQUIRED");
            return new(LoginStatus.PasswordChangeRequired);
        }

        await system.Users
            .Where(u => u.UserId == account.UserId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.FailedLoginCount, 0)
                .SetProperty(u => u.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(u => u.LastLoginAt, now), ct);

        if (account.Credential.Algorithm != PasswordHasher.Argon2Id)
            await UpgradeCredentialAsync(account, attempt.Password, ct);

        var accessToken = tokens.Issue(await claims.BuildAsync(account.TenantId, account.UserId, account.UserType, ct));
        var session = await sessions.StartAsync(account.TenantId, account.UserId, attempt.Client, ct);
        await Record("LOGIN_SUCCESS", null);

        return new(LoginStatus.Succeeded, accessToken, session);
    }

    private sealed record CredentialRow(Guid CredentialId, byte[] PasswordHash, string Algorithm, string? AlgorithmParams, bool MustChange, DateTimeOffset? ExpiresAt);

    private sealed record AccountRow(Guid UserId, Guid TenantId, string UserType, string Status, DateTimeOffset? LockedUntil, int FailedLoginCount, string? TenantStatus, CredentialRow? Credential);

    /// <summary>
    /// The one system-context lookup. Named so nobody reuses it by accident.
    /// An identifier with '@' is an e-mail, anything else a user name; a user name never
    /// contains '@' (ck_user__user_name), so the two can never find different accounts.
    /// user_name compares case-insensitively through the database collation.
    /// </summary>
    private Task<AccountRow?> FindForAuthentication_SystemContext(string login, CancellationToken ct) =>
        (login.Contains('@')
            ? system.Users.Where(u => u.EmailNormalised == login)
            : system.Users.Where(u => u.UserName == login && u.DeletedAt == null))
            .Select(u => new AccountRow(
                u.UserId,
                u.TenantId,
                u.UserType,
                u.Status,
                u.LockedUntil,
                u.FailedLoginCount,
                system.Tenants.Where(t => t.TenantId == u.TenantId).Select(t => t.Status).FirstOrDefault(),
                system.Credentials
                    .Where(c => c.UserId == u.UserId && c.IsCurrent)
                    .Select(c => new CredentialRow(c.CredentialId, c.PasswordHash, c.Algorithm, c.AlgorithmParams, c.MustChange, c.ExpiresAt))
                    .FirstOrDefault()))
            .SingleOrDefaultAsync(ct);

    /// <returns>True when this failure locked the account.</returns>
    private async Task<bool> RegisterFailedPasswordAsync(AccountRow account, DateTimeOffset now, CancellationToken ct)
    {
        var limits = lockout.Value;
        var locks = account.FailedLoginCount + 1 >= limits.MaxFailedAttempts;

        // Atomic increment in SQL; the lock decision uses the value read above,
        // which can lag by a concurrent attempt or two — acceptable for a lockout.
        await system.Users
            .Where(u => u.UserId == account.UserId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.FailedLoginCount, u => locks ? 0 : u.FailedLoginCount + 1)
                .SetProperty(u => u.LockedUntil, u => locks ? now.AddMinutes(limits.LockoutMinutes) : u.LockedUntil), ct);

        return locks;
    }

    /// <summary>
    /// Rehash-on-login: a correct password under an older algorithm is re-stored as Argon2id.
    ///
    /// Not only hygiene — it closes a timing gap. The miss path verifies against an
    /// Argon2id dummy (~220 ms); a PBKDF2 credential verifies in ~40 ms. While both
    /// kinds exist, "wrong password" and "no such user" are distinguishable by the
    /// clock. Measured on the dev fixtures 2026-09-15. Once every credential has
    /// logged in once, every path costs the same.
    ///
    /// The old row keeps its history (is_current = 0), never deleted.
    /// </summary>
    private async Task UpgradeCredentialAsync(AccountRow account, string password, CancellationToken ct)
    {
        var fresh = hasher.Hash(password);

        await using var tx = await system.Database.BeginTransactionAsync(ct);

        await system.Credentials
            .Where(c => c.CredentialId == account.Credential!.CredentialId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsCurrent, false), ct);

        system.Credentials.Add(new Credential
        {
            UserId = account.UserId,
            TenantId = account.TenantId,
            PasswordHash = fresh.Hash,
            Algorithm = fresh.Algorithm,
            AlgorithmParams = fresh.Parameters,
            IsCurrent = true,
            MustChange = false,
        });
        await system.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);
    }
}
