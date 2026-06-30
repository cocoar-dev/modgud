using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Apps;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Modgud.Provisioning.TestKit;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Stage 1d: drives the standalone <c>Modgud.Provisioning.TestKit</c> against the live
/// control-plane endpoints. Doubles as the kit's contract guard — the kit's own manifest
/// POCOs are serialised and posted to the real import/apply/delete endpoints, so any drift
/// between the kit's shape and the server's manifest contract fails here.
/// </summary>
public class ProvisioningTestKitTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task TestKit_imports_applies_and_hard_deletes_a_realm()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var httpClient = await factory.CreateRealmAdminAndLoginAsync();
        var svc = factory.Services.GetRequiredService<IRealmProvisioningService>();

        var kit = new ModgudProvisioningClient(httpClient);
        const string slug = "kittest";

        var realm = await kit.ImportRealmAsync(BuildManifest(slug, "Kit App"), ct);

        // The handle surfaces everything an app-under-test needs.
        Assert.Equal(slug, realm.Slug);
        Assert.Equal("kittest.localhost", realm.PrimaryDomain);
        Assert.Equal("https://kittest.localhost", realm.Authority);
        Assert.False(string.IsNullOrWhiteSpace(realm.SecretFor("kit-web")));
        Assert.NotNull(await svc.GetRealmBySlugAsync(slug, ct));

        // In-place apply through the kit.
        await realm.ApplyAsync(BuildManifest(slug, "Kit App v2"), ct);
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var app = await session.Query<App>().SingleAsync(a => !a.IsDeleted && a.Slug == "kit-app", ct);
            Assert.Equal("Kit App v2", app.DisplayName);
        });

        // Explicit teardown asserts the hard-delete really dropped the realm.
        await realm.DeleteAsync(ct);
        Assert.Null(await svc.GetRealmBySlugAsync(slug, ct));
    }

    [Fact]
    public async Task TestKit_surfaces_the_server_error_code_on_duplicate_import()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var kit = new ModgudProvisioningClient(await factory.CreateRealmAdminAndLoginAsync());

        const string slug = "kitdup";
        await using var first = await kit.ImportRealmAsync(BuildManifest(slug, "Dup"), ct);

        var ex = await Assert.ThrowsAsync<ModgudProvisioningException>(
            () => kit.ImportRealmAsync(BuildManifest(slug, "Dup"), ct));
        Assert.Equal("Realm.AlreadyExists", ex.Code);
    }

    private static RealmManifest BuildManifest(string slug, string appDisplayName) => new()
    {
        Realm = new RealmSpec
        {
            Slug = slug,
            DisplayName = slug,
            Domains = [$"{slug}.localhost"],
            InitialAdmin = new InitialAdmin { UserName = "admin", Email = $"admin@{slug}.test" },
        },
        Apps =
        [
            new RealmManifestApp
            {
                Slug = "kit-app",
                DisplayName = appDisplayName,
                Permissions = [new RealmManifestPermission("kit", "read")],
            },
        ],
        Clients =
        [
            new RealmManifestClient
            {
                ClientId = "kit-web",
                DisplayName = "Kit Web",
                ClientType = "confidential",
                RedirectUris = [$"https://{slug}.localhost/callback"],
                Scopes = ["openid"],
                AllowedGrantTypes = ["authorization_code", "refresh_token"],
                Apps = ["kit-app"],
            },
        ],
        Users =
        [
            new RealmManifestUser { Key = "admin", Email = $"admin@{slug}.test", UserName = "admin", Password = "Passw0rd!23" },
        ],
    };

    private static async Task InTenantAsync(
        ColdStartWebApplicationFactory factory, string slug, Func<IServiceProvider, Task> body)
    {
        using var _ = TenantContext.Enter(slug);
        using var scope = factory.Services.CreateScope();
        await body(scope.ServiceProvider);
    }
}
