using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Gecko.Data;

/// <summary>
/// A DbContext whose queries also say <c>tenant_id = @tenant</c> (owner 2026-10-18).
///
/// RLS stays the guard. Its predicate (tenant = SESSION_CONTEXT OR system context) gives
/// the optimizer no equality on tenant_id, and nearly every index leads with tenant_id, so
/// a query that does not name the tenant scans — 1.4 s for a page of 134k containers on
/// Azure, 2 ms with the equality. This filter only adds that equality: the same rows.
/// </summary>
public interface ITenantFilteredContext
{
    /// <summary>The tenant every query is filtered on; Guid.Empty (no rows) when none is known.</summary>
    Guid FilterTenantId { get; }
}

public static class TenantFilter
{
    private static TenantSessionInterceptor? InterceptorOf(DbContext context) =>
        context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()?.Interceptors?
            .OfType<TenantSessionInterceptor>()
            .FirstOrDefault();

    /// <summary>
    /// True for a context whose connections are scoped by <see cref="TenantSessionInterceptor"/>. A system
    /// context (gecko_system, cross-tenant) has none and is modelled without the tenant filter. Decided per
    /// context TYPE, once, when EF builds its model — so one type must never be registered both ways.
    /// </summary>
    public static bool IsTenantScoped(DbContext context) => InterceptorOf(context) is not null;

    /// <summary>The tenant this context's connections set in SESSION_CONTEXT; Guid.Empty when none.</summary>
    public static Guid For(DbContext context) => InterceptorOf(context)?.TenantId ?? Guid.Empty;
}
