namespace Gecko.SharedKernel;

/// <summary>
/// Base class for entities — objects with a stable identity that persists
/// across state changes. Two entities are equal if their Ids are equal,
/// regardless of what their other properties currently hold.
///
/// WHY: a Notification that has moved CREATED -> SENT is still the SAME
/// notification. Reference equality and value equality both give the wrong
/// answer here; identity equality gives the right one.
/// </summary>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : notnull
{
    public TId Id { get; protected set; } = default!;

    public bool Equals(Entity<TId>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        // Different concrete types are never equal, even with matching Ids.
        if (GetType() != other.GetType()) return false;

        // Transient entities (not yet persisted) are only equal by reference.
        if (IsTransient() || other.IsTransient()) return false;

        return EqualityComparer<TId>.Default.Equals(Id, other.Id);
    }

    public override bool Equals(object? obj) => Equals(obj as Entity<TId>);

    public override int GetHashCode() =>
        IsTransient() ? base.GetHashCode() : EqualityComparer<TId>.Default.GetHashCode(Id);

    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);

    private bool IsTransient() => EqualityComparer<TId>.Default.Equals(Id, default!);
}
