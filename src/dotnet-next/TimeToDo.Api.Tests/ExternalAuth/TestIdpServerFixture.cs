using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using TimeToDo.TestIdP;
using TimeToDo.TestIdP.Config;

namespace TimeToDo.Api.Tests.ExternalAuth;

/// <summary>
/// Starts an in-process TestIdP on a free port for the lifetime of a single
/// test. Uses real Kestrel (not <see cref="TestServer"/>) so TimeToDo's OIDC
/// handler — which makes real HTTP calls to discovery/token/userinfo — can
/// reach it without HttpClient plumbing.
/// <para>
/// Constructed per-test rather than as an xUnit class fixture so each test
/// gets a fresh TestIdP. Config edits (new users, different claim payloads)
/// stay scoped.
/// </para>
/// </summary>
public sealed class TestIdpServerFixture : IAsyncDisposable
{
    private WebApplication? _app;

    public string BaseAddress { get; private set; } = "";
    public string DiscoveryUri => $"{BaseAddress}/.well-known/openid-configuration";

    public const string DefaultClientId = "timetodo-test";
    public const string DefaultClientSecret = "test-secret";

    public static TestIdpConfig DefaultConfig() => new()
    {
        Clients =
        [
            new TestIdpClient
            {
                ClientId = DefaultClientId,
                ClientSecret = DefaultClientSecret,
                RedirectUris = [],  // tests register the concrete callback URI after creating the IdpConfig
            }
        ],
        Users =
        [
            new TestIdpUser
            {
                UserName = "alice",
                Password = "test123",
                Subject = "user-alice-001",
                Claims = new()
                {
                    ["email"] = "alice@acme.com",
                    ["name"] = "Alice Anderson",
                    ["preferred_username"] = "alice",
                    ["groups"] = new[] { "Admins", "Engineering" },
                    ["roles"] = new[] { "Contributor" },
                },
            },
            new TestIdpUser
            {
                UserName = "bob",
                Password = "test123",
                Subject = "user-bob-001",
                Claims = new()
                {
                    ["email"] = "bob@contoso.com",
                    ["name"] = "Bob Marketing",
                    ["preferred_username"] = "bob",
                    ["groups"] = Array.Empty<string>(),
                    ["roles"] = Array.Empty<string>(),
                },
            },
            new TestIdpUser
            {
                UserName = "mfauser",
                Password = "test123",
                Subject = "user-mfa-001",
                Claims = new()
                {
                    ["email"] = "mfa@acme.com",
                    ["name"] = "MFA User",
                    ["preferred_username"] = "mfauser",
                    ["amr"] = new[] { "mfa", "otp" },
                },
            },
        ],
    };

    public async Task StartAsync(TestIdpConfig? config = null)
    {
        config ??= DefaultConfig();
        var port = FindFreePort();
        BaseAddress = $"http://127.0.0.1:{port}";
        _app = TestIdpHost.Build(config, port: port, args: null);
        await _app.StartAsync();

        // Smoke-check: wait for the discovery doc to be reachable. Kestrel
        // binding is asynchronous; StartAsync completes before the listener
        // is fully accepting connections on some machines.
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var r = await probe.GetAsync(DiscoveryUri);
                if (r.IsSuccessStatusCode) return;
            }
            catch { /* retry */ }
            await Task.Delay(100);
        }
        throw new InvalidOperationException(
            $"TestIdP did not become reachable at {DiscoveryUri} within 30s.");
    }

    /// <summary>
    /// Registers a redirect URI with the default client after-the-fact. Use
    /// this right after creating an IdpConfig in TimeToDo but before kicking
    /// off the OIDC flow.
    /// </summary>
    public Task RegisterRedirectUriAsync(string uri, string clientId = DefaultClientId)
    {
        if (_app is null) throw new InvalidOperationException("Test IdP not started.");
        return TestIdpHost.AddRedirectUriAsync(_app.Services, clientId, uri);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
