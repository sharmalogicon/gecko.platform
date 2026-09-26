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
    public static ModelBuilder ApplySoftDeleteFilters(this ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var deletedAt = entityType.FindProperty("DeletedAt");
            if (deletedAt is null || entityType.IsKeyless) continue;

            var row = Expression.Parameter(entityType.ClrType, "row");
            var isLive = Expression.Equal(
                Expression.Property(row, deletedAt.PropertyInfo!),
                Expression.Constant(null, deletedAt.ClrType));

            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(Expression.Lambda(isLive, row));
        }

        return modelBuilder;
    }
}
