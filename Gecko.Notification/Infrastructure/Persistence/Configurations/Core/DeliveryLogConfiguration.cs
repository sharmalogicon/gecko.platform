using Gecko.Notification.Domain.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Gecko.Notification.Infrastructure.Persistence.Configurations.Core;

/// <summary>
/// core.delivery_log — one row per delivery ATTEMPT (not per notification).
///
/// NOTE ON THE MISSING RELATIONSHIP: NotificationId is deliberately mapped as
/// a plain column with no HasOne/WithMany. The schema has no foreign keys by
/// design (07_core_tables.sql, header) so the module can be split out later
/// without a cross-database FK to unpick. Declaring a navigation here would
/// make EF try to create that FK on the next migration and quietly undo the
/// decision.
///
/// It also has no tenant_id, so RLS does not cover it — see the note in the
/// DbContext about why that matters.
/// </summary>
public sealed class DeliveryLogConfiguration : IEntityTypeConfiguration<DeliveryLog>
{
    public void Configure(EntityTypeBuilder<DeliveryLog> builder)
    {
        builder.ToTable("delivery_log", "core");

        builder.HasKey(d => d.Id).IsClustered();

        builder.Property(d => d.Id)
               .HasColumnName("id")
               .ValueGeneratedOnAdd();

        builder.Property(d => d.NotificationId)
               .HasColumnName("notification_id")
               .IsRequired();

        builder.Property(d => d.AttemptNumber)
               .HasColumnName("attempt_number")
               .IsRequired();

        builder.Property(d => d.Status)
               .HasColumnName("status")
               .HasConversion<string>()
               .HasMaxLength(15)
               .IsUnicode(false)
               .IsRequired();

        // Renamed by database/10_fix_delivery_log_column.sql.
        // Until that script runs, this mapping fails — deliberately, and loudly.
        builder.Property(d => d.ProviderMessageId)
               .HasColumnName("provider_message_id")
               .HasMaxLength(200);

        builder.Property(d => d.ProviderResponse).HasColumnName("provider_response");

        builder.Property(d => d.ErrorCode)
               .HasColumnName("error_code")
               .HasMaxLength(50)
               .IsUnicode(false);

        builder.Property(d => d.ErrorMessage)
               .HasColumnName("error_message")
               .HasMaxLength(1000);

        builder.Property(d => d.HttpStatusCode).HasColumnName("http_status_code");
        builder.Property(d => d.DurationMs).HasColumnName("duration_ms");

        builder.Property(d => d.DispatcherInstance)
               .HasColumnName("dispatcher_instance")
               .HasMaxLength(100)
               .IsUnicode(false);

        builder.Property(d => d.AttemptedAt)
               .HasColumnName("attempted_at")
               .IsRequired();

        builder.HasIndex(d => d.NotificationId)
               .HasDatabaseName("ix_delivery_log__notification")
               .IncludeProperties(d => new { d.AttemptNumber, d.Status, d.AttemptedAt, d.DurationMs });

        builder.HasIndex(d => d.AttemptedAt)
               .HasDatabaseName("ix_delivery_log__failed_recent")
               .IsDescending(true)
               .HasFilter("status IN ('FAILED', 'TIMEOUT', 'BOUNCED')");
    }
}
