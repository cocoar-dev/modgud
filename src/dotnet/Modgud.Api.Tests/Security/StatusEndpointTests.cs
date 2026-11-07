using System.Net;
using System.Text.Json;
using Modgud.Api.Tests.Infrastructure;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B9-J04 — <c>GET api/status</c> reports host name, content paths, uptime, version and the
/// proxy chain: diagnostics for a realm admin, not for every signed-in user.
/// HTTP-B9-03 — its client address is the connection's (after the forwarded-headers
/// policy), never the first X-Forwarded-For entry a client can write itself.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class StatusEndpointTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task A_signed_in_user_without_realm_admin_is_refused()
    {
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Status", lastname: "Reader", acronym: "sr",
            email: "sr@test.com", password: "TestPass1234", isRealmAdmin: false);
        var user = await CreateAuthenticatedClientAsync("sr", "TestPass1234");

        var response = await user.GetAsync("/api/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_client_address_is_the_one_the_forwarded_headers_policy_resolved()
    {
        // The left entry is whatever the client wrote; the right one is what the last
        // proxy appended. The forwarded-headers policy (ForwardLimit 1, trust-all outside
        // Production) takes the right one; the old helper took the left one.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Add("X-Forwarded-For", "198.51.100.9, 203.0.113.77");

        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("203.0.113.77", json.RootElement.GetProperty("ClientIp").GetString());
    }
}
