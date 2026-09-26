using Gecko.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Gecko.Data;

/// <summary>
/// Fills the house audit columns by NAME, so scaffolded entities need no base
/// class or interface:
///   Added    -> created_by, updated_by
///   Modified -> updated_at, updated_by   (the DB default only fires on INSERT)
///   Deleted  -> converted to an UPDATE of deleted_at / deleted_by
///
/// The last one is not a convenience. Both SQL logins are DENIED DELETE on
/// tenant / iam / subscription, so a real DELETE fails at the engine. Turning
/// Remove() into a soft delete here means no endpoint can get it wrong.
/// </summary>
public sealed class AuditStampInterceptor(ITenantContext caller, TimeProvider clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null) return;

        var now = clock.GetUtcNow();
        var userId = caller.UserId;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    SetIfPresent(entry, "CreatedBy", userId);
                    SetIfPresent(entry, "UpdatedBy", userId);
                    break;

                case EntityState.Modified:
                    SetIfPresent(entry, "UpdatedAt", now);
                    SetIfPresent(entry, "UpdatedBy", userId);
                    break;

                case EntityState.Deleted when entry.Metadata.FindProperty("DeletedAt") is not null:
                    entry.State = EntityState.Modified;
                    SetIfPresent(entry, "DeletedAt", now);
                    SetIfPresent(entry, "DeletedBy", userId);
                    SetIfPresent(entry, "UpdatedAt", now);
                    SetIfPresent(entry, "UpdatedBy", userId);
                    break;
            }
        }
    }

    private static void SetIfPresent(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, string property, object? value)
    {
        if (entry.Metadata.FindProperty(property) is null) return;
        entry.Property(property).CurrentValue = value;
    }
}
