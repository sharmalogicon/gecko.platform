using System.Threading.RateLimiting;
using Gecko.Data;
using Gecko.Identity.Application.Auth;
using Gecko.Identity.Endpoints;
using Gecko.Identity.Endpoints.Admin;
using Gecko.Identity.Infrastructure.Auth;
using Gecko.Identity.Infrastructure.Persistence;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Gecko.Identity;

/// <summary>
/// The module's only entry point from the host. Gecko.Api calls Add + Map and
/// knows nothing else about what is inside.
/// </summary>
public static class IdentityModule
{
    /// <summary>Login gecko_app: every ordinary request. RLS-scoped, cannot read password hashes, cannot DELETE.</summary>
    public const string AppConnectionName = "IdentityApp";

    /// <summary>Login gecko_system: login, invitation acceptance, password reset, provisioning ONLY.</summary>
    public const string SystemConnectionName = "IdentitySystem";

    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        var appConnection = RequireConnectionString(configuration, AppConnectionName);
        var systemConnection = RequireConnectionString(configuration, SystemConnectionName);

        services.AddGeckoTenancy();

        services.AddDbContext<IdentityDbContext>((sp, options) => options
            .UseSqlServer(appConnection)
            .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>(), sp.GetRequiredService<AuditStampInterceptor>()));

        services.AddDbContext<IdentitySystemDbContext>((sp, options) => options
            .UseSqlServer(systemConnection)
            .AddInterceptors(sp.GetRequiredService<SystemSessionInterceptor>(), sp.GetRequiredService<AuditStampInterceptor>()));

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => TryDecodeKeyLength(o.SigningKey) >= 32, $"{JwtOptions.Section}:SigningKey must be base64 of at least 32 bytes. Set it with dotnet user-secrets.")
            .ValidateOnStart();
        services.AddOptions<LockoutOptions>()
            .Bind(configuration.GetSection(LockoutOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RefreshTokenOptions>()
            .Bind(configuration.GetSection(RefreshTokenOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<AccessTokenIssuer>();
        services.AddSingleton<TenantDbContextFactory>();
        services.AddScoped<ClaimsBuilder>();
        services.AddScoped<Contracts.IUserDirectory, Application.Directory.UserDirectory>();
        services.AddScoped<AuthEventWriter>();
        services.AddScoped<SessionService>();
        services.AddScoped<LoginService>();
        services.AddScoped<InvitationAcceptanceService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
            {
                // Keep "sub", "tid", "rol" as written. The default remaps "sub" to a
                // SOAP-era URI and ClaimsTenantContext would never find it.
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = jwt.Value.Audience,
                    IssuerSigningKey = AccessTokenIssuer.SigningKey(jwt.Value),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = GeckoClaimTypes.UserId,
                    RoleClaimType = GeckoClaimTypes.Role,
                };
            });
        services.AddAuthorization();

        // Credential stuffing (dev_03 has a 12-address burst from one IP): cap attempts per client IP.
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, http =>
            {
                var permits = http.RequestServices.GetRequiredService<IOptions<LockoutOptions>>().Value.LoginAttemptsPerMinutePerIp;
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1) });
            });
        });

        return services;
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAuthEndpoints();

        endpoints.MapGroup("/api")
            .MapBranchEndpoints()
            .MapRoleEndpoints()
            .MapUserEndpoints()
            .MapInvitationEndpoints()
            .MapApiTokenEndpoints()
            .MapSubscriptionEndpoints()
            .MapAuditEndpoints()
            .MapLookupEndpoints();

        return endpoints;
    }

    private static string RequireConnectionString(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"ConnectionStrings:{name} is not set. Set it with: dotnet user-secrets set \"ConnectionStrings:{name}\" \"...\" --project Gecko.Api");

    private static int TryDecodeKeyLength(string base64)
    {
        try { return Convert.FromBase64String(base64).Length; }
        catch (FormatException) { return 0; }
    }
}
