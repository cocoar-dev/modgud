using System.Net;

namespace Modgud.Authentication.ExtensionMethods;

public static class HttpRequestExtensions
{
    /// <summary>
    /// Resolves the chain of source IPs for the request: every parseable entry
    /// in <c>X-Forwarded-For</c> first, then the direct connection's
    /// <c>RemoteIpAddress</c>. Auth-log + magic-link rate-limiter consume the
    /// first entry, so order matters.
    ///
    /// <para>
    /// X-Forwarded-For values are accepted in either of the standard shapes
    /// produced by reverse proxies:
    /// <list type="bullet">
    ///   <item>Single header value with a comma-separated chain: <c>"1.2.3.4, 5.6.7.8"</c> (nginx, Cloudflare default)</item>
    ///   <item>Multiple header values: <c>["1.2.3.4", "5.6.7.8"]</c></item>
    /// </list>
    /// Both are flattened to the same per-IP list. Unparseable entries are
    /// silently skipped — bad input from a proxy must not take down the
    /// request pipeline.
    /// </para>
    /// </summary>
    public static List<IPAddress> FindSourceIp(this HttpRequest httpRequest)
    {
        var sourceIps = new List<IPAddress>();

        if (httpRequest.Headers.TryGetValue("X-Forwarded-For", out var forwarded))
        {
            foreach (var headerValue in forwarded)
            {
                if (string.IsNullOrWhiteSpace(headerValue))
                    continue;

                foreach (var part in headerValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (IPAddress.TryParse(part, out var ip))
                        sourceIps.Add(ip);
                }
            }
        }

        if (httpRequest.HttpContext.Connection.RemoteIpAddress is { } remote)
            sourceIps.Add(remote);

        return sourceIps;
    }
}
