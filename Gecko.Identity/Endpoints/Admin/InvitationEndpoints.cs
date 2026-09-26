using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Gecko.Data;
using Gecko.Identity.Application.Auth;
using Gecko.Identity.Infrastructure.Persistence;
using Gecko.Identity.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Gecko.Identity.Endpoints.Admin;

public sealed record InvitationResponse(
    Guid InvitationId, string Email, string? FullName, Guid RoleId, string RoleCode, Guid? BranchId,
    string Status, DateTimeOffset ExpiresAt, DateTimeOffset? AcceptedAt, DateTimeOffset? RevokedAt, int ResentCount,
    DateTimeOffset CreatedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? DevelopmentAcceptToken = null);

/// <param name="BranchId">
/// The branch the invitee gets access to. A tenant-admin role (TENANT_OWNER) is still
/// held tenant-wide; any other role is held at this branch. Omit for tenant-wide roles.
/// </param>
public sealed record CreateInvitationRequest(
    [property: Required, EmailAddress, MaxLength(256)] string Email,
    [property: Required] Guid RoleId,
    [property: MaxLength(200)] string? FullName = null,
    Guid? BranchId = null);

/// <summary>
/// The only way a user is added (ADR-006 D3). The token is a bearer credential: it
/// is generated here, stored as a hash, and delivered to the invitee — never logged.
///
/// DELIVERY IS NOT BUILT YET (Notification module). Until it is, the Development
/// environment returns the raw token as developmentAcceptToken so the flow can be
/// tested end to end. Outside Development it is never returned.
/// </summary>
internal static class InvitationEndpoints
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);   // matches usp_provision_tenant

    public static RouteGroupBuilder MapInvitationEndpoints(this RouteGroupBuilder api)
    {
        var invitations = api.MapGroup("/invitations").WithTags("Invitations").RequirePermission(Permissions.UserManage);

        invitations.MapGet("/", ListAsync).WithSummary("List invitations (status: PENDING, ACCEPTED, REVOKED, EXPIRED)");
        invitations.MapPost("/", CreateAsync).Validate<CreateInvitationRequest>().WithSummary("Invite someone by email");
        invitations.MapPost("/{invitationId:guid}/resend", ResendAsync).WithSummary("Issue a fresh token and extend the expiry");
        invitations.MapDelete("/{invitationId:guid}", RevokeAsync).WithSummary("Revoke a pending invitation");

        return api;
    }

    private static async Task<Ok<PagedResult<InvitationResponse>>> ListAsync(
        [AsParameters] ListQuery query, IdentityDbContext db, TimeProvider clock, CancellationToken ct, string? status = null)
    {
        var now = clock.GetUtcNow();
        var invitations = db.Invitations.AsNoTracking();

        invitations = status?.ToUpperInvariant() switch
        {
            "PENDING" => invitations.Where(i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now),
            "ACCEPTED" => invitations.Where(i => i.AcceptedAt != null),
            "REVOKED" => invitations.Where(i => i.RevokedAt != null),
            "EXPIRED" => invitations.Where(i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt <= now),
            _ => invitations,
        };
        if (!string.IsNullOrWhiteSpace(query.Search))
            invitations = invitations.Where(i => i.EmailNormalised.Contains(query.Search.ToLower()));

        return TypedResults.Ok(await invitations
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InvitationResponse(
                i.InvitationId, i.Email, i.FullName, i.RoleId,
                db.Roles.Where(r => r.RoleId == i.RoleId).Select(r => r.RoleCode).FirstOrDefault() ?? "?",
                i.BranchId,
                i.AcceptedAt != null ? "ACCEPTED" : i.RevokedAt != null ? "REVOKED" : i.ExpiresAt <= now ? "EXPIRED" : "PENDING",
                i.ExpiresAt, i.AcceptedAt, i.RevokedAt, i.ResentCount, i.CreatedAt, null))
            .ToPagedAsync(query.Page, query.PageSize, ct));
    }

    private static async Task<Results<Created<InvitationResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateInvitationRequest request, IdentityDbContext db, ITenantContext caller, ClaimsPrincipal principal,
        TimeProvider clock, IHostEnvironment environment, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var role = await db.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.RoleId == request.RoleId && r.IsActive, ct);
        if (role is null)
            return AdminSupport.InvalidReference("roleId", "Unknown or inactive role.");
        if (request.BranchId is { } branchId && !await db.Branches.AnyAsync(b => b.BranchId == branchId && b.IsActive, ct))
            return AdminSupport.InvalidReference("branchId", "Unknown or inactive branch.");
        // An INVITED user may be re-invited — e.g. a provisioned owner who lost the
        // original email. Anyone who already has a password signs in instead.
        if (await db.Users.AnyAsync(u => u.EmailNormalised == email && u.Status != "INVITED", ct))
            return AdminSupport.Conflict("A user with this email already exists in your tenant.");
        if (await db.Invitations.AnyAsync(i => i.EmailNormalised == email && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > clock.GetUtcNow(), ct))
            return AdminSupport.Conflict("There is already a pending invitation for this email. Resend it instead.");
        if (await AdminSupport.EnsureCallerCanGrantRoleAsync(db, principal, role.RoleId, ct) is { } forbidden)
            return forbidden;

        // An EXPIRED invitation still occupies uq_invitation__pending (the index cannot
        // see the clock). Revoke it so the new one can be inserted.
        var stale = await db.Invitations.Where(i => i.EmailNormalised == email && i.AcceptedAt == null && i.RevokedAt == null).ToListAsync(ct);
        foreach (var old in stale) old.RevokedAt = clock.GetUtcNow();

        var token = SecretTokens.NewInvitationToken();
        var invitation = new Invitation
        {
            TenantId = caller.TenantId(),
            Email = request.Email.Trim(),
            EmailNormalised = email,
            FullName = request.FullName,
            RoleId = role.RoleId,
            BranchId = request.BranchId,
            TokenHash = SecretTokens.Hash(token),
            ExpiresAt = clock.GetUtcNow().Add(Lifetime),
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(ct);
        db.RecordChange(caller, "USER", invitation.InvitationId, "CREATE", after: new { invitation.Email, role.RoleCode, invitation.BranchId }, reason: "invitation");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return TypedResults.Created($"/api/invitations/{invitation.InvitationId}", ToResponse(invitation, role.RoleCode, clock, environment.IsDevelopment() ? token : null));
    }

    private static async Task<Results<Ok<InvitationResponse>, NotFound, ProblemHttpResult>> ResendAsync(
        Guid invitationId, IdentityDbContext db, ITenantContext caller, TimeProvider clock, IHostEnvironment environment, CancellationToken ct)
    {
        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.InvitationId == invitationId, ct);
        if (invitation is null) return TypedResults.NotFound();
        if (invitation.AcceptedAt is not null || invitation.RevokedAt is not null)
            return AdminSupport.Conflict("Only a pending or expired invitation can be resent.");

        // A new token, not the old one: the previous link may be sitting in a
        // forwarded email. Resending invalidates it.
        var token = SecretTokens.NewInvitationToken();
        invitation.TokenHash = SecretTokens.Hash(token);
        invitation.ExpiresAt = clock.GetUtcNow().Add(Lifetime);
        invitation.ResentCount++;

        db.RecordChange(caller, "USER", invitationId, "UPDATE", reason: "invitation resent");
        await db.SaveChangesAsync(ct);

        var roleCode = await db.Roles.Where(r => r.RoleId == invitation.RoleId).Select(r => r.RoleCode).FirstOrDefaultAsync(ct) ?? "?";
        return TypedResults.Ok(ToResponse(invitation, roleCode, clock, environment.IsDevelopment() ? token : null));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RevokeAsync(
        Guid invitationId, IdentityDbContext db, ITenantContext caller, TimeProvider clock, CancellationToken ct)
    {
        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.InvitationId == invitationId, ct);
        if (invitation is null) return TypedResults.NotFound();
        if (invitation.AcceptedAt is not null) return AdminSupport.Conflict("The invitation was already accepted. Disable the user instead.");
        if (invitation.RevokedAt is not null) return TypedResults.NoContent();

        invitation.RevokedAt = clock.GetUtcNow();
        db.RecordChange(caller, "USER", invitationId, "REVOKE", reason: "invitation revoked");
        await db.SaveChangesAsync(ct);

        return TypedResults.NoContent();
    }

    private static InvitationResponse ToResponse(Invitation i, string roleCode, TimeProvider clock, string? token) => new(
        i.InvitationId, i.Email, i.FullName, i.RoleId, roleCode, i.BranchId,
        i.AcceptedAt != null ? "ACCEPTED" : i.RevokedAt != null ? "REVOKED" : i.ExpiresAt <= clock.GetUtcNow() ? "EXPIRED" : "PENDING",
        i.ExpiresAt, i.AcceptedAt, i.RevokedAt, i.ResentCount, i.CreatedAt, token);
}
