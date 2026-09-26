using System.Security.Cryptography;
using System.Text;

namespace Gecko.Identity.Application.Auth;

/// <summary>
/// Bearer secrets (invitation tokens, API keys): generated from a CSPRNG, stored
/// only as a hash.
///
/// HASH = SHA-256 over the UTF-16LE bytes of the token string. That is exactly what
/// SQL Server computes for HASHBYTES('SHA2_256', @nvarchar) — and usp_provision_tenant
/// hashes owner invitations that way. Any other encoding and an invitation created by
/// the provisioning script could never be accepted through the API.
/// </summary>
public static class SecretTokens
{
    public const string ApiKeyPrefix = "gk_live_";

    /// <summary>64 upper-case hex chars — the same shape usp_provision_tenant emits (CONVERT(..., CRYPT_GEN_RANDOM(32), 2)).</summary>
    public static string NewInvitationToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>"gk_live_" + 48 lower-case hex. The first 12 chars (gk_live_a1b2) are stored as token_prefix for display.</summary>
    public static string NewApiKey() => ApiKeyPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    public static string DisplayPrefix(string apiKey) => apiKey[..12];

    /// <summary>43 base64url chars (32 bytes). Lives only in an HttpOnly cookie.</summary>
    public static string NewRefreshToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.Unicode.GetBytes(token));
}
