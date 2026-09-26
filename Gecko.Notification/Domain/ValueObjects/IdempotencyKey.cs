using System.Security.Cryptography;
using System.Text;

namespace Gecko.Notification.Domain.ValueObjects;

/// <summary>
/// SHA-256 over (tenant + event + channel + recipient + locale), hex-encoded.
///
/// Maps to core.notification.idempotency_key VARCHAR(64), backed by the unique
/// index uq_notification__idempotency_key.
///
/// WHY: Azure Service Bus guarantees at-LEAST-once delivery, so the same
/// CONTAINER_GATE_IN event WILL sometimes arrive twice — on redelivery after a
/// consumer crash, or during a scale-out rebalance. Without this, the depot's
/// customer gets the same LINE message twice and stops trusting the alerts.
///
/// The check is at the DATABASE, not in application code: two dispatcher
/// instances can process the duplicate concurrently, both see "no existing
/// row", and both insert. Only the unique index resolves that race. The
/// application catches the duplicate-key violation and treats it as success —
/// the notification already exists, which is exactly the desired end state.
///
/// The five components are precisely the things that make a notification
/// distinct. Same event to the same person on LINE in Thai AND in English is
/// two notifications; the same one twice is a duplicate.
/// </summary>
public readonly record struct IdempotencyKey
{
    public const int Length = 64;

    public string Value { get; }

    private IdempotencyKey(string value) => Value = value;

    /// <summary>Derives the key from the five identifying components.</summary>
    public static IdempotencyKey Compute(
        Guid tenantId,
        Guid eventId,
        ChannelCode channel,
        RecipientAddress recipient,
        Locale locale)
    {
        // '|' is a safe separator: it cannot occur in a GUID, and using one at
        // all prevents the classic collision where ("AB","C") and ("A","BC")
        // hash identically.
        var material = $"{tenantId:N}|{eventId:N}|{channel.Value}|{recipient.Value}|{locale.Value}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new IdempotencyKey(Convert.ToHexStringLower(hash));
    }

    /// <summary>Rehydrates a key already persisted. Does not recompute.</summary>
    public static IdempotencyKey FromExisting(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != Length)
            throw new ArgumentException(
                $"Idempotency key must be exactly {Length} hex characters.", nameof(value));

        return new IdempotencyKey(value.ToLowerInvariant());
    }

    public override string ToString() => Value;

    public static implicit operator string(IdempotencyKey key) => key.Value;
}
