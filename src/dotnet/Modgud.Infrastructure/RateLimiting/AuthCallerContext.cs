using System.Net;
using Microsoft.AspNetCore.Http;

namespace Modgud.Infrastructure.RateLimiting;

/// <summary>
/// ADR 0007 — who is calling a public auth endpoint, resolved once per request by
/// the caller-context middleware (Modgud.Api) and read by the rate-limit evaluator.
///
/// <para><b>Addresses.</b> <see cref="RemoteAddress"/> is the connection peer after the
/// (unchanged) reverse-proxy policy. <see cref="ForwardedAddress"/> is taken from the
/// dedicated <c>Modgud-Forwarded-For</c> header ONLY when the request carried client
/// authentication of a <em>confidential</em> client that holds the
/// <c>cap:trusted-forwarder</c> capability; it is never read from
/// <c>X-Forwarded-For</c>. <see cref="EffectiveAddress"/> is the forwarded one when
/// present, else the remote one.</para>
///
/// <para><b>Source key.</b> The bucket key derived from the effective address: the
/// address itself for IPv4, the /64 prefix for IPv6 (an attacker owning a /64 must not
/// get unlimited distinct sources). Tests may override it (see the middleware).</para>
///
/// <para>Trust never depends on who owns a client: any realm admin can grant the
/// capability to any confidential client, and it shifts only the source dimensions —
/// target, client and app limits apply to a forwarder unchanged.</para>
/// </summary>
public sealed record AuthCallerContext
{
    public const string ItemsKey = "Modgud.AuthCallerContext";

    /// <summary>Dedicated header a trusted forwarder uses to convey the end user's address.</summary>
    public const string ForwardedForHeader = "Modgud-Forwarded-For";

    public string? RealmSlug { get; init; }
    public Guid? ApplicationId { get; init; }

    /// <summary>The OAuth client that authenticated THIS request (client_secret_basic);
    /// null for anonymous callers. Public clients never authenticate here.</summary>
    public string? ClientId { get; init; }
    public bool ClientIsConfidential { get; init; }
    public IReadOnlySet<string> ClientCapabilities { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public IPAddress? RemoteAddress { get; init; }
    public IPAddress? ForwardedAddress { get; init; }
    public IPAddress? EffectiveAddress => ForwardedAddress ?? RemoteAddress;

    public required string SourceKey { get; init; }

    /// <summary>The effective address matched a realm allowlist entry: the source
    /// dimensions are skipped, every other dimension still applies.</summary>
    public bool SourceAllowlisted { get; init; }

    public bool IsForwarded => ForwardedAddress is not null;

    public static AuthCallerContext? From(HttpContext context) =>
        context.Items.TryGetValue(ItemsKey, out var raw) ? raw as AuthCallerContext : null;

    /// <summary>IPv4 → the address; IPv6 → the /64 prefix (RFC 4291 subnet size).</summary>
    public static string SourceKeyFor(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.ToString();
        var bytes = address.GetAddressBytes();
        return Convert.ToHexStringLower(bytes.AsSpan(0, 8)) + "/64";
    }
}
