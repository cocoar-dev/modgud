using Microsoft.AspNetCore.Builder;

namespace Modgud.Api;

/// <summary>
/// Configures <see cref="ForwardedHeadersOptions"/> for Modgud's "reverse proxy
/// terminates TLS" topology, with a genuinely fail-closed default. PROD-03.
///
/// <para>Behind a TLS-terminating proxy, Modgud derives the public scheme and host
/// of every request from <c>X-Forwarded-Proto</c>/<c>-Host</c> — and the per-realm
/// OAuth issuer plus every outbound user-facing link are then built from that host.
/// So the forwarded headers must be trusted <b>only</b> from the real reverse proxy.
/// The <c>ProxyAllowedNetworks</c> env var (comma-separated CIDR list, e.g.
/// <c>"10.0.0.0/8,192.168.1.0/24"</c>) pins that range.</para>
///
/// <para><b>The gotcha this guards against.</b> ASP.NET Core's
/// <c>ForwardedHeadersMiddleware</c> only runs its known-proxy check when at least
/// one entry is configured:
/// <code>var checkKnownIps = KnownIPNetworks.Count > 0 || KnownProxies.Count > 0;</code>
/// If BOTH lists are empty it <b>skips</b> the check and applies forwarded headers
/// from <em>any</em> source — i.e. empty lists mean <b>trust-all</b>, not reject-all.
/// (<c>KnownNetworks</c> and <c>KnownIPNetworks</c> share one backing list, so adding
/// to either keeps the check active.) Therefore, when no real proxy range is
/// configured in Production, we install an unroutable RFC 5737 sentinel network: the
/// check stays active, no real peer ever matches it, and every forwarded header is
/// rejected — a real fail-closed default rather than the silent trust-all the empty
/// case would otherwise produce.</para>
/// </summary>
public static class ForwardedHeadersTrust
{
    /// <summary>
    /// RFC 5737 TEST-NET-1 (<c>192.0.2.0/24</c>) — reserved for documentation and
    /// guaranteed never to be globally routable. Used purely as a never-matching
    /// sentinel so the framework's known-IP check stays active (see remarks on
    /// <see cref="ForwardedHeadersTrust"/>). It must never match a real connecting
    /// peer; were it to, the only consequence would be that peer's forwarded headers
    /// being trusted.
    /// </summary>
    public static readonly Microsoft.AspNetCore.HttpOverrides.IPNetwork RejectAllSentinel
        = new(System.Net.IPAddress.Parse("192.0.2.0"), 24);

    /// <summary>
    /// Applies the forwarded-headers trust policy to <paramref name="options"/>.
    /// Extracted from <c>Program.cs</c> so the fail-closed default is unit-testable
    /// against the real <c>ForwardedHeadersMiddleware</c>.
    /// </summary>
    /// <param name="options">The options instance to mutate.</param>
    /// <param name="isProduction"><c>true</c> for the published image; <c>false</c> for local dev.</param>
    /// <param name="allowedNetworksCsv">The raw <c>ProxyAllowedNetworks</c> value (may be null/empty).</param>
    public static void Configure(ForwardedHeadersOptions options, bool isProduction, string? allowedNetworksCsv)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                                 | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                                 | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        if (isProduction)
        {
            var configured = 0;
            if (!string.IsNullOrWhiteSpace(allowedNetworksCsv))
            {
                foreach (var entry in allowedNetworksCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Microsoft.AspNetCore.HttpOverrides.IPNetwork.TryParse(entry, out var network))
                    {
                        options.KnownNetworks.Add(network);
                        configured++;
                    }
                }
            }

            // Fail closed. With no real proxy range configured, an EMPTY known-IP
            // list makes the middleware trust forwarded headers from anywhere (see
            // remarks). The sentinel keeps the check active so every forwarded
            // header is rejected instead.
            if (configured == 0)
                options.KnownNetworks.Add(RejectAllSentinel);

            // Cap the X-Forwarded-* depth — defence against a chain of
            // attacker-controlled headers being treated as trusted.
            options.ForwardLimit = 1;
        }
        else
        {
            // Dev convenience: trust loopback so localhost reverse-proxies
            // (Vite, Docker port-forwards) work without ENV setup.
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("127.0.0.0"), 8));
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.IPv6Loopback, 128));
        }
    }
}
