using System.ComponentModel.DataAnnotations;
using Gecko.Data;
using Gecko.Identity.Application.Auth;
using Gecko.Identity.Infrastructure.Auth;
using Gecko.Identity.Infrastructure.Persistence;
using Gecko.Identity.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Gecko.Identity.Endpoints;

/// <param name="Email">The user's e-mail, or their user name — whichever they have (an identifier without '@' is a user name).</param>
public sealed record LoginRequest(
    [property: Required, MaxLength(256)] string Email,
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

/// <summary>The signed-in user's new password, typed twice.</summary>
public sealed record ChangePasswordRequest(
    [property: Required, StringLength(128, MinimumLength = 12, ErrorMessage = "Password must be 12-128 characters.")] string NewPassword,
    [property: Required] string ConfirmPassword);

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
            .WithSummary("Exchange email (or user name) + password for an access token (+ refresh cookie)")
            .WithDescription("`email` takes the e-mail or the user name, whichever the user has. Accounts are created by invitation; there is no self-service signup. A wrong email or user name and a wrong password all return the same 401.");

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

        auth.MapPost("/password", ChangePasswordAsync)
            .RequireAuthorization()
            .Validate<ChangePasswordRequest>()
            .WithSummary("Change your own password: new password + confirmation")
            .WithDescription("Owner 2026-10-10: just the new password typed twice; the caller's access token proves who they are. The old password stops working at once; this session stays signed in.");

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
                return TypedResults.Problem(title: "Invalid email, user name or password.", statusCode: StatusCodes.Status401Unauthorized);
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

    private static async Task<Results<NoContent, ValidationProblem>> ChangePasswordAsync(
        ChangePasswordRequest request, IdentityDbContext db, PasswordHasher hasher, AuthEventWriter events, HttpContext http, CancellationToken ct)
    {
        if (request.NewPassword != request.ConfirmPassword)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["confirmPassword"] = ["The two passwords do not match."] });

        var userId = Guid.Parse(http.User.FindFirst(GeckoClaimTypes.UserId)!.Value);
        var tenantId = Guid.Parse(http.User.FindFirst(GeckoClaimTypes.TenantId)!.Value);
        var fresh = hasher.Hash(request.NewPassword);

        // The old credential is kept as history (is_current = 0), as the rehash on login does.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Credentials
                .Where(c => c.UserId == userId && c.IsCurrent)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsCurrent, false), ct);
            db.Credentials.Add(new Credential
            {
                UserId = userId,
                TenantId = tenantId,
                PasswordHash = fresh.Hash,
                Algorithm = fresh.Algorithm,
                AlgorithmParams = fresh.Parameters,
                IsCurrent = true,
                MustChange = false,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        await events.RecordAsync(Client(http), "PASSWORD_CHANGED", null, tenantId, userId, ct: ct);
        return TypedResults.NoContent();
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
