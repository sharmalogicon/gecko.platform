using Gecko.Data;
using Gecko.Identity.Infrastructure.Persistence;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Endpoints.Admin;

public sealed record AuthEventResponse(
    long AuthEventId, Guid? UserId, string? EmailAttempted, string EventType, string Outcome, string? FailureReason,
    string? IpAddress, string? UserAgent, DateTimeOffset OccurredAt);

public sealed record ChangeLogResponse(
    long ChangeLogId, Guid? ActorUserId, string? ActorEmail, string EntityType, Guid? EntityId, string Action,
    string? BeforeJson, string? AfterJson, string? Reason, DateTimeOffset OccurredAt);

/// <summary>Append-only audit trail (DENY UPDATE/DELETE on audit.* for both logins). Read-only here by construction.</summary>
internal static class AuditEndpoints
{
    public static RouteGroupBuilder MapAuditEndpoints(this RouteGroupBuilder api)
    {
        var audit = api.MapGroup("/audit").WithTags("Audit (read-only)").RequirePermission(Permissions.AuditView);

        audit.MapGet("/auth-events", AuthEventsAsync).WithSummary("Sign-in activity: logins, failures, lockouts");
        audit.MapGet("/change-log", ChangeLogAsync).WithSummary("Who changed what in the admin console");

        return api;
    }

    /// <summary>
    /// !! audit.auth_event is NOT covered by RLS (06_rls_security.sql): tenant_id is
    /// nullable, because a failed login for an unknown email has no tenant and is
    /// exactly the attack evidence worth keeping. So the tenant filter below is the
    /// ONLY thing between one tenant and every other tenant's sign-in history.
    /// Tested by Auth_events_are_filtered_to_the_callers_tenant.
    /// </summary>
    private static async Task<Ok<PagedResult<AuthEventResponse>>> AuthEventsAsync(
        [AsParameters] ListQuery query, IdentityDbContext db, ITenantContext caller, CancellationToken ct,
        string? outcome = null, string? eventType = null, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var tenantId = caller.TenantId();
        var events = db.AuthEvents.AsNoTracking().Where(e => e.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(outcome)) events = events.Where(e => e.Outcome == outcome.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(eventType)) events = events.Where(e => e.EventType == eventType.ToUpperInvariant());
        if (from is not null) events = events.Where(e => e.OccurredAt >= from);
        if (to is not null) events = events.Where(e => e.OccurredAt <= to);
        if (!string.IsNullOrWhiteSpace(query.Search)) events = events.Where(e => e.EmailAttempted!.Contains(query.Search.ToLower()));

        return TypedResults.Ok(await events
            .OrderByDescending(e => e.AuthEventId)
            .Select(e => new AuthEventResponse(e.AuthEventId, e.UserId, e.EmailAttempted, e.EventType, e.Outcome, e.FailureReason, e.IpAddress, e.UserAgent, e.OccurredAt))
            .ToPagedAsync(query.Page, query.PageSize, ct));
    }

    private static async Task<Ok<PagedResult<ChangeLogResponse>>> ChangeLogAsync(
        [AsParameters] ListQuery query, IdentityDbContext db, CancellationToken ct,
        string? entityType = null, Guid? entityId = null, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        // RLS covers change_log, so no explicit tenant filter is needed — contrast with auth_event above.
        var changes = db.ChangeLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityType)) changes = changes.Where(c => c.EntityType == entityType.ToUpperInvariant());
        if (entityId is not null) changes = changes.Where(c => c.EntityId == entityId);
        if (from is not null) changes = changes.Where(c => c.OccurredAt >= from);
        if (to is not null) changes = changes.Where(c => c.OccurredAt <= to);

        return TypedResults.Ok(await changes
            .OrderByDescending(c => c.ChangeLogId)
            .Select(c => new ChangeLogResponse(
                c.ChangeLogId, c.ActorUserId,
                db.Users.IgnoreQueryFilters().Where(u => u.UserId == c.ActorUserId).Select(u => u.Email).FirstOrDefault(),
                c.EntityType, c.EntityId, c.Action, c.BeforeJson, c.AfterJson, c.Reason, c.OccurredAt))
            .ToPagedAsync(query.Page, query.PageSize, ct));
    }
}
