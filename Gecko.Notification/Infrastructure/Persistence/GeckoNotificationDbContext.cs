using Gecko.Notification.Domain.Entities.Core;
using Microsoft.EntityFrameworkCore;

// See NotificationConfiguration — bare `Notification` binds to the namespace
// Gecko.Notification, not the entity. Alias required.
using NotificationEntity = Gecko.Notification.Domain.Entities.Core.Notification;

namespace Gecko.Notification.Infrastructure.Persistence;

/// <summary>
/// The single DbContext for the modular monolith.
///
/// WHY ONE CONTEXT AND NOT ONE PER MODULE: the whole reason the brain is a
/// monolith is that a notification and its outbox row must commit in ONE
/// transaction (Transactional Outbox, LLD-02 §6.3). Two DbContexts means two
/// connections means a distributed transaction — the exact complexity the
/// modular monolith exists to avoid. Module boundaries are enforced by the
/// IConfigModule / ITemplateModule interfaces from HLD_03, not by splitting
/// the context.
///
/// DbSets are intentionally exposed only for AGGREGATE ROOTS. There is no
/// DbSet&lt;DeliveryLog&gt; on purpose — see the note below.
/// </summary>
public class GeckoNotificationDbContext : DbContext
{
    public GeckoNotificationDbContext(DbContextOptions<GeckoNotificationDbContext> options)
        : base(options) { }

    // ---------- core schema ----------
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<NotificationEvent> NotificationEvents => Set<NotificationEvent>();

    /// <summary>
    /// Delivery attempts. Exposed because dispatchers write these directly and
    /// they are not reachable through the Notification aggregate — the child
    /// rows are written by a DIFFERENT process (an Azure Function) than the one
    /// that owns the parent. Making it a true child collection would force
    /// dispatchers to load the whole aggregate just to append one attempt row.
    /// </summary>
    public DbSet<DeliveryLog> DeliveryLogs => Set<DeliveryLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Picks up every IEntityTypeConfiguration in this assembly, so adding
        // the config/, template/, lookup/, outbox/ and audit/ schemas needs no
        // change here — just drop the configuration classes in and they bind.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(GeckoNotificationDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // datetime2(7) everywhere, matching the schema. Without this EF picks
        // datetime2(7) on SQL Server anyway, but stating it means a provider
        // change cannot silently truncate delivery timestamps — and at a 5
        // second SLA, sub-second precision is not decorative.
        configurationBuilder.Properties<DateTime>().HavePrecision(7);
        configurationBuilder.Properties<DateTime?>().HavePrecision(7);

        base.ConfigureConventions(configurationBuilder);
    }
}

// =====================================================================
// STILL MISSING — do not ship without these. Listed here so they are
// impossible to forget:
//
// 1. RLS SESSION CONTEXT INTERCEPTOR.  Nothing yet executes
//       EXEC sp_set_session_context @key=N'TenantId', @value=...
//    on connection open. Until that exists, every query runs with no tenant
//    context and — now that 05_rls_security.sql is fail-CLOSED — returns
//    zero rows. That is the correct failure: silent emptiness beats silent
//    cross-tenant reads. It is the next thing to build.
//
// 2. DOMAIN EVENT DISPATCH.  AggregateRoot.DomainEvents must be drained in
//    SaveChangesAsync AFTER the transaction commits, never before.
//
// 3. NO OPTIMISTIC CONCURRENCY TOKEN.  core.notification has no rowversion
//    column, so two dispatchers acting on the same notification (one calling
//    MarkSent, another RecordFailure) will last-write-wins and one outcome is
//    lost. The state machine rejects illegal transitions in memory but cannot
//    see the other process. Fixing it means adding a rowversion column to the
//    table — a schema decision, so it is flagged rather than assumed.
// =====================================================================
