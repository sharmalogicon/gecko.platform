namespace Gecko.Notification.Domain.ValueObjects;

/// <summary>
/// The business event that triggered a notification —
/// CONTAINER_GATE_IN, CONTAINER_GATE_OUT, BOOKING_CONFIRMED, REEFER_ALARM, etc.
///
/// Maps to core.notification.event_type_code VARCHAR(60), denormalised from
/// lookup.event_type.code so dashboards can filter without a join.
/// </summary>
public readonly record struct EventTypeCode
{
    public const int MaxLength = 60;

    public string Value { get; }

    private EventTypeCode(string value) => Value = value;

    public static EventTypeCode From(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Event type code is required.", nameof(value));

        var normalised = value.Trim().ToUpperInvariant();

        if (normalised.Length > MaxLength)
            throw new ArgumentException(
                $"Event type code '{normalised}' exceeds {MaxLength} characters.", nameof(value));

        return new EventTypeCode(normalised);
    }

    public override string ToString() => Value;

    public static implicit operator string(EventTypeCode code) => code.Value;
}
