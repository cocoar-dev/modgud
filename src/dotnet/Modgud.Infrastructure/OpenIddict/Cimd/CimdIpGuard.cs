using System.Net;
using System.Net.Sockets;

namespace Modgud.Infrastructure.OpenIddict.Cimd;

/// <summary>
/// SSRF address guard for the CIMD metadata fetcher. Classifies a resolved
/// <see cref="IPAddress"/> as routable-public (allowed) or
/// non-public/special-use (blocked). Pure + dependency-free so the full
/// range table is unit-testable.
///
/// <para>The fetcher resolves DNS itself and connects to a validated IP via
/// <c>SocketsHttpHandler.ConnectCallback</c> — checking the <em>resolved</em>
/// address (not the hostname) and connecting to exactly that address closes
/// the DNS-rebinding window where a name resolves "public" at validation
/// time and "private" at connect time.</para>
/// </summary>
public static class CimdIpGuard
{
    /// <summary>
    /// True if <paramref name="address"/> must NOT be connected to (loopback,
    /// private, link-local, CGNAT, multicast, documentation, or otherwise
    /// non-globally-routable). IPv4-mapped IPv6 addresses are unwrapped and
    /// judged on the embedded IPv4.
    /// </summary>
    public static bool IsBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Unwrap ::ffff:a.b.c.d so a mapped private address can't sneak past
        // the IPv6 checks.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsBlockedV4(address),
            AddressFamily.InterNetworkV6 => IsBlockedV6(address),
            // Anything that isn't plain IPv4/IPv6 (Unix sockets, IPX, …) has
            // no business being an HTTP origin — refuse it.
            _ => true,
        };
    }

    private static bool IsBlockedV4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        uint v = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

        // 0.0.0.0/8        "this network"
        if (InRange(v, 0x00000000, 8)) return true;
        // 10.0.0.0/8       private (RFC 1918)
        if (InRange(v, 0x0A000000, 8)) return true;
        // 100.64.0.0/10    CGNAT (RFC 6598)
        if (InRange(v, 0x64400000, 10)) return true;
        // 127.0.0.0/8      loopback
        if (InRange(v, 0x7F000000, 8)) return true;
        // 169.254.0.0/16   link-local (RFC 3927)
        if (InRange(v, 0xA9FE0000, 16)) return true;
        // 172.16.0.0/12    private (RFC 1918)
        if (InRange(v, 0xAC100000, 12)) return true;
        // 192.0.0.0/24     IETF protocol assignments
        if (InRange(v, 0xC0000000, 24)) return true;
        // 192.0.2.0/24     TEST-NET-1 (documentation)
        if (InRange(v, 0xC0000200, 24)) return true;
        // 192.168.0.0/16   private (RFC 1918)
        if (InRange(v, 0xC0A80000, 16)) return true;
        // 198.18.0.0/15    benchmarking (RFC 2544)
        if (InRange(v, 0xC6120000, 15)) return true;
        // 198.51.100.0/24  TEST-NET-2 (documentation)
        if (InRange(v, 0xC6336400, 24)) return true;
        // 203.0.113.0/24   TEST-NET-3 (documentation)
        if (InRange(v, 0xCB007100, 24)) return true;
        // 224.0.0.0/4      multicast + 240.0.0.0/4 reserved + 255.255.255.255
        if (InRange(v, 0xE0000000, 3)) return true; // 224.0.0.0/3 covers 224–255

        return false;
    }

    private static bool IsBlockedV6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal) return true;   // fe80::/10
        if (address.IsIPv6UniqueLocal) return true; // fc00::/7 (ULA)
        if (address.IsIPv6Multicast) return true;   // ff00::/8
        if (IPAddress.IsLoopback(address)) return true; // ::1
        if (address.Equals(IPAddress.IPv6Any)) return true; // ::

        var bytes = address.GetAddressBytes();

        // 2001:db8::/32 — documentation
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) return true;

        // 64:ff9b::/96 (NAT64) and ::/96 embedded IPv4 — unwrap the trailing
        // 4 bytes and re-check as IPv4 so a tunnelled private target is caught.
        // ::ffff:0:0/96 is handled by IsIPv4MappedToIPv6 upstream; this covers
        // the well-known NAT64 prefix.
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b)
        {
            var embedded = new IPAddress(bytes[12..16]);
            return IsBlockedV4(embedded);
        }

        return false;
    }

    private static bool InRange(uint address, uint network, int prefixBits)
    {
        if (prefixBits == 0) return true;
        uint mask = prefixBits >= 32 ? 0xFFFFFFFF : ~((1u << (32 - prefixBits)) - 1);
        return (address & mask) == (network & mask);
    }
}
