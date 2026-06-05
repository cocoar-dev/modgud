using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authorization.Apps;
using Modgud.Domain.OAuth.Scopes;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Stage 0 of the cold-start ladder: a blank-DB boot reaches a usable state.
/// Asserts the cold-boot seed milestones — the system realm with its domains,
/// the seeded apps, OAuth scopes, and the Internal login provider — and that the
/// realm is genuinely adminless (first admin comes from CLI / InitialAdmin, never
/// auto-seeded). This both proves the harness boots and pins the cold-boot
/// contract the rest of the ladder builds on.
/// </summary>
public class ColdStartBootTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Cold_boot_seeds_the_system_realm_as_the_control_plane_with_its_domains()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = Factory.Services.GetRequiredService<IRealmProvisioningService>();

        var realms = await svc.GetAllRealmsAsync(ct);
        var system = Assert.Single(realms, r => r.Slug == "system");

        Assert.True(system.IsControlPlane);
        Assert.True(system.IsActive);
        Assert.Equal("localhost", system.PrimaryDomain);
        Assert.Contains("localhost", system.Domains);
    }

    [Fact]
    public async Task Cold_boot_seeds_apps_oauth_scopes_and_the_internal_provider_into_the_system_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Factory.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession("system");

        // Apps: the system app `modgud` plus the control-plane app (system is the CP).
        var apps = await session.Query<App>().Where(a => !a.IsDeleted).ToListAsync(ct);
        var appSlugs = apps.Select(a => a.Slug).ToList();
        Assert.Contains(AppSlugs.Modgud, appSlugs);
        Assert.Contains(AppSlugs.ControlPlane, appSlugs);

        // The modgud catalog must carry real permission entries (not an empty app).
        var modgud = apps.Single(a => a.Slug == AppSlugs.Modgud);
        Assert.Contains(modgud.Permissions, p => p.Resource == "oauth-client" && p.Action == "write");
        Assert.Contains(modgud.Permissions, p => p.Resource == "user" && p.Action == "read");

        // Standard OIDC scopes.
        var scopes = await session.Query<OAuthScopeState>().Where(s => !s.IsDeleted).ToListAsync(ct);
        var scopeNames = scopes.Select(s => s.Name).ToList();
        foreach (var expected in new[] { "openid", "email", "profile", "roles", "permissions", "offline_access" })
            Assert.Contains(expected, scopeNames);

        // The built-in Internal login provider.
        var providers = await session.Query<LoginProvider>().ToListAsync(ct);
        Assert.Contains(providers, p => p.Type == LoginProviderType.Internal && p.Slug == "internal");
    }

    [Fact]
    public async Task Cold_boot_leaves_an_adminless_realm_no_users_and_no_invites()
    {
        // Use a guaranteed-pristine isolated boot so the assertion is robust
        // regardless of anything other tests did on the shared host.
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var ct = TestContext.Current.CancellationToken;

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession("system");

        Assert.Equal(0, await session.Query<ApplicationUser>().CountAsync(ct));
        Assert.Equal(0, await session.Query<PendingAdminInvite>().CountAsync(ct));
    }
}
