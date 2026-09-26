namespace Gecko.Notification.Domain.ValueObjects;

/// <summary>
/// A dispatch channel identifier — LINE, EMAIL, SMS, WHATSAPP, TELEGRAM,
/// ZALO, VIBER, SLACK, TEAMS, WEBHOOK, FTP.
///
/// Maps to core.notification.channel_code VARCHAR(20), denormalised from
/// lookup.channel_registry.code.
///
/// WHY A TYPE AND NOT A string: ChannelCode and EventTypeCode are both
/// strings, and both appear in the Create signature. A value object makes
/// swapping them a compile error instead of a production bug that sends
/// gate-in alerts to a channel called "CONTAINER_GATE_IN".
/// </summary>
public readonly record struct ChannelCode
{
    public const int MaxLength = 20;

    public string Value { get; }

    private ChannelCode(string value) => Value = value;

    public static ChannelCode From(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Channel code is required.", nameof(value));

        var normalised = value.Trim().ToUpperInvariant();

        if (normalised.Length > MaxLength)
            throw new ArgumentException(
                $"Channel code '{normalised}' exceeds {MaxLength} characters.", nameof(value));

        return new ChannelCode(normalised);
    }

    public override string ToString() => Value;

    public static implicit operator string(ChannelCode code) => code.Value;
}
