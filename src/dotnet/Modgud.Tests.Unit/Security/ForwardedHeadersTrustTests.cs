using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modgud.Api;
using Xunit;

namespace Modgud.Tests.Unit.Security;

/// <summary>
/// PROD-03 — forwarded-header trust must fail CLOSED. ASP.NET Core's
/// <see cref="ForwardedHeadersMiddleware"/> only runs its known-proxy check when at
/// least one network/proxy is configured (<c>checkKnownIps = KnownIPNetworks.Count
/// &gt; 0 || KnownProxies.Count &gt; 0</c>); with both lists empty it SKIPS the check
/// and trusts forwarded headers from anywhere. Modgud derives the public scheme/host
/// — and thus the per-realm OAuth issuer and every outbound link — from those
/// headers, so a trust-all default behind a bypassable proxy lets an attacker spoof
/// the issuer/host. These tests drive the real middleware against the configuration
/// <c>Program.cs</c> wires up, locking in that an empty <c>ProxyAllowedNetworks</c>
/// in Production genuinely REJECTS forwarded headers.
/// </summary>
public class ForwardedHeadersTrustTests
{
    // A real proxy forwards all three headers; include them so the known-IP check
    // (which is tied to the X-Forwarded-For chain iteration) is actually exercised.
    private const string ForwardedClient = "198.51.100.7"; // RFC 5737 TEST-NET-2
    private const string ForwardedProto = "https";
    private const string ForwardedHost = "public.example.com";
    private const string KestrelScheme = "http";
    private const string KestrelHost = "kestrel.internal";

    /// <summary>
    /// Builds the middleware from <see cref="ForwardedHeadersTrust.Configure"/> exactly
    /// as Program.cs does, pushes a request from <paramref name="remoteIp"/> carrying
    /// (potentially spoofed) X-Forwarded-* headers through it, and returns the
    /// scheme/host the application code downstream would actually observe.
    /// </summary>
    private static async Task<(string Scheme, string Host)> RunAsync(
        bool isProduction, string? allowedNetworksCsv, string remoteIp)
    {
        var options = new ForwardedHeadersOptions();
        ForwardedHeadersTrust.Configure(options, isProduction, allowedNetworksCsv);

        var middleware = new ForwardedHeadersMiddleware(
            next: _ => Task.CompletedTask,
            loggerFactory: NullLoggerFactory.Instance,
            options: Options.Create(options));

        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteIp);
        ctx.Request.Scheme = KestrelScheme;
        ctx.Request.Host = new HostString(KestrelHost);
        ctx.Request.Headers["X-Forwarded-For"] = ForwardedClient;
        ctx.Request.Headers["X-Forwarded-Proto"] = ForwardedProto;
        ctx.Request.Headers["X-Forwarded-Host"] = ForwardedHost;

        await middleware.Invoke(ctx);
        return (ctx.Request.Scheme, ctx.Request.Host.Value);
    }

    [Fact]
    public async Task Production_with_no_allowlist_rejects_forwarded_headers()
    {
        // The core regression: an UNSET ProxyAllowedNetworks must not silently
        // become trust-all. The spoofed headers from an arbitrary peer are ignored
        // and the app sees the real (Kestrel) scheme/host.
        var (scheme, host) = await RunAsync(isProduction: true, allowedNetworksCsv: null, remoteIp: "203.0.113.9");

        Assert.Equal(KestrelScheme, scheme);
        Assert.Equal(KestrelHost, host);
    }

    [Fact]
    public async Task Production_with_blank_allowlist_rejects_forwarded_headers()
    {
        // A whitespace-only / comma-only value parses to zero networks — same
        // fail-closed outcome as unset.
        var (scheme, host) = await RunAsync(isProduction: true, allowedNetworksCsv: " , ", remoteIp: "203.0.113.9");

        Assert.Equal(KestrelScheme, scheme);
        Assert.Equal(KestrelHost, host);
    }

    [Fact]
    public async Task Production_trusts_forwarded_headers_from_a_peer_inside_the_allowed_range()
    {
        var (scheme, host) = await RunAsync(isProduction: true, allowedNetworksCsv: "10.0.0.0/8", remoteIp: "10.1.2.3");

        Assert.Equal(ForwardedProto, scheme);
        Assert.Equal(ForwardedHost, host);
    }

    [Fact]
    public async Task Production_rejects_forwarded_headers_from_a_peer_outside_the_allowed_range()
    {
        // An allowlist IS configured, but the peer isn't in it — headers ignored.
        var (scheme, host) = await RunAsync(isProduction: true, allowedNetworksCsv: "10.0.0.0/8", remoteIp: "203.0.113.9");

        Assert.Equal(KestrelScheme, scheme);
        Assert.Equal(KestrelHost, host);
    }

    [Fact]
    public async Task Development_trusts_a_loopback_proxy()
    {
        var (scheme, host) = await RunAsync(isProduction: false, allowedNetworksCsv: null, remoteIp: "127.0.0.1");

        Assert.Equal(ForwardedProto, scheme);
        Assert.Equal(ForwardedHost, host);
    }

    [Fact]
    public void Reject_all_sentinel_is_an_unroutable_rfc5737_network_that_matches_no_real_peer()
    {
        Assert.False(ForwardedHeadersTrust.RejectAllSentinel.Contains(System.Net.IPAddress.Parse("203.0.113.9")));
        Assert.False(ForwardedHeadersTrust.RejectAllSentinel.Contains(System.Net.IPAddress.Parse("10.1.2.3")));
        Assert.True(ForwardedHeadersTrust.RejectAllSentinel.Contains(System.Net.IPAddress.Parse("192.0.2.50")));
    }
}
