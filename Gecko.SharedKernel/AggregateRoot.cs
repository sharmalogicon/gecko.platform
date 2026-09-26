namespace Gecko.SharedKernel;

/// <summary>
/// An aggregate root is the ONLY object outside code may hold a reference to
/// and mutate. Everything inside its boundary changes through its methods,
/// so its invariants can never be violated from the outside.
///
/// Shared by every module. Each module owns its own aggregate roots — e.g.
/// Notification: Notification, OutboxMessage, NotificationEvent,
/// NotificationTemplate.
///
/// Repositories are defined per aggregate root, never per table.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>
    /// Facts raised since this aggregate was loaded. The Unit of Work drains
    /// these AFTER SaveChanges succeeds — never before, or we would publish
    /// events for a transaction that later rolled back.
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
