using System.Net;
using Modgud.Infrastructure.Http;

namespace Modgud.Tests.Unit.Infrastructure.Http;

/// <summary>
/// The operator's exemption from the SSRF guard: parsing of the setting,
/// exact and suffix matching, and the guard itself honouring it at connect
/// time against a loopback listener (blocked by default, reachable when the
/// host is listed).
/// </summary>
public class SsrfAllowListTests
{
    [Theory]
    [InlineData("idp.internal", "idp.internal", true)]
    [InlineData("idp.internal", "IDP.Internal", true)]
    [InlineData("idp.internal", "idp.internal.", true)]
    [InlineData("idp.internal", "other.internal", false)]
    [InlineData("*.corp.example", "keycloak.corp.example", true)]
    [InlineData("*.corp.example", "a.b.corp.example", true)]
    [InlineData("*.corp.example", "corp.example", false)]
    [InlineData("*.corp.example", "notcorp.example", false)]
    [InlineData("idp.internal, keycloak.corp;  *.lab.local", "x.lab.local", true)]
    [InlineData("", "anything", false)]
    [InlineData(null, "anything", false)]
    public void Parses_and_matches_exact_and_suffix_entries(string? setting, string host, bool expected)
    {
        Assert.Equal(expected, SsrfAllowList.Parse(setting).Allows(host));
    }

    [Fact]
    public void Empty_setting_is_the_empty_list()
    {
        Assert.True(SsrfAllowList.Parse("  ").IsEmpty);
        Assert.Same(SsrfAllowList.Empty, SsrfAllowList.Parse(null));
    }

    [Fact]
    public async Task Guard_refuses_loopback_by_default_and_connects_when_the_host_is_listed()
    {
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        });

        try
        {
            using var blocked = new HttpClient(SsrfSafeHttpHandlerFactory.Create("test fetch"));
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => blocked.GetAsync($"http://localhost:{port}/"));
            Assert.Contains("did not resolve to a routable public address", Flatten(ex));
            Assert.Contains("OutboundHttp__AllowedPrivateHosts", Flatten(ex));

            using var allowed = new HttpClient(SsrfSafeHttpHandlerFactory.Create("test fetch", SsrfAllowList.Parse("localhost")));
            var response = await allowed.GetAsync($"http://localhost:{port}/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException) parts.Add(e.Message);
        return string.Join(" | ", parts);
    }

    private static int FreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}
