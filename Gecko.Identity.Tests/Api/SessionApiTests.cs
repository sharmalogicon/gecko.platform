using System.Net;
using System.Net.Http.Json;
using Gecko.Data;
using Gecko.Identity.Endpoints;
using Gecko.Identity.Endpoints.Admin;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Tests.Api;

internal static class SessionHttp
{
    public const string CookieName = "__Secure-gecko_rt";

    public static string? RefreshCookieHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(CookieName + "=", StringComparison.Ordinal) && !v.StartsWith(CookieName + "=;", StringComparison.Ordinal))
            : null;

    public static string RefreshCookieValue(HttpResponseMessage response) =>
        RefreshCookieHeader(response)?.Split(';')[0][(CookieName.Length + 1)..]
        ?? throw new InvalidOperationException("No refresh cookie in response.");

    public static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, CancellationToken ct) =>
        await client.PostAsJsonAsync("/auth/login", new { email, password = ApiFactory.Password }, ct);

    public static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string cookie, CancellationToken ct) =>
        client.SendAsync(WithCookie(HttpMethod.Post, "/auth/refresh", cookie), ct);

    public static Task<HttpResponseMessage> LogoutAsync(HttpClient client, string cookie, CancellationToken ct) =>
        client.SendAsync(WithCookie(HttpMethod.Post, "/auth/logout", cookie), ct);

    private static HttpRequestMessage WithCookie(HttpMethod method, string url, string cookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        return request;
    }
}

/// <summary>ADR-006 step 4: refresh rotation, reuse detection, logout.</summary>
[Collection(ApiCollection.Name)]
public class SessionApiTests(ApiFactory api)
{
    private const string User = "yard.lcb@sct.co.th";

    [Fact]
    public async Task Login_sets_a_hardened_refresh_cookie_and_keeps_the_token_out_of_the_body()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = api.CreateSessionClient();

        var response = await SessionHttp.LoginAsync(client, User, ct);

        var header = SessionHttp.RefreshCookieHeader(response)!;
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/auth", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SessionHttp.RefreshCookieValue(response), await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Refresh_rotates_and_replaying_an_old_cookie_revokes_the_whole_chain()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = api.CreateSessionClient();

        var first = SessionHttp.RefreshCookieValue(await SessionHttp.LoginAsync(client, User, ct));

        var refreshed = await SessionHttp.RefreshAsync(client, first, ct);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.False(string.IsNullOrEmpty((await refreshed.Content.ReadFromJsonAsync<LoginResponse>(ct))!.AccessToken));
        var second = SessionHttp.RefreshCookieValue(refreshed);
        Assert.NotEqual(first, second);

        var third = SessionHttp.RefreshCookieValue(await SessionHttp.RefreshAsync(client, second, ct));

        // A thief replays the FIRST cookie...
        Assert.Equal(HttpStatusCode.Unauthorized, (await SessionHttp.RefreshAsync(client, first, ct)).StatusCode);

        // ...and the legitimate tip of the chain is now dead too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SessionHttp.RefreshAsync(client, third, ct)).StatusCode);

        await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
        Assert.True(await db.AuthEvents.AnyAsync(e => e.EventType == "TOKEN_REUSE_DETECTED" && e.EmailAttempted == User && e.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-2), ct));
    }

    [Fact]
    public async Task A_signed_in_user_changes_their_password_with_a_new_one_typed_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        const string who = "gate2.lcb@sct.co.th";
        const string fresh = "A-new-Password-2026";
        using var anonymous = api.CreateClient();
        Task<HttpResponseMessage> Login(string password) => anonymous.PostAsJsonAsync("/auth/login", new { email = who, password }, ct);
        async Task<HttpClient> SignedIn(string password)
        {
            var body = await (await Login(password)).Content.ReadFromJsonAsync<LoginResponse>(ct);
            var client = api.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", body!.AccessToken);
            return client;
        }

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/auth/password", new { newPassword = fresh, confirmPassword = fresh }, ct)).StatusCode);

        using var user = await SignedIn(ApiFactory.Password);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await user.PostAsJsonAsync("/auth/password", new { newPassword = fresh, confirmPassword = fresh + "x" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await user.PostAsJsonAsync("/auth/password", new { newPassword = "short", confirmPassword = "short" }, ct)).StatusCode);

        try
        {
            Assert.Equal(HttpStatusCode.NoContent,
                (await user.PostAsJsonAsync("/auth/password", new { newPassword = fresh, confirmPassword = fresh }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Login(fresh)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(ApiFactory.Password)).StatusCode);
        }
        finally
        {
            // Back to the fixture password, through the same endpoint.
            Assert.Equal(HttpStatusCode.NoContent,
                (await user.PostAsJsonAsync("/auth/password", new { newPassword = ApiFactory.Password, confirmPassword = ApiFactory.Password }, ct)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await Login(ApiFactory.Password)).StatusCode);

        await using var db = TestDatabase.ForTenant(TestDatabase.Sct);
        Assert.True(await db.AuthEvents.AnyAsync(e => e.EventType == "PASSWORD_CHANGED" && e.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-2), ct));
    }

    [Fact]
    public async Task Logout_kills_the_session_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = api.CreateSessionClient();
        var cookie = SessionHttp.RefreshCookieValue(await SessionHttp.LoginAsync(client, User, ct));

        Assert.Equal(HttpStatusCode.NoContent, (await SessionHttp.LogoutAsync(client, cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SessionHttp.RefreshAsync(client, cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SessionHttp.LogoutAsync(client, cookie, ct)).StatusCode);
    }

    [Fact]
    public async Task Disabling_a_user_ends_their_existing_session()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = api.CreateSessionClient();
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);
        const string email = "gate1.lkr@sct.co.th";

        var cookie = SessionHttp.RefreshCookieValue(await SessionHttp.LoginAsync(client, email, ct));
        var user = (await admin.GetFromJsonAsync<PagedResult<UserSummary>>($"/api/users?search={email}", ct))!.Items.Single();

        try
        {
            await admin.PostAsync($"/api/users/{user.UserId}/disable", null, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, (await SessionHttp.RefreshAsync(client, cookie, ct)).StatusCode);
        }
        finally
        {
            await admin.PostAsync($"/api/users/{user.UserId}/enable", null, ct);
        }
    }

    [Fact]
    public async Task Refresh_without_a_cookie_is_401()
    {
        using var client = api.CreateSessionClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/auth/refresh", null, TestContext.Current.CancellationToken)).StatusCode);
    }
}

/// <summary>Two browser tabs refresh with the same cookie at the same moment: one wins, the other must not log the user out.</summary>
public class RotationGraceTests(GraceWindowApiFactory api) : IClassFixture<GraceWindowApiFactory>
{
    [Fact]
    public async Task Reuse_inside_the_grace_window_is_refused_without_revoking_the_chain()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = api.CreateSessionClient();

        var first = SessionHttp.RefreshCookieValue(await SessionHttp.LoginAsync(client, "gate1.lcb@sct.co.th", ct));
        var second = SessionHttp.RefreshCookieValue(await SessionHttp.RefreshAsync(client, first, ct));

        Assert.Equal(HttpStatusCode.Unauthorized, (await SessionHttp.RefreshAsync(client, first, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SessionHttp.RefreshAsync(client, second, ct)).StatusCode);
    }
}
