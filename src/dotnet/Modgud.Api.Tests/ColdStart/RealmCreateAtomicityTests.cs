using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Stage 2: creating the very first realm over HTTP must be atomic with its
/// bootstrap-invite. The realm is provisioned (tenant DB created + seeded) before
/// the invite is issued; if invite issuance throws, the operator must NOT be left
/// with an adminless, orphaned realm and a retry that 409s. The fix rolls the
/// realm back and returns a clear, recoverable error.
/// </summary>
public class RealmCreateAtomicityTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Realm_create_rolls_back_when_bootstrap_invite_fails_and_a_retry_is_clean()
    {
        // Isolated cold boot: the system realm + admin we create live in a
        // throwaway DB, so this mutating test pollutes nothing.
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;

        var client = await factory.CreateRealmAdminAndLoginAsync();
        var fault = factory.Services.GetRequiredService<ColdStartFaultInjection>();
        var svc = factory.Services.GetRequiredService<IRealmProvisioningService>();

        const string slug = "atomicity-test";
        var body = new
        {
            slug,
            displayName = "Atomicity Test",
            domains = new[] { "atomicity.localhost" },
            primaryDomain = "atomicity.localhost",
            initialAdmin = new { userName = "admin", email = "admin@atomicity.local" },
        };

        // 1. Force the bootstrap-invite to blow up after the realm is provisioned.
        fault.ThrowOnInvite = true;
        var failure = await client.PostAsJsonAsync("/api/admin/realms", body, ct);

        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        var problem = await failure.Content.ReadAsStringAsync(ct);
        Assert.Contains("Realm.Provisioning.InviteFailed", problem);

        // 2. The partially-provisioned realm must be rolled back, not orphaned.
        Assert.Null(await svc.GetRealmBySlugAsync(slug, ct));

        // 3. With the fault cleared, a retry of the exact same request is clean.
        fault.ThrowOnInvite = false;
        var retry = await client.PostAsJsonAsync("/api/admin/realms", body, ct);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.NotNull(await svc.GetRealmBySlugAsync(slug, ct));
    }
}
