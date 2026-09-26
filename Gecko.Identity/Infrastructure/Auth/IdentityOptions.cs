using System.ComponentModel.DataAnnotations;

namespace Gecko.Identity.Infrastructure.Auth;

/// <summary>Bound from "Identity:Jwt". SigningKey comes from user-secrets / Key Vault, never appsettings.json.</summary>
public sealed class JwtOptions
{
    public const string Section = "Identity:Jwt";

    [Required] public string Issuer { get; set; } = "gecko-identity";
    [Required] public string Audience { get; set; } = "gecko-platform";

    /// <summary>Base64, at least 32 bytes decoded (HS256).</summary>
    [Required] public string SigningKey { get; set; } = "";

    /// <summary>ADR-006: 15 minutes. Development may raise it to make Swagger testing bearable.</summary>
    [Range(1, 1440)] public int AccessTokenMinutes { get; set; } = 15;
}

/// <summary>Bound from "Identity:Lockout".</summary>
public sealed class LockoutOptions
{
    public const string Section = "Identity:Lockout";

    [Range(1, 100)] public int MaxFailedAttempts { get; set; } = 5;
    [Range(1, 1440)] public int LockoutMinutes { get; set; } = 15;

    /// <summary>POST /auth/login attempts allowed per client IP per minute before 429.</summary>
    [Range(1, 100_000)] public int LoginAttemptsPerMinutePerIp { get; set; } = 10;
}

/// <summary>Bound from "Identity:Session". Refresh tokens (ADR-006).</summary>
public sealed class RefreshTokenOptions
{
    public const string Section = "Identity:Session";

    /// <summary>
    /// __Secure- prefix: browsers refuse the cookie unless it is Secure and set over HTTPS.
    /// (__Host- would be stronger but requires Path=/, and this cookie is scoped to /auth.)
    /// </summary>
    public const string CookieName = "__Secure-gecko_rt";
    public const string CookiePath = "/auth";

    /// <summary>ADR-006: 30 days.</summary>
    [Range(1, 365)] public int RefreshTokenDays { get; set; } = 30;

    /// <summary>
    /// A rotated token presented again within this window is treated as a race (two
    /// tabs refreshing at once), not theft: refused, but the chain is left alone.
    /// After the window, reuse revokes the whole chain.
    /// </summary>
    [Range(0, 300)] public int RotationGraceSeconds { get; set; } = 10;
}
