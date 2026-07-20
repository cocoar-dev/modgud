using System.Net;
using Modgud.Infrastructure.Http;

namespace Modgud.Tests.Unit.Infrastructure.Http;

/// <summary>
/// Pins the SSRF address classifier shared by every admin-supplied-URL fetch
/// (CIMD client metadata, SAML IdP metadata, OIDC discovery/backchannel). Each
/// resolves DNS itself and refuses any non-public resolved address before
/// connecting; a gap here would re-open the server-side-request-forgery surface
/// all three are gated behind.
/// </summary>
public class SsrfIpGuardTests
{
    [Theory]
    // IPv4 special-use / non-routable
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("100.64.0.1")]      // CGNAT
    [InlineData("127.0.0.1")]       // loopback
    [InlineData("169.254.169.254")] // link-local (cloud metadata!)
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("192.0.0.1")]       // IETF protocol assignments
    [InlineData("192.0.2.1")]       // TEST-NET-1
    [InlineData("198.18.0.1")]      // benchmarking
    [InlineData("198.51.100.1")]    // TEST-NET-2
    [InlineData("203.0.113.1")]     // TEST-NET-3
    [InlineData("224.0.0.1")]       // multicast
    [InlineData("239.255.255.255")] // multicast
    [InlineData("255.255.255.255")] // broadcast
    // IPv6 special-use
    [InlineData("::1")]             // loopback
    [InlineData("::")]              // unspecified
    [InlineData("fe80::1")]         // link-local
    [InlineData("fc00::1")]         // ULA
    [InlineData("fd00::1")]         // ULA
    [InlineData("ff02::1")]         // multicast
    [InlineData("2001:db8::1")]     // documentation
    // IPv4-mapped IPv6 must be unwrapped + judged on the embedded IPv4
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    public void Blocks_non_public_addresses(string address)
    {
        Assert.True(SsrfIpGuard.IsBlocked(IPAddress.Parse(address)),
            $"{address} is non-public and must be blocked.");
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]   // example.com
    [InlineData("142.250.72.14")]   // google
    [InlineData("2001:4860:4860::8888")] // google public DNS v6
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")] // example.com v6
    public void Allows_routable_public_addresses(string address)
    {
        Assert.False(SsrfIpGuard.IsBlocked(IPAddress.Parse(address)),
            $"{address} is a routable public address and must be allowed.");
    }
}
