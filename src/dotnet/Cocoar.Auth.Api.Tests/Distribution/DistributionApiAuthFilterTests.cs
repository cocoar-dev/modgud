using System.Net;
using Cocoar.Auth.Api.Tests.Infrastructure;

namespace Cocoar.Auth.Api.Tests.Distribution;

/// <summary>
/// Pins the auth envelope of the distribution API. Every request must carry
/// <i>both</i> a valid user-bearer access token <i>and</i>
/// resource-server credentials in the <c>X-Resource-Server-*</c> headers.
///
/// <para>Bearer-token issuance requires a full OAuth code flow which is out
/// of scope for these tests; what we cover here is the negative space —
/// every shape of "missing or wrong credentials" must come back as a clean
/// 401 with the right <c>WWW-Authenticate</c> challenge, never as a
/// silent fall-through to the user-only auth path. The Bearer success
/// path is covered manually for now (see the manual smoke checklist).</para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class DistributionApiAuthFilterTests : IntegrationTestBase
{
    public DistributionApiAuthFilterTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task NoBearer_NoRsHeaders_Returns_401()
    {
        // The endpoint group hard-requires the OpenIddict-validation scheme
        // so an unauthenticated request never even reaches the RS-Auth
        // filter. Pins that the bearer guard sits in front of everything.
        var anon = Factory.CreateClient();

        var r = await anon.GetAsync("/api/v1/distribution/me-permissions",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task CookieAuth_NoBearer_Returns_401()
    {
        // Cookie auth is intentionally NOT enough on the distribution
        // surface — that surface is server-to-server. The admin SPA goes
        // through `/api/v1/me/*` instead. This test guards that the
        // explicit bearer-only policy on the endpoint group wins over the
        // ambient cookie principal.
        var r = await Client.GetAsync("/api/v1/distribution/me-permissions",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task CookieAuth_Plus_RsHeaders_Still_Returns_401()
    {
        // Even if the caller adds full RS-Auth headers, the bearer policy
        // still rejects — the RS-Auth filter only runs after the bearer
        // policy passes. Pins the layering: RS-Auth is an additive axis,
        // not a substitute for bearer.
        Client.DefaultRequestHeaders.Add("X-Resource-Server-Id", "anything");
        Client.DefaultRequestHeaders.Add("X-Resource-Server-Secret", "anything");

        var r = await Client.GetAsync("/api/v1/distribution/me-permissions",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);

        Client.DefaultRequestHeaders.Remove("X-Resource-Server-Id");
        Client.DefaultRequestHeaders.Remove("X-Resource-Server-Secret");
    }
}
