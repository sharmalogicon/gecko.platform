using Gecko.Notification.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// The entity is called Notification and so is the root namespace segment
// (Gecko.Notification). Inside this namespace, bare `Notification` binds to
// the NAMESPACE, giving CS0118 "is a namespace but is used like a type".
// An alias resolves it unambiguously. This will bite you in every schema.
using NotificationEntity = Gecko.Notification.Domain.Entities.Core.Notification;

namespace Gecko.Notification.Infrastructure.Persistence.Configurations.Core;

/// <summary>
/// core.notification — the reference mapping for this codebase.
///
/// Read this one and the other five schemas become mechanical. It is the only
/// configuration that hits every technique at once:
///   1. a rich aggregate with private setters
///   2. an identity WE generate, not the database
///   3. five value-object converters
///   4. enums stored as strings
///   5. properties that must NOT be persisted at all
///   6. VARCHAR vs NVARCHAR, which is a performance decision here, not cosmetic
/// </summary>
public sealed class NotificationConfiguration : IEntityTypeConfiguration<NotificationEntity>
{
    public void Configure(EntityTypeBuilder<NotificationEntity> builder)
    {
        builder.ToTable("notification", "core");

        // -----------------------------------------------------------
        // KEY
        //
        // IsClustered(false) mirrors `pk_notification PRIMARY KEY NONCLUSTERED`
        // in 07_core_tables.sql. If EF ever generates a migration it must not
        // "helpfully" make this clustered — a clustered index on a GUID would
        // fragment the table on every insert, which is the exact problem the
        // schema author avoided.
        //
        // ValueGeneratedNever() is the line people forget. Notification.Create
        // assigns Guid.CreateVersion7(). Without this, EF assumes the database
        // owns the value (the column has DEFAULT NEWSEQUENTIALID()), sends no
        // Id on INSERT, and the Id you validated in the domain is discarded.
        // -----------------------------------------------------------
        builder.HasKey(n => n.Id).IsClustered(false);

        builder.Property(n => n.Id)
               .HasColumnName("id")
               .ValueGeneratedNever();

        // -----------------------------------------------------------
        // TENANCY & ROUTING
        // -----------------------------------------------------------
        builder.Property(n => n.TenantId)
               .HasColumnName("tenant_id")
               .IsRequired();

        builder.Property(n => n.EventId)
               .HasColumnName("event_id")
               .IsRequired();

        // IsUnicode(false) is NOT cosmetic. The column is VARCHAR(60). If EF
        // sends an NVARCHAR parameter, SQL Server must implicitly convert the
        // COLUMN to match the parameter — which makes the predicate
        // non-sargable and turns index seeks into scans. On the hot path, at
        // 10K/sec, that is the difference between working and not.
        // Every VARCHAR column below gets this. Every NVARCHAR one must not.
        builder.Property(n => n.EventTypeCode)
               .HasColumnName("event_type_code")
               .HasConversion(ValueObjectConverters.EventTypeCode)
               .HasMaxLength(60)
               .IsUnicode(false)
               .IsRequired();

        builder.Property(n => n.ChannelCode)
               .HasColumnName("channel_code")
               .HasConversion(ValueObjectConverters.ChannelCode)
               .HasMaxLength(20)
               .IsUnicode(false)
               .IsRequired();

        // NVARCHAR(500) — Thai names, Arabic addresses. Unicode stays ON.
        builder.Property(n => n.RecipientAddress)
               .HasColumnName("recipient_address")
               .HasConversion(ValueObjectConverters.RecipientAddress)
               .HasMaxLength(500)
               .IsRequired();

        builder.Property(n => n.RecipientType)
               .HasColumnName("recipient_type")
               .HasMaxLength(30)
               .IsUnicode(false);

        builder.Property(n => n.Locale)
               .HasColumnName("locale")
               .HasConversion(ValueObjectConverters.Locale)
               .HasMaxLength(10)
               .IsUnicode(false)
               .IsRequired();

        // -----------------------------------------------------------
        // ENUMS AS STRINGS
        //
        // HasConversion<string>() writes the MEMBER NAME, not the ordinal.
        // Three reasons this is not optional here:
        //   - the columns carry CHECK constraints on string literals
        //     ('CRITICAL','HIGH',...) — an int would violate them outright
        //   - reordering the enum would silently reinterpret every historic row
        //   - support staff reading core.notification at 2am see DLQ, not 9
        // -----------------------------------------------------------
        builder.Property(n => n.Priority)
               .HasColumnName("priority")
               .HasConversion<string>()
               .HasMaxLength(10)
               .IsUnicode(false)
               .IsRequired();

        builder.Property(n => n.Status)
               .HasColumnName("status")
               .HasConversion<string>()
               .HasMaxLength(15)
               .IsUnicode(false)
               .IsRequired();

        // -----------------------------------------------------------
        // CONTENT
        // -----------------------------------------------------------
        builder.Property(n => n.TemplateId).HasColumnName("template_id");

        builder.Property(n => n.RenderedSubject)
               .HasColumnName("rendered_subject")
               .HasMaxLength(500);

        // NVARCHAR(MAX) — no HasMaxLength call means MAX.
        builder.Property(n => n.RenderedBody).HasColumnName("rendered_body");

        // -----------------------------------------------------------
        // DELIVERY GUARANTEES
        // -----------------------------------------------------------
        builder.Property(n => n.IdempotencyKey)
               .HasColumnName("idempotency_key")
               .HasConversion(ValueObjectConverters.IdempotencyKey)
               .HasMaxLength(64)
               .IsUnicode(false)
               .IsRequired();

        builder.Property(n => n.ProviderMessageId)
               .HasColumnName("provider_message_id")
               .HasMaxLength(200);

        builder.Property(n => n.RetryCount).HasColumnName("retry_count").IsRequired();
        builder.Property(n => n.NextRetryAt).HasColumnName("next_retry_at");

        // -----------------------------------------------------------
        // TIMESTAMPS
        //
        // CreatedAt is ValueGeneratedNever for the same reason as Id: the
        // aggregate sets it from the utcNow passed into Create(). Letting the
        // column DEFAULT win would give a timestamp that disagrees with the
        // one already inside the domain event we are about to publish.
        // -----------------------------------------------------------
        builder.Property(n => n.ScheduledAt).HasColumnName("scheduled_at");
        builder.Property(n => n.CreatedAt).HasColumnName("created_at").ValueGeneratedNever().IsRequired();
        builder.Property(n => n.QueuedAt).HasColumnName("queued_at");
        builder.Property(n => n.SentAt).HasColumnName("sent_at");
        builder.Property(n => n.DeliveredAt).HasColumnName("delivered_at");
        builder.Property(n => n.ReadAt).HasColumnName("read_at");
        builder.Property(n => n.FailedAt).HasColumnName("failed_at");

        builder.Property(n => n.CorrelationId).HasColumnName("correlation_id");

        // -----------------------------------------------------------
        // WHAT MUST NOT BE PERSISTED
        //
        // Miss these and EF fails at model-build time with a confusing message
        // about IDomainEvent having no key.
        //
        //   DomainEvents    — in-memory only, drained after commit
        //   IsTerminal      — derived from Status
        //   IsAwaitingRetry — derived from Status + NextRetryAt
        //
        // Deriving rather than storing is deliberate: a stored flag can
        // disagree with the status it describes. A computed one cannot.
        // -----------------------------------------------------------
        builder.Ignore(n => n.DomainEvents);
        builder.Ignore(n => n.IsTerminal);
        builder.Ignore(n => n.IsAwaitingRetry);

        // -----------------------------------------------------------
        // INDEXES — declared so EF's model matches the deployed schema.
        //
        // The unique one is load-bearing, not an optimisation: it is the ONLY
        // thing that stops a duplicate when two dispatcher instances process
        // the same redelivered Service Bus message concurrently. Both check
        // "does it exist?", both see no, both insert, one gets a 2601 —
        // which the repository catches and treats as success.
        //
        // The filtered indexes are declared with HasFilter so a future
        // migration does not silently drop the WHERE clause and bloat them.
        // -----------------------------------------------------------
        builder.HasIndex(n => n.IdempotencyKey)
               .HasDatabaseName("uq_notification__idempotency_key")
               .IsUnique();

        builder.HasIndex(n => new { n.TenantId, n.Status })
               .HasDatabaseName("ix_notification__tenant_status")
               .IncludeProperties(n => new { n.EventTypeCode, n.ChannelCode, n.CreatedAt });

        builder.HasIndex(n => new { n.TenantId, n.CreatedAt })
               .HasDatabaseName("ix_notification__tenant_created")
               .IncludeProperties(n => new { n.EventTypeCode, n.ChannelCode, n.Status });

        builder.HasIndex(n => n.ScheduledAt)
               .HasDatabaseName("ix_notification__scheduled")
               .HasFilter("scheduled_at IS NOT NULL AND status = 'CREATED'");

        builder.HasIndex(n => n.NextRetryAt)
               .HasDatabaseName("ix_notification__retry")
               .HasFilter("status = 'RETRY' AND next_retry_at IS NOT NULL");
    }
}
