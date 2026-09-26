using System.Security.Claims;
using Gecko.SharedKernel;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Gecko.Identity.Infrastructure.Auth;

public sealed record AccessTokenClaims(
    Guid UserId,
    Guid TenantId,
    string UserType,
    IReadOnlyCollection<Guid> Branches,
    IReadOnlyCollection<string> Modules,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> BranchPermissions);

public sealed record IssuedAccessToken(string Token, DateTimeOffset ExpiresAt);

public sealed class AccessTokenIssuer(IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JsonWebTokenHandler _handler = new();

    public static SymmetricSecurityKey SigningKey(JwtOptions jwt) => new(Convert.FromBase64String(jwt.SigningKey));

    public IssuedAccessToken Issue(AccessTokenClaims claims)
    {
        var jwt = options.Value;
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(jwt.AccessTokenMinutes);

        var identity = new ClaimsIdentity(
        [
            new Claim(GeckoClaimTypes.UserId, claims.UserId.ToString()),
            new Claim(GeckoClaimTypes.TenantId, claims.TenantId.ToString()),
            new Claim(GeckoClaimTypes.UserType, claims.UserType.ToLowerInvariant()),
            .. claims.Branches.Select(b => new Claim(GeckoClaimTypes.Branch, b.ToString())),
            .. claims.Modules.Select(m => new Claim(GeckoClaimTypes.Module, m)),
            .. claims.Roles.Select(r => new Claim(GeckoClaimTypes.Role, r)),
            .. claims.Permissions.Select(p => new Claim(GeckoClaimTypes.Permission, p)),
            .. BranchPermissionClaims(claims.BranchPermissions),
        ]);

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Subject = identity,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKey(jwt), SecurityAlgorithms.HmacSha256),
        });

        return new IssuedAccessToken(token, expires);
    }

    /// <summary>
    /// One <c>bpm</c> claim per distinct permission set, not per (permission, branch)
    /// pair: the same role at three depots is one claim instead of 120, which is the
    /// difference between a 1 KB token and one that will not fit in a header.
    /// </summary>
    private static IEnumerable<Claim> BranchPermissionClaims(IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> byBranch) =>
        byBranch
            .Where(b => b.Value.Count > 0)
            .GroupBy(b => string.Join(',', b.Value.Order(StringComparer.Ordinal)), StringComparer.Ordinal)
            .Select(set => new Claim(
                GeckoClaimTypes.BranchPermission,
                BranchPermissionClaim.Format(set.Key.Split(','), set.Select(b => b.Key).Order())));
}
