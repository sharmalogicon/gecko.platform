using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Gecko.Data;

/// <summary>
/// The <c>Idempotency-Key</c> request header: a client that cannot tell "the request
/// failed" from "it succeeded and I lost the answer" sends the same key again and
/// gets the same result, not a second booking or a second receipt.
///
/// The key is stored ON the row the request created (a unique index per tenant), in
/// the SAME transaction — so "the row exists" and "the key is spent" cannot disagree,
/// which a separate key table written before or after the work could. The request's
/// hash goes with it: the same key with a DIFFERENT body is a client bug, answered 422.
/// </summary>
public static class Idempotency
{
    public const string Header = "Idempotency-Key";
    public const int MaxLength = 100;

    /// <summary>The key the caller sent, or null when it sent none. A key that cannot be stored is a problem to send back.</summary>
    public static (string? Key, string? Problem) KeyOf(HttpRequest request)
    {
        var raw = request.Headers[Header].ToString().Trim();
        if (raw.Length == 0) return (null, null);
        return raw.Length > MaxLength || raw.Any(c => c < 0x21 || c > 0x7E)
            ? (null, $"{Header} is at most {MaxLength} printable ASCII characters with no spaces — a UUID is the usual choice.")
            : (raw, null);
    }

    /// <summary>SHA-256 of the request as bound: the same key must come with the same request.</summary>
    public static byte[] HashOf<T>(T request) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request));

    public static bool SameRequest(byte[]? stored, byte[] sent) => stored is not null && stored.AsSpan().SequenceEqual(sent);

    /// <summary>The same key, another body.</summary>
    public static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult DifferentRequest(string key) => TypedResults.Problem(
        title: $"{Header} {key} was already used with a different request.",
        detail: "A key belongs to one request. Send a new key for a new request, or repeat the original request unchanged.",
        statusCode: StatusCodes.Status422UnprocessableEntity);
}
