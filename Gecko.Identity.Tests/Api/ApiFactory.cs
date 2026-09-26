using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gecko.Identity.Tests.Api;

/// <summary>
/// The real Gecko.Api host in memory, against the local dev gecko_identity.
/// Connection strings and the signing key come from the API's user-secrets
/// (Development environment), exactly as when running from Visual Studio.
///
/// One instance per test run: logins are cached per user so the suite does not
/// burn the login rate limit or add hundreds of auth_event rows.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string Password = "Gecko#Test2026";

    public const string SctAdmin = "admin@sct.co.th";              // TENANT_OWNER, all 28 permissions
    public const string SctOpsLcb = "ops.lcb@sct.co.th";           // branch-scoped only — no tenant-wide permissions
    public const string SssAdmin = "admin@siamshoreside.co.th";   // TENANT_OWNER of another tenant (SSS)

    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _tokens = new();

    /// <summary>0 = any reuse of a rotated refresh token is treated as theft immediately.</summary>
    protected virtual int RotationGraceSeconds => 0;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Identity:Lockout:LoginAttemptsPerMinutePerIp", "10000");
        builder.UseSetting("Identity:Session:RotationGraceSeconds", RotationGraceSeconds.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<HttpClient> ClientForAsync(string email)
    {
        var token = await _tokens.GetOrAdd(email, e => new Lazy<Task<string>>(() => LoginAsync(e))).Value;
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// HTTPS, no automatic cookie jar: session tests read Set-Cookie and send Cookie
    /// themselves, so they can replay an OLD refresh token the way a thief would.
    /// </summary>
    public HttpClient CreateSessionClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });

    private async Task<string> LoginAsync(string email)
    {
        using var anonymous = CreateClient();
        var response = await anonymous.PostAsJsonAsync("/auth/login", new { email, password = Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginBody>())!.AccessToken;
    }

    private sealed record LoginBody(string AccessToken);
}

/// <summary>Production-like grace window, for the two-tabs-refreshing-at-once case.</summary>
public sealed class GraceWindowApiFactory : ApiFactory
{
    protected override int RotationGraceSeconds => 60;
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
