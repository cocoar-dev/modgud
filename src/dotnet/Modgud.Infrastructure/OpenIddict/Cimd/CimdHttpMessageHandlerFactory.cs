using System.Net.Sockets;

namespace Modgud.Infrastructure.OpenIddict.Cimd;

/// <summary>
/// Builds the primary <see cref="SocketsHttpHandler"/> for the CIMD metadata
/// fetcher with the SSRF defences baked into the transport itself:
/// <list type="bullet">
///   <item>redirects disabled — a 30x to an internal host can't be followed;</item>
///   <item>a <see cref="SocketsHttpHandler.ConnectCallback"/> that resolves
///   DNS, refuses any non-public resolved address
///   (<see cref="CimdIpGuard"/>), and connects to exactly that validated IP —
///   closing the DNS-rebinding gap a name-then-connect check would leave;</item>
///   <item>tight connect/response timeouts.</item>
/// </list>
/// TLS still validates against the request hostname (SNI + cert), so pinning
/// the socket to the pre-validated IP doesn't weaken certificate checking.
/// </summary>
internal static class CimdHttpMessageHandlerFactory
{
    public static SocketsHttpHandler Create() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        // Don't reuse pooled connections across hosts longer than needed; a
        // CIMD fetch is a one-shot lookup, not a chatty API.
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        var addresses = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken)
            .ConfigureAwait(false);

        foreach (var address in addresses)
        {
            if (CimdIpGuard.IsBlocked(address)) continue;

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
            $"CIMD metadata fetch refused: '{host}' did not resolve to a routable public address.");
    }
}
