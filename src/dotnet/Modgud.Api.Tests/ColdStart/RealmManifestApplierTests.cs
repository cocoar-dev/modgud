using Modgud.Api.Features.Admin.Provisioning;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.Realms;
using Modgud.Application.Services;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Stage 1b: the RealmManifestApplier imports a fully-configured realm in-process by
/// reusing the canonical admin operations. Proves the writes land in the NEW realm's
/// tenant database (not the control-plane/system tenant the call runs under), via the
/// AsyncLocal TenantContext taking precedence over the ambient HttpContext.
/// </summary>
public class RealmManifestApplierTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Import_provisions_a_fully_configured_realm_in_the_right_tenant()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;

        const string slug = "acme";
        var manifest = new RealmManifest
        {
            Realm = new CreateRealmDto
            {
                Slug = slug,
                DisplayName = "Acme",
                Domains = ["acme.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "admin", Email = "admin@acme.test" },
            },
            Apis = [new CreateOAuthApiDto { Name = "acme-api", DisplayName = "Acme API" }],
            Scopes = [new CreateOAuthScopeDto { Name = "acme.read", DisplayName = "Acme — Read", Resources = ["acme-api"] }],
            Clients =
            [
                new CreateOAuthClientDto
                {
                    ClientId = "acme-web",
                    DisplayName = "Acme Web",
                    ClientType = "confidential",
                    RedirectUris = ["https://acme.test/callback"],
                    Scopes = ["openid", "acme.read"],
                    AllowedGrantTypes = ["authorization_code", "refresh_token"],
                },
            ],
            Users = [new RealmManifestUser { Email = "alice@acme.test", UserName = "alice", Password = "Passw0rd!23" }],
        };

        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        var result = await applier.ImportNewRealmAsync(manifest, ct);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        Assert.Equal(slug, result.Value.Slug);
        Assert.Equal("acme.localhost", result.Value.PrimaryDomain);
        // The confidential client's generated secret is surfaced for the caller.
        Assert.True(result.Value.ClientSecrets.ContainsKey("acme-web"));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.ClientSecrets["acme-web"]));

        // The realm shell exists in the global store.
        var realms = factory.Services.GetRequiredService<IRealmProvisioningService>();
        Assert.NotNull(await realms.GetRealmBySlugAsync(slug, ct));

        // The OAuth config landed in the NEW realm's tenant DB (read via the same
        // inline-consistent admin read methods).
        await InTenantAsync(factory, slug, async oauth =>
        {
            var clients = await oauth.GetClientsAsync(new PaginationRequest { PageSize = 200 }, ct);
            Assert.Contains(clients.Items, c => c.ClientId == "acme-web");
            var apis = await oauth.GetApisAsync(new PaginationRequest { PageSize = 200 }, ct);
            Assert.Contains(apis.Items, a => a.Name == "acme-api");
            var scopes = await oauth.GetScopesAsync(ct);
            Assert.Contains(scopes.Items, s => s.Name == "acme.read");
        });

        // Isolation: the realm's client must NOT exist in the system tenant.
        await InTenantAsync(factory, TenantConstants.SystemTenantId, async oauth =>
        {
            var clients = await oauth.GetClientsAsync(new PaginationRequest { PageSize = 200 }, ct);
            Assert.DoesNotContain(clients.Items, c => c.ClientId == "acme-web");
        });
    }

    [Fact]
    public async Task Import_rejects_a_slug_that_already_exists()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;

        var manifest = new RealmManifest
        {
            Realm = new CreateRealmDto
            {
                Slug = "dup",
                DisplayName = "Dup",
                Domains = ["dup.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "admin", Email = "admin@dup.test" },
            },
        };

        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        var first = await applier.ImportNewRealmAsync(manifest, ct);
        Assert.False(first.IsError, first.IsError ? first.FirstError.Description : string.Empty);

        var second = await applier.ImportNewRealmAsync(manifest, ct);
        Assert.True(second.IsError);
        Assert.Equal("Realm.AlreadyExists", second.FirstError.Code);
    }

    private static async Task InTenantAsync(
        ColdStartWebApplicationFactory factory, string slug, Func<OAuthAdminService, Task> body)
    {
        using var _ = TenantContext.Enter(slug);
        using var scope = factory.Services.CreateScope();
        await body(scope.ServiceProvider.GetRequiredService<OAuthAdminService>());
    }
}
