using System.Net;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Microsoft.AspNetCore.Http;

namespace Cocoar.Auth.Tests.Unit.Authentication.ExtensionMethods;

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
    public void Comma_separated_forwarded_for_in_a_single_header_is_NOT_split()
    {
        // Production-code behaviour pin: the helper does NOT split a single
        // "1.2.3.4, 5.6.7.8" header value on commas — IPAddress.Parse will throw.
        // This means upstream proxy chains that pack everything into one header
        // value crash the request. Tracked as a production gap.
        var ctx = WithRemote(null);
        ctx.Request.Headers["X-Forwarded-For"] = "1.2.3.4, 5.6.7.8";

        Assert.Throws<FormatException>(() => ctx.Request.FindSourceIp());
    }
}
