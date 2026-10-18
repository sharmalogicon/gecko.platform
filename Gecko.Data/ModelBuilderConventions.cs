using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Data;

/// <summary>
/// House conventions applied on top of a scaffolded model, so the scaffold can
/// be re-run against the SQL scripts without losing them.
/// </summary>
public static class ModelBuilderConventions
{
    /// <summary>
    /// Every entity with a DeletedAt column hides removed rows. RLS does not look
    /// at deleted_at (06_rls_security.sql) — filtering them is the application's
    /// job, done once here. Opt out per query with IgnoreQueryFilters().
    /// </summary>
    public static ModelBuilder ApplySoftDeleteFilters(this ModelBuilder modelBuilder) => modelBuilder.ApplyHouseFilters(null);

    /// <summary>
    /// The soft-delete filter above and, for a <see cref="ITenantFilteredContext"/>, the tenant
    /// equality (<see cref="TenantFilter"/>) on every entity with a non-null TenantId — one filter
    /// per entity, because a second HasQueryFilter would replace the first.
    ///
    /// The tenant term is a plain <c>row.TenantId == context.FilterTenantId</c>, read by EF from the context
    /// running the query — SQL <c>tenant_id = @tenant</c>, which an index on (tenant_id, …) can seek. Pass null
    /// for a system (cross-tenant) context: its model gets no tenant term. IgnoreQueryFilters() drops both
    /// terms; RLS still scopes the rows.
    /// </summary>
    public static ModelBuilder ApplyHouseFilters(this ModelBuilder modelBuilder, ITenantFilteredContext? context)
    {
        var tenantOfContext = context is null ? null : Expression.Property(Expression.Constant(context), nameof(ITenantFilteredContext.FilterTenantId));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var row = Expression.Parameter(entityType.ClrType, "row");
            Expression? filter = null;

            var deletedAt = entityType.FindProperty("DeletedAt");
            if (deletedAt is not null && !entityType.IsKeyless)
                filter = Expression.Equal(Expression.Property(row, deletedAt.PropertyInfo!), Expression.Constant(null, deletedAt.ClrType));

            // A nullable tenant_id (audit.auth_event: failed logins have none) is not tenant-owned the same way: left alone.
            var tenantId = entityType.FindProperty("TenantId");
            if (tenantOfContext is not null && tenantId?.PropertyInfo is { } tenantProperty && tenantId.ClrType == typeof(Guid))
            {
                var sameTenant = Expression.Equal(Expression.Property(row, tenantProperty), tenantOfContext);
                filter = filter is null ? sameTenant : Expression.AndAlso(filter, sameTenant);
            }

            if (filter is not null)
                modelBuilder.Entity(entityType.ClrType).HasQueryFilter(Expression.Lambda(filter, row));
        }

        return modelBuilder;
    }
}
