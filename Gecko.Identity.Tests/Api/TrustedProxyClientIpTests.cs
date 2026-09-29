using System.Net;
using System.Text;
using Gecko.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Gecko.Identity.Tests.Api;

/// <summary>One login per IP per minute, and a proxy key configured.</summary>
public sealed class ProxyKeyApiFactory : ApiFactory
{
    public const string Key = "test-proxy-key-0123456789-abcdefghijkl";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Identity:Lockout:LoginAttemptsPerMinutePerIp", "1");
        builder.UseSetting("ClientIp:ProxyKey", Key);
    }
}

/// <summary>
/// Behind Vercel the API's peer is a rotating Vercel IP. The proxy vouches for the
/// real client with X-Gecko-Client-Ip + a shared key; without the right key the
/// header must buy nothing.
/// </summary>
public sealed class TrustedProxyClientIpTests(ProxyKeyApiFactory factory) : IClassFixture<ProxyKeyApiFactory>
{
    // An empty login: rejected by validation, never touches the database, but the
    // rate limiter has already counted it.
    private async Task<int> LoginAsync(string peer, string? clientIp, string? key)
    {
        var context = await factory.Server.SendAsync(http =>
        {
            http.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            http.Request.Method = HttpMethods.Post;
            http.Request.Path = "/auth/login";
            http.Request.ContentType = "application/json";
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"email":"","password":""}"""));
            if (clientIp is not null) http.Request.Headers[TrustedProxyClientIp.ClientIpHeader] = clientIp;
            if (key is not null) http.Request.Headers[TrustedProxyClientIp.KeyHeader] = key;
        });
        return context.Response.StatusCode;
    }

    [Fact]
    public async Task Keyed_proxy_gives_each_client_its_own_partition()
    {
        // Same Vercel egress IP, two different users.
        Assert.NotEqual(429, await LoginAsync("192.0.2.10", "203.0.113.30", ProxyKeyApiFactory.Key));
        Assert.Equal(429, await LoginAsync("192.0.2.10", "203.0.113.30", ProxyKeyApiFactory.Key));   // same user: limited
        Assert.NotEqual(429, await LoginAsync("192.0.2.10", "203.0.113.31", ProxyKeyApiFactory.Key)); // other user: own partition
    }

    [Fact]
    public async Task Client_ip_with_a_wrong_key_is_ignored()
    {
        Assert.NotEqual(429, await LoginAsync("192.0.2.60", "203.0.113.40", "wrong-key-wrong-key-wrong-key-wrong"));
        Assert.Equal(429, await LoginAsync("192.0.2.60", "203.0.113.41", "wrong-key-wrong-key-wrong-key-wrong")); // peer partition
    }

    [Fact]
    public async Task Client_ip_without_a_key_is_ignored()
    {
        Assert.NotEqual(429, await LoginAsync("192.0.2.70", "203.0.113.50", key: null));
        Assert.Equal(429, await LoginAsync("192.0.2.70", "203.0.113.51", key: null));
    }
}
