using System.Net.Sockets;

namespace Modgud.Infrastructure.Http;

/// <summary>
/// Builds the primary <see cref="SocketsHttpHandler"/> for any server-side
/// fetch of an operator- or admin-supplied URL, with the SSRF defences baked
/// into the transport itself:
/// <list type="bullet">
///   <item>redirects disabled — a 30x to an internal host can't be followed;</item>
///   <item>a <see cref="SocketsHttpHandler.ConnectCallback"/> that resolves
///   DNS, refuses any non-public resolved address
///   (<see cref="SsrfIpGuard"/>), and connects to exactly that validated IP —
///   closing the DNS-rebinding gap a name-then-connect check would leave;</item>
///   <item>tight connect/response timeouts.</item>
/// </list>
/// TLS still validates against the request hostname (SNI + cert), so pinning
/// the socket to the pre-validated IP doesn't weaken certificate checking.
///
/// <para>Used by every admin-supplied-URL fetcher: CIMD client metadata, SAML
/// IdP metadata, and the dynamic OIDC discovery/backchannel. A realm admin is a
/// lower-trust tier than the platform operator, so "an admin configured it" is
/// NOT a reason to skip this guard.</para>
/// </summary>
public static class SsrfSafeHttpHandlerFactory
{
    /// <param name="purpose">Short human-readable label for the fetch (e.g.
    /// "SAML metadata fetch"), used in the refusal message so a blocked
    /// connection is diagnosable from the log alone.</param>
    public static SocketsHttpHandler Create(string purpose) => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        // Don't reuse pooled connections across hosts longer than needed; these
        // are one-shot metadata lookups, not chatty APIs.
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = (context, cancellationToken) =>
            ConnectAsync(purpose, context, cancellationToken),
    };

    private static async ValueTask<Stream> ConnectAsync(
        string purpose, SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        var addresses = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken)
            .ConfigureAwait(false);

        foreach (var address in addresses)
        {
            if (SsrfIpGuard.IsBlocked(address)) continue;

            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        // Every resolved address was non-public (or the host resolved to
        // nothing). Refuse — this is the SSRF block path.
        throw new IOException(
            $"{purpose} refused: '{host}' did not resolve to a routable public address.");
    }
}
