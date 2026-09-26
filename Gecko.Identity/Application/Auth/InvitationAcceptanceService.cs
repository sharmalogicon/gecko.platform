using Gecko.Identity.Infrastructure.Auth;
using Gecko.Identity.Infrastructure.Persistence;
using Gecko.Identity.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Application.Auth;

public sealed record InvitationPreview(string Email, string? FullName, string TenantName, string RoleName, string? BranchName, DateTimeOffset ExpiresAt);

public enum AcceptanceStatus
{
    Accepted,

    /// <summary>Unknown, expired, revoked or already used — deliberately one answer.</summary>
    InvalidToken,

    /// <summary>The email already belongs to an active account (here or in another tenant — email is globally unique).</summary>
    EmailInUse,
}

public sealed record AcceptanceOutcome(AcceptanceStatus Status, IssuedAccessToken? Token = null, IssuedSession? Session = null);

/// <summary>
/// ADR-006 step 5: the ONLY way an account gets a password. The token is the proof
/// of email ownership, so a successful acceptance also verifies the email.
///
/// Two shapes of invitation reach here:
///   * usp_provision_tenant  — the owner user ALREADY EXISTS as INVITED, with role and branch.
///                             Acceptance activates that row.
///   * POST /api/invitations — nobody exists yet. Acceptance creates the user.
///
/// Runs on system context: the token is the only thing known, so the tenant cannot be
/// scoped until the invitation is found. Every query below therefore filters on the
/// invitation's tenant EXPLICITLY — RLS is not there to catch a mistake.
/// </summary>
public sealed class InvitationAcceptanceService(
    IdentitySystemDbContext system,
    PasswordHasher hasher,
    ClaimsBuilder claims,
    SessionService sessions,
    AuthEventWriter events,
    AccessTokenIssuer tokens,
    TimeProvider clock)
{
    public async Task<InvitationPreview?> PreviewAsync(string token, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hash = SecretTokens.Hash(token);

        return await system.Invitations.AsNoTracking()
            .Where(i => i.TokenHash == hash && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .Select(i => new InvitationPreview(
                i.Email,
                i.FullName,
                system.Tenants.Where(t => t.TenantId == i.TenantId).Select(t => t.DisplayName).First(),
                system.Roles.Where(r => r.RoleId == i.RoleId && r.TenantId == i.TenantId).Select(r => r.DisplayName).First(),
                system.Branches.Where(b => b.BranchId == i.BranchId && b.TenantId == i.TenantId).Select(b => b.DisplayName).FirstOrDefault(),
                i.ExpiresAt))
            .SingleOrDefaultAsync(ct);
    }

    public async Task<AcceptanceOutcome> AcceptAsync(string token, string password, string? fullName, ClientInfo client, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hash = SecretTokens.Hash(token);

        var invitation = await system.Invitations
            .SingleOrDefaultAsync(i => i.TokenHash == hash && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now, ct);
        var tenantActive = invitation is not null
            && await system.Tenants.AnyAsync(t => t.TenantId == invitation.TenantId && t.Status == "ACTIVE", ct);
        var role = invitation is null ? null
            : await system.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.RoleId == invitation.RoleId && r.TenantId == invitation.TenantId && r.IsActive, ct);

        if (invitation is null || !tenantActive || role is null)
        {
            await events.RecordAsync(client, "INVITATION_ACCEPTED", invitation is null ? "INVALID_TOKEN" : "TENANT_OR_ROLE_INACTIVE",
                invitation?.TenantId, email: invitation?.EmailNormalised, ct: ct);
            return new(AcceptanceStatus.InvalidToken);
        }

        // Global lookup on purpose: email is unique across ALL tenants.
        var user = await system.Users.SingleOrDefaultAsync(u => u.EmailNormalised == invitation.EmailNormalised, ct);
        if (user is not null && (user.TenantId != invitation.TenantId || user.Status != "INVITED"))
        {
            // Recorded WITHOUT a tenant: in the inviting tenant's audit view this row would
            // tell their admin that the address has an account elsewhere on the platform.
            await events.RecordAsync(client, "INVITATION_ACCEPTED", "EMAIL_IN_USE", tenantId: null, userId: null, invitation.EmailNormalised, ct);
            return new(AcceptanceStatus.EmailInUse);
        }

        var credential = hasher.Hash(password);

        await using (var tx = await system.Database.BeginTransactionAsync(ct))
        {
            var created = user is null;
            user ??= system.Users.Add(new User
            {
                TenantId = invitation.TenantId,
                Email = invitation.Email,
                EmailNormalised = invitation.EmailNormalised,
                FullName = invitation.FullName ?? invitation.Email,
                UserType = "OPERATOR",
                Status = "INVITED",
                DefaultBranchId = invitation.BranchId,
            }).Entity;

            if (!string.IsNullOrWhiteSpace(fullName)) user.FullName = fullName.Trim();
            user.Status = "ACTIVE";
            user.EmailVerifiedAt = now;
            user.LastLoginAt = now;
            await system.SaveChangesAsync(ct);

            await system.Credentials
                .Where(c => c.UserId == user.UserId && c.IsCurrent)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsCurrent, false), ct);
            system.Credentials.Add(new Credential
            {
                TenantId = user.TenantId,
                UserId = user.UserId,
                PasswordHash = credential.Hash,
                Algorithm = credential.Algorithm,
                AlgorithmParams = credential.Parameters,
                IsCurrent = true,
                MustChange = false,
            });

            if (invitation.BranchId is { } branchId && !await system.UserBranches.AnyAsync(ub => ub.UserId == user.UserId && ub.BranchId == branchId, ct))
                system.UserBranches.Add(new UserBranch { TenantId = user.TenantId, UserId = user.UserId, BranchId = branchId });

            // Invitation branch = the branch they get ACCESS to. A tenant-admin role is
            // held tenant-wide (that is what usp_provision_tenant does for the owner);
            // any other role is held at that branch.
            if (!await system.UserRoles.AnyAsync(ur => ur.UserId == user.UserId && ur.RoleId == role.RoleId, ct))
                system.UserRoles.Add(new UserRole { TenantId = user.TenantId, UserId = user.UserId, RoleId = role.RoleId, BranchId = role.IsTenantAdmin ? null : invitation.BranchId });

            invitation.AcceptedAt = now;
            invitation.AcceptedUserId = user.UserId;

            system.ChangeLogs.Add(new ChangeLog
            {
                TenantId = user.TenantId,
                ActorUserId = user.UserId,
                ActorDescription = "invitation acceptance",
                EntityType = "USER",
                EntityId = user.UserId,
                Action = created ? "CREATE" : "ACTIVATE",
                Reason = $"accepted invitation {invitation.InvitationId}",
            });

            await system.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var accessToken = tokens.Issue(await claims.BuildAsync(user.TenantId, user.UserId, user.UserType, ct));
        var session = await sessions.StartAsync(user.TenantId, user.UserId, client, ct);
        await events.RecordAsync(client, "INVITATION_ACCEPTED", null, user.TenantId, user.UserId, user.EmailNormalised, ct);

        return new(AcceptanceStatus.Accepted, accessToken, session);
    }
}
