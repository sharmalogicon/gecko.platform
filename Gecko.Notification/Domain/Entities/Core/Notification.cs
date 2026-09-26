using Gecko.SharedKernel;
using Gecko.Notification.Domain.Enums;
using Gecko.Notification.Domain.Events;
using Gecko.Notification.Domain.Policies;
using Gecko.Notification.Domain.ValueObjects;

namespace Gecko.Notification.Domain.Entities.Core;

/// <summary>
/// core.notification — the central record for every notification dispatched,
/// and the aggregate root that owns the delivery lifecycle.
///
/// One inbound event fans out to MANY of these (per channel × recipient × locale):
///     CONTAINER_GATE_IN
///       -> LINE  to consignee      (th)
///       -> EMAIL to consignee      (en)
///       -> SLACK to ops team       (en)
///       -> SMS   to customs broker (th)
///
/// STATE MACHINE (LLD-02 §9.1). Every mutation goes through a method that
/// asserts the transition is legal, so an illegal one is an immediate,
/// testable exception rather than a corrupt row found weeks later in a
/// delivery report:
///
///     CREATED ──> QUEUED ──> PROCESSING ──> SENT ──> DELIVERED ──> READ
///                                │            │
///                                │            └──> BOUNCED   (permanent)
///                                ├──> RETRY ──┘ (back to PROCESSING)
///                                ├──> FAILED              (permanent, terminal)
///                                └──> DLQ                 (retries exhausted)
///
/// Properties have PRIVATE setters by design. `notification.Status = SENT`
/// does not compile — the only way to reach SENT is MarkSent(), which will
/// not run unless the notification is actually PROCESSING.
/// </summary>
public sealed class Notification : AggregateRoot<Guid>, ITenantScoped
{
    // ---------------------------------------------------------------
    // Identity & routing
    // ---------------------------------------------------------------
    public Guid TenantId { get; private set; }
    public Guid EventId { get; private set; }
    public EventTypeCode EventTypeCode { get; private set; }
    public ChannelCode ChannelCode { get; private set; }
    public RecipientAddress RecipientAddress { get; private set; }
    public string? RecipientType { get; private set; }
    public Locale Locale { get; private set; }
    public Priority Priority { get; private set; }

    // ---------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------
    public NotificationStatus Status { get; private set; }

    // ---------------------------------------------------------------
    // Content
    // ---------------------------------------------------------------
    public Guid? TemplateId { get; private set; }
    public string? RenderedSubject { get; private set; }
    public string? RenderedBody { get; private set; }

    // ---------------------------------------------------------------
    // Delivery guarantees
    // ---------------------------------------------------------------
    public IdempotencyKey IdempotencyKey { get; private set; }
    public string? ProviderMessageId { get; private set; }
    public int RetryCount { get; private set; }
    public DateTime? NextRetryAt { get; private set; }

    // ---------------------------------------------------------------
    // Timestamps (UTC) — passed in, never read from DateTime.UtcNow, so the
    // state machine is deterministically testable without a clock abstraction.
    // ---------------------------------------------------------------
    public DateTime? ScheduledAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? QueuedAt { get; private set; }
    public DateTime? SentAt { get; private set; }
    public DateTime? DeliveredAt { get; private set; }
    public DateTime? ReadAt { get; private set; }
    public DateTime? FailedAt { get; private set; }

    public Guid? CorrelationId { get; private set; }

    /// <summary>EF Core materialisation only. Never call this from domain code.</summary>
    private Notification() { }

    // ===============================================================
    // FACTORY
    // ===============================================================

    /// <summary>
    /// Creates a notification in CREATED. The idempotency key is derived here
    /// rather than accepted as a parameter — a caller cannot supply a wrong or
    /// stale key, so the uniqueness guarantee cannot be bypassed.
    /// </summary>
    /// <param name="scheduledAt">
    /// null sends immediately; a future UTC instant defers it (FR-SB-01).
    /// </param>
    public static Notification Create(
        Guid tenantId,
        Guid eventId,
        EventTypeCode eventTypeCode,
        ChannelCode channelCode,
        RecipientAddress recipientAddress,
        Locale locale,
        Priority priority,
        DateTime utcNow,
        string? recipientType = null,
        DateTime? scheduledAt = null,
        Guid? correlationId = null)
    {
        if (tenantId == Guid.Empty)
            throw new DomainException("TenantId is required — RLS cannot isolate an empty tenant.");

        if (eventId == Guid.Empty)
            throw new DomainException("EventId is required for correlation back to the source event.");

        if (scheduledAt is { } when && when <= utcNow)
            throw new DomainException(
                $"ScheduledAt must be in the future (got {when:O}, now {utcNow:O}). " +
                "Pass null to send immediately.");

        var notification = new Notification
        {
            Id = Guid.CreateVersion7(),   // time-ordered: index-friendly inserts
            TenantId = tenantId,
            EventId = eventId,
            EventTypeCode = eventTypeCode,
            ChannelCode = channelCode,
            RecipientAddress = recipientAddress,
            RecipientType = recipientType,
            Locale = locale,
            Priority = priority,
            Status = NotificationStatus.CREATED,
            IdempotencyKey = IdempotencyKey.Compute(
                                 tenantId, eventId, channelCode, recipientAddress, locale),
            ScheduledAt = scheduledAt,
            CreatedAt = utcNow,
            RetryCount = 0,
            CorrelationId = correlationId
        };

        notification.Raise(new NotificationCreated(
            notification.Id, tenantId, eventId, eventTypeCode, channelCode,
            priority, scheduledAt, correlationId, utcNow));

        return notification;
    }

    // ===============================================================
    // TRANSITIONS
    // ===============================================================

    /// <summary>
    /// Attaches rendered content. Only valid before the notification is queued —
    /// once it is on the bus a dispatcher may already be reading it, and
    /// changing the body underneath would send content nobody approved.
    /// </summary>
    public void AttachRenderedContent(Guid templateId, string? subject, string body)
    {
        if (Status is not (NotificationStatus.CREATED or NotificationStatus.RETRY))
            throw new DomainException(
                $"Content can only be attached before dispatch (status is {Status}).");

        if (string.IsNullOrWhiteSpace(body))
            throw new DomainException("Rendered body cannot be empty.");

        TemplateId = templateId;
        RenderedSubject = subject;
        RenderedBody = body;
    }

    /// <summary>Placed on the Service Bus priority topic for its tier.</summary>
    public void MarkQueued(DateTime utcNow)
    {
        Transition(NotificationStatus.QUEUED);

        if (RenderedBody is null)
            throw new DomainException(
                "Cannot queue a notification with no rendered body — " +
                "call AttachRenderedContent first.");

        QueuedAt = utcNow;
        NextRetryAt = null;

        Raise(new NotificationQueued(Id, TenantId, ChannelCode, Priority, utcNow));
    }

    /// <summary>A dispatcher has picked the message up and is calling the provider.</summary>
    public void MarkProcessing(DateTime utcNow)
    {
        Transition(NotificationStatus.PROCESSING);
        NextRetryAt = null;
    }

    /// <summary>
    /// The provider accepted the message. This is acceptance, NOT delivery —
    /// LINE returning 200 means LINE has it, not that the driver saw it.
    /// DELIVERED comes later, from a webhook.
    /// </summary>
    public void MarkSent(string providerMessageId, DateTime utcNow)
    {
        Transition(NotificationStatus.SENT);

        SentAt = utcNow;
        ProviderMessageId = string.IsNullOrWhiteSpace(providerMessageId)
            ? null
            : providerMessageId.Trim();

        Raise(new NotificationSent(Id, TenantId, ChannelCode, ProviderMessageId ?? string.Empty, utcNow));
    }

    /// <summary>Provider webhook confirmed the message reached the recipient.</summary>
    public void MarkDelivered(DateTime utcNow)
    {
        // Webhooks are unordered and duplicated. A repeat DELIVERED is a
        // no-op, not an error — otherwise a provider retry becomes a 500.
        if (Status is NotificationStatus.DELIVERED or NotificationStatus.READ) return;

        Transition(NotificationStatus.DELIVERED);
        DeliveredAt = utcNow;

        Raise(new NotificationDelivered(Id, TenantId, ChannelCode, utcNow));
    }

    /// <summary>Recipient opened the message, where the channel reports it.</summary>
    public void MarkRead(DateTime utcNow)
    {
        if (Status is NotificationStatus.READ) return;

        Transition(NotificationStatus.READ);
        ReadAt = utcNow;
    }

    /// <summary>
    /// Records a failed delivery attempt and decides what happens next:
    ///
    ///   retryable   + attempts left  -> RETRY, NextRetryAt set by backoff
    ///   retryable   + attempts spent -> DLQ   (triggers channel fallback)
    ///   permanent                    -> FAILED (no point retrying a 400)
    ///
    /// The decision lives here rather than in the dispatcher so all 11 channel
    /// dispatchers behave identically and it can be unit-tested without Azure.
    /// </summary>
    public void RecordFailure(string errorCode, string reason, bool isRetryable, DateTime utcNow)
    {
        if (Status is not (NotificationStatus.PROCESSING or NotificationStatus.QUEUED))
            throw new InvalidStateTransitionException(
                nameof(Notification), Status.ToString(), "FAILED/RETRY/DLQ");

        FailedAt = utcNow;

        if (!isRetryable)
        {
            Status = NotificationStatus.FAILED;
            NextRetryAt = null;

            Raise(new NotificationFailed(
                Id, TenantId, ChannelCode, errorCode, reason,
                RetryCount, WillRetry: false, NextRetryAt: null, utcNow));
            return;
        }

        RetryCount++;

        if (RetryBackoffPolicy.HasAttemptsLeft(RetryCount))
        {
            Status = NotificationStatus.RETRY;
            NextRetryAt = RetryBackoffPolicy.NextAttemptAt(RetryCount, utcNow);

            Raise(new NotificationFailed(
                Id, TenantId, ChannelCode, errorCode, reason,
                RetryCount, WillRetry: true, NextRetryAt, utcNow));
        }
        else
        {
            Status = NotificationStatus.DLQ;
            NextRetryAt = null;

            Raise(new NotificationDeadLettered(
                Id, TenantId, ChannelCode, RecipientAddress, errorCode, RetryCount, utcNow));
        }
    }

    /// <summary>
    /// Permanent rejection reported by the provider — dead mailbox, blocked
    /// number, recipient no longer following the LINE account. Reachable from
    /// SENT because bounces arrive by webhook after acceptance.
    /// </summary>
    public void MarkBounced(string reason, DateTime utcNow)
    {
        if (Status is NotificationStatus.BOUNCED) return;

        Transition(NotificationStatus.BOUNCED);
        FailedAt = utcNow;
        NextRetryAt = null;

        Raise(new NotificationBounced(Id, TenantId, ChannelCode, RecipientAddress, reason, utcNow));
    }

    // ===============================================================
    // STATE MACHINE
    // ===============================================================

    /// <summary>True once no further transition is possible.</summary>
    public bool IsTerminal => Status is
        NotificationStatus.READ or
        NotificationStatus.FAILED or
        NotificationStatus.DLQ or
        NotificationStatus.BOUNCED;

    /// <summary>True while the outbox/scheduler should keep looking at this row.</summary>
    public bool IsAwaitingRetry =>
        Status is NotificationStatus.RETRY && NextRetryAt is not null;

    /// <summary>
    /// The single gate every status change passes through. Keeping the legal
    /// edges in ONE switch is what makes the machine reviewable — a reader can
    /// verify the whole protocol here instead of hunting for assignments.
    /// </summary>
    private void Transition(NotificationStatus to)
    {
        if (!IsTransitionAllowed(Status, to))
            throw new InvalidStateTransitionException(
                nameof(Notification), Status.ToString(), to.ToString());

        Status = to;
    }

    public static bool IsTransitionAllowed(NotificationStatus from, NotificationStatus to) =>
        (from, to) switch
        {
            (NotificationStatus.CREATED,    NotificationStatus.QUEUED)     => true,

            (NotificationStatus.QUEUED,     NotificationStatus.PROCESSING) => true,

            (NotificationStatus.PROCESSING, NotificationStatus.SENT)       => true,
            (NotificationStatus.PROCESSING, NotificationStatus.BOUNCED)    => true,

            // Retry loops back through the dispatcher.
            (NotificationStatus.RETRY,      NotificationStatus.PROCESSING) => true,
            (NotificationStatus.RETRY,      NotificationStatus.QUEUED)     => true,

            // Post-acceptance webhook outcomes.
            (NotificationStatus.SENT,       NotificationStatus.DELIVERED)  => true,
            (NotificationStatus.SENT,       NotificationStatus.BOUNCED)    => true,
            (NotificationStatus.DELIVERED,  NotificationStatus.READ)       => true,

            // Everything else — including any transition out of a terminal
            // state, and any attempt to skip a step — is rejected.
            _ => false
        };
}
