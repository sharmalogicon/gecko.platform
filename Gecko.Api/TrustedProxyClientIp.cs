using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Gecko.Api;

/// <summary>
/// The real caller's IP when the UI's one-origin proxy runs somewhere we cannot
/// list by address (Vercel). Its egress IPs rotate, so X-Forwarded-For trust by
/// peer address (<see cref="GeckoForwardedHeaders"/>) cannot work there: every
/// user would arrive as a Vercel IP and share one login rate-limit partition.
///
/// Instead the proxy sends the client IP together with a shared secret. The IP is
/// believed ONLY when the secret matches, compared in constant time; otherwise
/// both headers are dropped and the TCP peer stands. Anyone calling the API
/// directly cannot pick their own partition without the key.
///
/// Off unless ClientIp:ProxyKey is set (App Service: ClientIp__ProxyKey).
/// The UI side is src/proxy.ts in web.tos.gecko-api (GECKO_PROXY_KEY).
/// </summary>
public static class TrustedProxyClientIp
{
    public const string KeyHeader = "X-Gecko-Proxy-Key";
    public const string ClientIpHeader = "X-Gecko-Client-Ip";
    public const int MinimumKeyLength = 32;

    public static IApplicationBuilder UseTrustedProxyClientIp(this IApplicationBuilder app, IConfiguration configuration)
    {
        var key = configuration["ClientIp:ProxyKey"];
        if (string.IsNullOrWhiteSpace(key)) return app;
        if (key.Length < MinimumKeyLength)
            throw new InvalidOperationException($"ClientIp:ProxyKey must be at least {MinimumKeyLength} characters.");

        var expected = Encoding.UTF8.GetBytes(key);
        return app.Use((http, next) =>
        {
            var headers = http.Request.Headers;
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(headers[KeyHeader].ToString()), expected)
                && IPAddress.TryParse(headers[ClientIpHeader].ToString(), out var client))
            {
                http.Connection.RemoteIpAddress = client;
            }

            headers.Remove(KeyHeader);
            headers.Remove(ClientIpHeader);
            return next(http);
        });
    }
}
