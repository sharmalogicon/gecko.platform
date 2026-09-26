namespace Gecko.SharedKernel;

/// <summary>
/// A fact that has already happened inside the domain, expressed in past tense.
///
/// WHY THIS EXISTS: the aggregate must not know about Service Bus, Redis, the
/// audit logger, or EF Core. It just records "this happened". The application
/// layer drains these after a successful commit and decides what to do —
/// write the outbox row, call IAuditLogger, invalidate a cache key.
///
/// This is what keeps the Observer pattern in LLD-02 §7.3 from turning into
/// the domain calling infrastructure directly.
/// </summary>
public interface IDomainEvent
{
    /// <summary>When the fact occurred (UTC). Set by the aggregate, not the clock.</summary>
    DateTime OccurredAtUtc { get; }
}
