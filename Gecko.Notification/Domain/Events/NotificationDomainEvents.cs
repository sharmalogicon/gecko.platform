using Gecko.SharedKernel;
using Gecko.Notification.Domain.Enums;
using Gecko.Notification.Domain.ValueObjects;

namespace Gecko.Notification.Domain.Events;

/// <summary>
/// A notification record was created and is ready to be queued.
/// The application layer turns this into the outbox.message row —
/// which is how the Transactional Outbox pattern stays out of the aggregate.
/// </summary>
public sealed record NotificationCreated(
    Guid NotificationId,
    Guid TenantId,
    Guid EventId,
    EventTypeCode EventTypeCode,
    ChannelCode ChannelCode,
    Priority Priority,
    DateTime? ScheduledAt,
    Guid? CorrelationId,
    DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>Handed to Service Bus on the priority topic for its tier.</summary>
public sealed record NotificationQueued(
    Guid NotificationId,
    Guid TenantId,
    ChannelCode ChannelCode,
    Priority Priority,
    DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>Provider accepted the message. Not yet proof of delivery.</summary>
public sealed record NotificationSent(
    Guid NotificationId,
    Guid TenantId,
    ChannelCode ChannelCode,
    string ProviderMessageId,
    DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>Provider webhook confirmed the message reached the recipient.</summary>
public sealed record NotificationDelivered(
    Guid NotificationId,
    Guid TenantId,
    ChannelCode ChannelCode,
    DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>
/// An attempt failed. <paramref name="WillRetry"/> distinguishes a transient
/// failure now scheduled for another attempt from a permanent one.
/// </summary>
public sealed record NotificationFailed(
    Guid NotificationId,
    Guid TenantId,
    ChannelCode ChannelCode,
    string ErrorCode,
    string Reason,
    int RetryCount,
    bool WillRetry,
    DateTime? NextRetryAt,
    DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Retries are exhausted. This is what triggers channel fallback in UC-05 —
/// LINE gave up, so try SMS. Ops alerting hangs off this event.
/// </summary>
public sealed record NotificationDeadLettered(
    Guid NotificationId,
    Guid TenantId,
    ChannelCode ChannelCode,
    RecipientAddress Recipient,
    string LastErrorCode,
    int RetryCount,
    DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Permanent rejection by the provider — dead mailbox, blocked number,
/// recipient left the LINE account. Retrying is pointless and, on some
/// providers, damages sender reputation. The address should be quarantined.
/// </summary>
public sealed record NotificationBounced(
    Guid NotificationId,
    Guid TenantId,
    ChannelCode ChannelCode,
    RecipientAddress Recipient,
    string Reason,
    DateTime OccurredAtUtc) : IDomainEvent;
