namespace Gecko.Notification.Domain.ValueObjects;

/// <summary>
/// Where a notification is actually delivered — an email address, an E.164
/// phone number, a LINE userId, a Slack channel, a webhook URL.
///
/// Maps to core.notification.recipient_address NVARCHAR(500).
///
/// DELIBERATELY NOT channel-validated here. Whether "U4af4980629..." is a
/// valid LINE userId is the LINE dispatcher's business, not the aggregate's —
/// the domain would have to know all 11 providers' formats and would need
/// changing every time one is added. The aggregate guarantees only what is
/// true for every channel: present, trimmed, within the column width.
/// Channel-specific validation belongs in IChannelDispatcher (Strategy).
/// </summary>
public readonly record struct RecipientAddress
{
    public const int MaxLength = 500;

    public string Value { get; }

    private RecipientAddress(string value) => Value = value;

    public static RecipientAddress From(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Recipient address is required.", nameof(value));

        var trimmed = value.Trim();

        if (trimmed.Length > MaxLength)
            throw new ArgumentException(
                $"Recipient address exceeds {MaxLength} characters.", nameof(value));

        return new RecipientAddress(trimmed);
    }

    /// <summary>
    /// Masked form for logs and audit trails. Never log the raw address —
    /// recipient addresses are personal data under Thai PDPA and UAE law,
    /// and support staff read these logs.
    /// </summary>
    public string ToMasked()
    {
        var v = Value;
        if (v.Length <= 4) return new string('*', v.Length);

        var at = v.IndexOf('@');
        if (at > 1)   // email: keep first char + domain
            return $"{v[0]}{new string('*', at - 1)}{v[at..]}";

        return $"{v[..2]}{new string('*', v.Length - 4)}{v[^2..]}";
    }

    public override string ToString() => Value;
}
