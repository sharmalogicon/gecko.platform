using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Gecko.Data;
using Gecko.Identity.Application.Auth;
using Gecko.Identity.Infrastructure.Persistence;
using Gecko.Identity.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Endpoints.Admin;

public sealed record ApiTokenResponse(
    Guid ApiTokenId, string Name, string TokenPrefix, Guid? BranchId, string? ModuleCode, IReadOnlyList<string> Scopes,
    string Status, DateTimeOffset? LastUsedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset CreatedAt);

/// <summary>Returned once, at creation. The secret is not stored and cannot be shown again.</summary>
public sealed record CreatedApiTokenResponse(ApiTokenResponse Token, string Secret);

public sealed record CreateApiTokenRequest(
    [property: Required, MaxLength(100)] string Name,
    Guid? BranchId = null,
    [property: MaxLength(20)] string? ModuleCode = null,
    IReadOnlyList<string>? Scopes = null,
    DateTimeOffset? ExpiresAt = null);

public sealed record UpdateApiTokenRequest(
    [property: Required, MaxLength(100)] string Name,
    bool IsActive);

/// <summary>
/// Machine-to-machine keys (EDI feeds, integrations).
///
/// THREE DISTINCT "OFF" STATES, kept distinct on purpose (12_corrections.sql):
///   isActive = false   PAUSED  — reversible (PUT isActive=true)
///   revokedAt set      REVOKED — permanent (DELETE)
///   expiresAt passed   EXPIRED — time-based
/// iam.vw_api_token_usable is the single definition of "usable"; Status below mirrors it.
/// </summary>
internal static class ApiTokenEndpoints
{
    public static RouteGroupBuilder MapApiTokenEndpoints(this RouteGroupBuilder api)
    {
        var tokens = api.MapGroup("/api-tokens").WithTags("API tokens").RequirePermission(Permissions.ApiTokenManage);

        tokens.MapGet("/", ListAsync).WithSummary("List API tokens (secrets are never returned)");
        tokens.MapPost("/", CreateAsync).Validate<CreateApiTokenRequest>().WithSummary("Create a token — the secret is shown ONCE");
        tokens.MapPut("/{apiTokenId:guid}", UpdateAsync).Validate<UpdateApiTokenRequest>().WithSummary("Rename, pause (isActive=false) or resume");
        tokens.MapDelete("/{apiTokenId:guid}", RevokeAsync).WithSummary("Revoke permanently");

        return api;
    }

    private static async Task<Ok<List<ApiTokenResponse>>> ListAsync(IdentityDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var rows = await db.ApiTokens.AsNoTracking().OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(t => ToResponse(t, clock.GetUtcNow())).ToList());
    }

    private static async Task<Results<Created<CreatedApiTokenResponse>, ValidationProblem>> CreateAsync(
        CreateApiTokenRequest request, IdentityDbContext db, ITenantContext caller, TimeProvider clock, CancellationToken ct)
    {
        if (request.BranchId is { } branchId && !await db.Branches.AnyAsync(b => b.BranchId == branchId && b.IsActive, ct))
            return AdminSupport.InvalidReference("branchId", "Unknown or inactive branch.");
        if (request.ModuleCode is { } module && !await db.Modules.AnyAsync(m => m.ModuleCode == module && m.IsActive, ct))
            return AdminSupport.InvalidReference("moduleCode", $"Unknown module '{module}'.");
        if (request.ExpiresAt is { } expires && expires <= clock.GetUtcNow())
            return AdminSupport.InvalidReference("expiresAt", "Expiry must be in the future.");

        var secret = SecretTokens.NewApiKey();
        var token = new ApiToken
        {
            TenantId = caller.TenantId(),
            BranchId = request.BranchId,
            ModuleCode = request.ModuleCode,
            Name = request.Name,
            TokenPrefix = SecretTokens.DisplayPrefix(secret),
            TokenHash = SecretTokens.Hash(secret),
            ScopesJson = request.Scopes is { Count: > 0 } ? JsonSerializer.Serialize(request.Scopes) : null,
            ExpiresAt = request.ExpiresAt,
            IsActive = true,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(ct);
        db.RecordChange(caller, "API_TOKEN", token.ApiTokenId, "CREATE", after: new { token.Name, token.TokenPrefix, token.BranchId, token.ModuleCode });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return TypedResults.Created($"/api/api-tokens/{token.ApiTokenId}", new CreatedApiTokenResponse(ToResponse(token, clock.GetUtcNow()), secret));
    }

    private static async Task<Results<Ok<ApiTokenResponse>, NotFound, ProblemHttpResult>> UpdateAsync(
        Guid apiTokenId, UpdateApiTokenRequest request, IdentityDbContext db, ITenantContext caller, TimeProvider clock, CancellationToken ct)
    {
        var token = await db.ApiTokens.SingleOrDefaultAsync(t => t.ApiTokenId == apiTokenId, ct);
        if (token is null) return TypedResults.NotFound();
        if (token.RevokedAt is not null && request.IsActive)
            return AdminSupport.Conflict("A revoked token cannot be resumed. Create a new one.");

        var action = token.IsActive == request.IsActive ? "UPDATE" : request.IsActive ? "ACTIVATE" : "SUSPEND";
        db.RecordChange(caller, "API_TOKEN", apiTokenId, action, new { token.Name, token.IsActive }, new { request.Name, request.IsActive });
        token.Name = request.Name;
        token.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(ToResponse(token, clock.GetUtcNow()));
    }

    private static async Task<Results<NoContent, NotFound>> RevokeAsync(
        Guid apiTokenId, IdentityDbContext db, ITenantContext caller, TimeProvider clock, CancellationToken ct)
    {
        var token = await db.ApiTokens.SingleOrDefaultAsync(t => t.ApiTokenId == apiTokenId, ct);
        if (token is null) return TypedResults.NotFound();
        if (token.RevokedAt is not null) return TypedResults.NoContent();

        token.RevokedAt = clock.GetUtcNow();
        db.RecordChange(caller, "API_TOKEN", apiTokenId, "REVOKE", before: new { token.Name, token.TokenPrefix });
        await db.SaveChangesAsync(ct);

        return TypedResults.NoContent();
    }

    private static ApiTokenResponse ToResponse(ApiToken t, DateTimeOffset now) => new(
        t.ApiTokenId, t.Name, t.TokenPrefix, t.BranchId, t.ModuleCode,
        t.ScopesJson is null ? [] : JsonSerializer.Deserialize<List<string>>(t.ScopesJson) ?? [],
        t.RevokedAt is not null ? "REVOKED" : !t.IsActive ? "PAUSED" : t.ExpiresAt <= now ? "EXPIRED" : "ACTIVE",
        t.LastUsedAt, t.ExpiresAt, t.RevokedAt, t.CreatedAt);
}
