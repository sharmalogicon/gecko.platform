using Microsoft.AspNetCore.HttpOverrides;

namespace Gecko.Api;

/// <summary>
/// X-Forwarded-For / X-Forwarded-Proto from the reverse proxies in front of the API
/// (Caddy or IIS, then next start). Trusts loopback peers only.
/// </summary>
public static class GeckoForwardedHeaders
{
    /// <summary>Two loopback hops: the TLS proxy and the Next.js one-origin proxy.</summary>
    public const int DefaultForwardLimit = 2;

    public static ForwardedHeadersOptions Options(IConfiguration configuration)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = configuration.GetValue("ForwardedHeaders:ForwardLimit", DefaultForwardLimit),
        };

        // Replace the defaults explicitly so the trust list is visible here and nowhere else.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("127.0.0.0/8"));
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("::1/128"));
        return options;
    }
}
