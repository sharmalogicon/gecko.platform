using Gecko.Notification.Domain.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Gecko.Notification.Infrastructure.Persistence.Configurations.Core;

/// <summary>
/// core.notification_event — append-only store of every inbound domain event.
///
/// Contrast with NotificationConfiguration: this entity is still anemic
/// (public setters, plain strings), so the mapping is almost all defaults.
/// That difference is the point — the ceremony in the Notification mapping
/// buys the invariants; where there are none to protect, there is no ceremony.
/// </summary>
public sealed class NotificationEventConfiguration : IEntityTypeConfiguration<NotificationEvent>
{
    public void Configure(EntityTypeBuilder<NotificationEvent> builder)
    {
        builder.ToTable("notification_event", "core");

        // BIGINT IDENTITY, CLUSTERED — the opposite choice to core.notification.
        // Sequential inserts append to the end of the B-tree with no page
        // splits, which is what you want for a pure append-only log.
        builder.HasKey(e => e.Id).IsClustered();

        builder.Property(e => e.Id)
               .HasColumnName("id")
               .ValueGeneratedOnAdd();          // <- the database owns this one

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(e => e.EventId).HasColumnName("event_id").IsRequired();

        builder.Property(e => e.EventTypeCode)
               .HasColumnName("event_type_code")
               .HasMaxLength(60)
               .IsUnicode(false)
               .IsRequired();

        builder.Property(e => e.SourceModule)
               .HasColumnName("source_module")
               .HasConversion<string>()
               .HasMaxLength(20)
               .IsUnicode(false)
               .IsRequired();

        // NVARCHAR(MAX). Holds the raw publisher payload — container numbers,
        // vessel names, Thai consignee names. Unicode stays on.
        builder.Property(e => e.EventDataJson)
               .HasColumnName("event_data_json")
               .IsRequired();

        builder.Property(e => e.CorrelationId).HasColumnName("correlation_id");

        // Unlike core.notification.created_at, nothing in the domain sets this,
        // so the column DEFAULT SYSUTCDATETIME() is allowed to win.
        builder.Property(e => e.ReceivedAt)
               .HasColumnName("received_at")
               .HasDefaultValueSql("SYSUTCDATETIME()")
               .ValueGeneratedOnAdd()
               .IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.ReceivedAt })
               .HasDatabaseName("ix_notification_event__tenant_received")
               .IsDescending(false, true);

        builder.HasIndex(e => e.EventId)
               .HasDatabaseName("ix_notification_event__event_id");
    }
}
