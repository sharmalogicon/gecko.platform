using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Gecko.Identity.Tests.Api;

/// <summary>One login attempt per IP per minute, so the second attempt from the same client is a 429.</summary>
public sealed class OneLoginPerMinuteApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Identity:Lockout:LoginAttemptsPerMinutePerIp", "1");
    }
}

/// <summary>
/// Production runs behind Caddy/IIS and next start, so the API's TCP peer is always
/// loopback. X-Forwarded-For from a loopback peer must pick the login rate-limit
/// partition; from any other peer it must be ignored.
/// </summary>
public sealed class ForwardedHeadersTests(OneLoginPerMinuteApiFactory factory) : IClassFixture<OneLoginPerMinuteApiFactory>
{
    // An empty login: rejected by validation, never touches the database, but the
    // rate limiter has already counted it.
    private async Task<int> LoginAsync(string peer, string? forwardedFor)
    {
        var context = await factory.Server.SendAsync(http =>
        {
            http.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            http.Request.Method = HttpMethods.Post;
            http.Request.Path = "/auth/login";
            http.Request.ContentType = "application/json";
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"email":"","password":""}"""));
            if (forwardedFor is not null) http.Request.Headers["X-Forwarded-For"] = forwardedFor;
        });
        return context.Response.StatusCode;
    }

    [Fact]
    public async Task Loopback_proxy_forwarded_clients_get_separate_partitions()
    {
        Assert.NotEqual(429, await LoginAsync("127.0.0.1", "203.0.113.10"));
        Assert.Equal(429, await LoginAsync("127.0.0.1", "203.0.113.10"));   // same client: limited
        Assert.NotEqual(429, await LoginAsync("127.0.0.1", "203.0.113.11")); // other client: own partition
    }

    [Fact]
    public async Task Two_loopback_hops_still_resolve_the_real_client()
    {
        // Caddy -> next start -> API: the Next hop may add loopback after the client.
        Assert.NotEqual(429, await LoginAsync("127.0.0.1", "198.51.100.20, 127.0.0.1"));
        Assert.Equal(429, await LoginAsync("127.0.0.1", "198.51.100.20"));
    }

    [Fact]
    public async Task Forwarded_header_from_a_non_loopback_peer_is_ignored()
    {
        Assert.NotEqual(429, await LoginAsync("192.0.2.50", "203.0.113.99"));
        Assert.Equal(429, await LoginAsync("192.0.2.50", "203.0.113.98"));   // spoofed header does not buy a new partition
    }
}
