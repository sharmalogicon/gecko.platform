using System.ComponentModel.DataAnnotations;
using Gecko.Data;
using Gecko.Identity.Application.Auth;
using Gecko.Identity.Infrastructure.Auth;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Gecko.Identity.Endpoints;

public sealed record LoginRequest(
    [property: Required, EmailAddress, MaxLength(256)] string Email,
    [property: Required, MaxLength(256)] string Password);

/// <summary>The refresh token is NOT in the body — it is set as an HttpOnly cookie the page's JavaScript cannot read.</summary>
public sealed record LoginResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt);

public sealed record MeResponse(
    Guid UserId, Guid TenantId, string? UserType,
    IReadOnlyList<string> Branches, IReadOnlyList<string> Modules,
    IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions,
    IReadOnlyList<string> BranchPermissions);

public sealed record InvitationTokenRequest([property: Required, StringLength(64, MinimumLength = 64)] string Token);

public sealed record AcceptInvitationRequest(
    [property: Required, StringLength(64, MinimumLength = 64)] string Token,
    [property: Required, StringLength(128, MinimumLength = 12, ErrorMessage = "Password must be 12-128 characters.")] string Password,
    [property: MaxLength(200)] string? FullName = null);

internal static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "identity-login";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .Validate<LoginRequest>()
            .RequireRateLimiting(LoginRateLimitPolicy)
            .WithSummary("Exchange email + password for an access token (+ refresh cookie)")
            .WithDescription("Accounts are created by invitation; there is no self-service signup. A wrong email and a wrong password both return the same 401.");

        auth.MapPost("/refresh", RefreshAsync)
            .AllowAnonymous()
            .WithSummary("New access token from the refresh cookie; rotates the cookie")
            .WithDescription("Uses the HttpOnly cookie set by /auth/login — Swagger UI sends it automatically. Presenting an already-rotated cookie revokes the whole session chain.");

        auth.MapPost("/logout", LogoutAsync)
            .AllowAnonymous()
            .WithSummary("Revoke the refresh cookie's session");

        auth.MapGet("/me", Me)
            .RequireAuthorization()
            .WithSummary("The claims in the caller's access token");

        // POST, not GET /invitations/{token}: a bearer token in a URL ends up in
        // proxy logs, browser history and Referer headers.
        auth.MapPost("/invitations/preview", PreviewInvitationAsync)
            .AllowAnonymous()
            .Validate<InvitationTokenRequest>()
            .RequireRateLimiting(LoginRateLimitPolicy)
            .WithSummary("Show who invited you to what, before setting a password");

        auth.MapPost("/invitations/accept", AcceptInvitationAsync)
            .AllowAnonymous()
            .Validate<AcceptInvitationRequest>()
            .RequireRateLimiting(LoginRateLimitPolicy)
            .WithSummary("Set your password, activate the account and sign in");

        return app;
    }

    private static async Task<Results<Ok<LoginResponse>, ProblemHttpResult>> LoginAsync(
        LoginRequest request, LoginService login, HttpContext http, CancellationToken ct)
    {
        var outcome = await login.LoginAsync(new LoginAttempt(request.Email, request.Password, Client(http)), ct);

        switch (outcome.Status)
        {
            case LoginStatus.Succeeded:
                SetRefreshCookie(http, outcome.Session!);
                return TypedResults.Ok(new LoginResponse(outcome.Token!.Token, "Bearer", outcome.Token.ExpiresAt));
            case LoginStatus.PasswordChangeRequired:
                return TypedResults.Problem(
                    title: "Password change required", statusCode: StatusCodes.Status403Forbidden,
                    extensions: new Dictionary<string, object?> { ["code"] = "password_change_required" });
            default:
                return TypedResults.Problem(title: "Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private static async Task<Results<Ok<LoginResponse>, ProblemHttpResult>> RefreshAsync(
        SessionService sessions, HttpContext http, CancellationToken ct)
    {
        if (!http.Request.Cookies.TryGetValue(RefreshTokenOptions.CookieName, out var presented) || string.IsNullOrEmpty(presented))
            return TypedResults.Problem(title: "No session.", statusCode: StatusCodes.Status401Unauthorized);

        var outcome = await sessions.RefreshAsync(presented, Client(http), ct);
        if (outcome.Status != RefreshStatus.Succeeded)
        {
            ClearRefreshCookie(http);
            return TypedResults.Problem(title: "Session expired. Sign in again.", statusCode: StatusCodes.Status401Unauthorized);
        }

        SetRefreshCookie(http, outcome.Session!);
        return TypedResults.Ok(new LoginResponse(outcome.AccessToken!.Token, "Bearer", outcome.AccessToken.ExpiresAt));
    }

    private static async Task<NoContent> LogoutAsync(SessionService sessions, HttpContext http, CancellationToken ct)
    {
        if (http.Request.Cookies.TryGetValue(RefreshTokenOptions.CookieName, out var presented) && !string.IsNullOrEmpty(presented))
            await sessions.EndAsync(presented, Client(http), ct);

        ClearRefreshCookie(http);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<InvitationPreview>, NotFound>> PreviewInvitationAsync(
        InvitationTokenRequest request, InvitationAcceptanceService invitations, CancellationToken ct) =>
        await invitations.PreviewAsync(request.Token, ct) is { } preview ? TypedResults.Ok(preview) : TypedResults.NotFound();

    private static async Task<Results<Ok<LoginResponse>, ProblemHttpResult>> AcceptInvitationAsync(
        AcceptInvitationRequest request, InvitationAcceptanceService invitations, HttpContext http, CancellationToken ct)
    {
        var outcome = await invitations.AcceptAsync(request.Token, request.Password, request.FullName, Client(http), ct);

        switch (outcome.Status)
        {
            case AcceptanceStatus.Accepted:
                SetRefreshCookie(http, outcome.Session!);
                return TypedResults.Ok(new LoginResponse(outcome.Token!.Token, "Bearer", outcome.Token.ExpiresAt));
            case AcceptanceStatus.EmailInUse:
                return TypedResults.Problem(title: "This email already has an account. Sign in instead.", statusCode: StatusCodes.Status409Conflict);
            default:
                return TypedResults.Problem(title: "This invitation is invalid or has expired. Ask for a new one.", statusCode: StatusCodes.Status404NotFound);
        }
    }

    private static MeResponse Me(HttpContext http)
    {
        var user = http.User;
        List<string> All(string type) => user.FindAll(type).Select(c => c.Value).ToList();

        return new MeResponse(
            Guid.Parse(user.FindFirst(GeckoClaimTypes.UserId)!.Value),
            Guid.Parse(user.FindFirst(GeckoClaimTypes.TenantId)!.Value),
            user.FindFirst(GeckoClaimTypes.UserType)?.Value,
            All(GeckoClaimTypes.Branch), All(GeckoClaimTypes.Module), All(GeckoClaimTypes.Role), All(GeckoClaimTypes.Permission),
            All(GeckoClaimTypes.BranchPermission));
    }

    private static ClientInfo Client(HttpContext http) =>
        new(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());

    /// <summary>
    /// HttpOnly: page JavaScript cannot read it, so an XSS cannot steal a 30-day session
    /// (the console's localStorage JWT could be). SameSite=Strict: a cross-site form
    /// cannot trigger /auth/refresh. Path=/auth: never sent to /api/*.
    /// </summary>
    private static void SetRefreshCookie(HttpContext http, IssuedSession session) =>
        http.Response.Cookies.Append(RefreshTokenOptions.CookieName, session.RefreshToken, CookieOptions(session.ExpiresAt));

    private static void ClearRefreshCookie(HttpContext http) =>
        http.Response.Cookies.Delete(RefreshTokenOptions.CookieName, CookieOptions(expires: null));

    private static CookieOptions CookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = RefreshTokenOptions.CookiePath,
        Expires = expires,
        IsEssential = true,
    };
}
