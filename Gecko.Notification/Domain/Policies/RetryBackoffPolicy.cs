namespace Gecko.Notification.Domain.Policies;

/// <summary>
/// Exponential backoff schedule for failed delivery attempts —
/// 5s, 10s, 20s, 40s, 80s, then dead-letter.
///
/// Lives in the DOMAIN, not in the dispatcher, because "how many times do we
/// try before giving up" is a business rule the depot cares about, not an
/// implementation detail of the LINE client. Putting it here means it is
/// testable without Azure and identical across all 11 channel dispatchers.
///
/// This is the third of the four safety nets in UC-05:
///   1. Polly in-process retry     (transient blips, milliseconds)
///   2. Circuit breaker via Redis  (provider is down, stop hammering it)
///   3. THIS — durable server-side retry via ScheduledEnqueueTimeUtc
///   4. Dead-letter + channel fallback (SMS when LINE has given up)
/// </summary>
public static class RetryBackoffPolicy
{
    /// <summary>Attempts before a notification is dead-lettered.</summary>
    public const int MaxRetries = 5;

    private static readonly TimeSpan[] Schedule =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(40),
        TimeSpan.FromSeconds(80)
    ];

    /// <summary>
    /// Delay before retry number <paramref name="retryCount"/> (1-based).
    /// Counts beyond the schedule clamp to the final interval rather than
    /// growing unbounded — a caller that ignores <see cref="HasAttemptsLeft"/>
    /// gets a sane delay instead of an overflow.
    /// </summary>
    public static TimeSpan DelayFor(int retryCount)
    {
        if (retryCount < 1)
            throw new ArgumentOutOfRangeException(nameof(retryCount), "Retry count is 1-based.");

        var index = Math.Min(retryCount, Schedule.Length) - 1;
        return Schedule[index];
    }

    /// <summary>True while the notification may still be retried.</summary>
    public static bool HasAttemptsLeft(int retryCount) => retryCount < MaxRetries;

    /// <summary>Absolute UTC instant of the next attempt.</summary>
    public static DateTime NextAttemptAt(int retryCount, DateTime utcNow) =>
        utcNow + DelayFor(retryCount);
}
