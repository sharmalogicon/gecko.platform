namespace Gecko.Notification.Domain.ValueObjects;

/// <summary>
/// BCP-47 language tag driving template selection — "th", "en", "th-TH".
///
/// Maps to core.notification.locale VARCHAR(10), default 'en'.
///
/// Matters more here than in most systems: a Laem Chabang depot's drivers
/// read Thai, the liner's operations desk reads English, and the SAME
/// gate-in event fans out to both. Locale is part of the idempotency key
/// precisely because "same event, same channel, same recipient, different
/// language" is two legitimate notifications, not a duplicate.
/// </summary>
public readonly record struct Locale
{
    public const int MaxLength = 10;

    /// <summary>Tenant default when nothing more specific is configured.</summary>
    public static readonly Locale Default = new("en");

    public string Value { get; }

    private Locale(string value) => Value = value;

    public static Locale From(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Default;

        var trimmed = value.Trim();

        if (trimmed.Length > MaxLength)
            throw new ArgumentException(
                $"Locale '{trimmed}' exceeds {MaxLength} characters.", nameof(value));

        // Language subtag lowercase, region subtag uppercase: "th-TH".
        var parts = trimmed.Split('-', 2);
        var normalised = parts.Length == 2
            ? $"{parts[0].ToLowerInvariant()}-{parts[1].ToUpperInvariant()}"
            : parts[0].ToLowerInvariant();

        return new Locale(normalised);
    }

    /// <summary>
    /// Language-only form, for the template fallback chain in LLD-02 §4.4:
    /// "th-TH" -> "th" -> tenant default. Returns null when already language-only.
    /// </summary>
    public Locale? ToLanguageOnly()
    {
        var dash = Value.IndexOf('-');
        return dash < 0 ? null : new Locale(Value[..dash]);
    }

    public override string ToString() => Value;

    public static implicit operator string(Locale locale) => locale.Value;
}
