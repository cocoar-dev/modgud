using System.Net;
using Modgud.Api.Tests.Infrastructure;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Stage 6 of the cold-start ladder (no silent failures). A permission denial
/// used to be a bare, bodyless <c>403</c> (<c>Results.Forbid()</c>) — the caller
/// got no hint which grant they were missing, unlike OAuth scope rejections that
/// carry an actionable description. This pins that the gate now names the missing
/// permission in the response body.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PermissionDenialBodyTests : IntegrationTestBase
{
    public PermissionDenialBodyTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Gate_403_names_the_missing_permission_in_its_body()
    {
        // A signed-in user with no group/role lacks modgud:user:read, the gate
        // on GET /api/user.
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "No", lastname: "Perm", acronym: "noperm",
            email: "noperm@test.com", password: "TestPass1234");
        var client = await CreateAuthenticatedClientAsync("noperm", "TestPass1234");

        var res = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // The denial must say WHICH permission is missing — not an empty 403.
        Assert.Contains("user:read", body);
    }
}
