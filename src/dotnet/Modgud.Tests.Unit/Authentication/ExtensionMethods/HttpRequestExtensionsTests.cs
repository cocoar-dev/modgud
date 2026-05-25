using System.Net;
using Modgud.Authentication.ExtensionMethods;
using Microsoft.AspNetCore.Http;

namespace Modgud.Tests.Unit.Authentication.ExtensionMethods;

/// <summary>
/// Pins <see cref="HttpRequestExtensions.FindSourceIp"/>. The order matters
/// (<c>X-Forwarded-For</c> entries first, then the direct connection IP) — the
/// auth log + magic-link rate limiter both consume the first entry, so a swap
/// would silently leak the proxy IP in audit logs.
/// </summary>
public class HttpRequestExtensionsTests
{
    private static DefaultHttpContext WithRemote(IPAddress? remoteIp)
    {
        var ctx = new DefaultHttpContext();
        if (remoteIp is not null)
            ctx.Connection.RemoteIpAddress = remoteIp;
        return ctx;
    }

    [Fact]
    public void Returns_only_remote_ip_when_no_forwarded_header_present()
    {
        var ctx = WithRemote(IPAddress.Parse("10.0.0.5"));

        var ips = ctx.Request.FindSourceIp();

        Assert.Single(ips);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), ips[0]);
    }

    [Fact]
    public void Returns_empty_when_no_forwarded_header_and_no_remote_ip()
    {
        var ctx = WithRemote(null);

        var ips = ctx.Request.FindSourceIp();

        Assert.Empty(ips);
    }

    [Fact]
    public void Forwarded_for_entries_come_before_remote_ip()
    {
        var ctx = WithRemote(IPAddress.Parse("10.0.0.5"));
        ctx.Request.Headers["X-Forwarded-For"] = "1.2.3.4";

        var ips = ctx.Request.FindSourceIp();

        Assert.Equal(2, ips.Count);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), ips[0]);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), ips[1]);
    }

    [Fact]
    public void Multiple_forwarded_for_header_values_are_each_parsed()
    {
        // ASP.NET Core exposes a multi-value header as separate StringValues entries.
        var ctx = WithRemote(IPAddress.Parse("10.0.0.5"));
        ctx.Request.Headers["X-Forwarded-For"] = new Microsoft.Extensions.Primitives.StringValues(new[] { "1.2.3.4", "5.6.7.8" });

        var ips = ctx.Request.FindSourceIp();

        Assert.Equal(3, ips.Count);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), ips[0]);
        Assert.Equal(IPAddress.Parse("5.6.7.8"), ips[1]);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), ips[2]);
    }

    [Fact]
    public void Ipv6_forwarded_for_is_parsed()
    {
        var ctx = WithRemote(null);
        ctx.Request.Headers["X-Forwarded-For"] = "::1";

        var ips = ctx.Request.FindSourceIp();

        Assert.Single(ips);
        Assert.Equal(IPAddress.IPv6Loopback, ips[0]);
    }

    [Fact]
    public void Comma_separated_forwarded_for_in_a_single_header_is_split()
    {
        // nginx and Cloudflare default: pack the proxy chain into a single
        // header value with comma+space separators. Each parseable IP
        // becomes its own entry, in chain order.
        var ctx = WithRemote(IPAddress.Parse("10.0.0.5"));
        ctx.Request.Headers["X-Forwarded-For"] = "1.2.3.4, 5.6.7.8";

        var ips = ctx.Request.FindSourceIp();

        Assert.Equal(3, ips.Count);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), ips[0]);
        Assert.Equal(IPAddress.Parse("5.6.7.8"), ips[1]);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), ips[2]);
    }

    [Fact]
    public void Unparseable_forwarded_for_entries_are_silently_skipped()
    {
        // Defensive: garbage from a misbehaving proxy must not crash the
        // request. Skip what doesn't parse, keep the rest.
        var ctx = WithRemote(null);
        ctx.Request.Headers["X-Forwarded-For"] = "not-an-ip, 1.2.3.4, also-bad";

        var ips = ctx.Request.FindSourceIp();

        Assert.Single(ips);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), ips[0]);
    }

    [Fact]
    public void Empty_or_whitespace_forwarded_for_entries_are_skipped()
    {
        var ctx = WithRemote(null);
        ctx.Request.Headers["X-Forwarded-For"] = "  , 1.2.3.4 , ,";

        var ips = ctx.Request.FindSourceIp();

        Assert.Single(ips);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), ips[0]);
    }
}
