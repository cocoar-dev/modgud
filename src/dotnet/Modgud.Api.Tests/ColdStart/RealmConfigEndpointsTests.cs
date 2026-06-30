using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Apps;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// The per-realm (data-plane) declarative-config surface: a <c>realm:admin</c> manages THEIR
/// OWN realm from a manifest via <c>/api/admin/realm-config/*</c> — reusing the applier/exporter
/// but scoped to the host-routed realm and gated by realm:admin (not the control plane). It can
/// fully edit the realm's config + entities (incl. prune within the realm), but cannot target
/// another realm, and create/delete-realm stay control-plane-only.
/// </summary>
public class RealmConfigEndpointsTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Apply_manages_the_current_realm_for_a_realm_admin()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        // Slug omitted → the endpoint pins the manifest to the caller's current realm.
        var manifest = new
        {
            Realm = new { },
            Apps = new[]
            {
                new { Slug = "rc-app", DisplayName = "Realm-Config App",
                      Permissions = new[] { new { Resource = "rc", Action = "read" } } },
            },
        };

        var apply = await client.PostAsJsonAsync(
            "/api/admin/realm-config/apply", manifest, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);

        // It landed in the current (system) realm — the data-plane apply targets TenantContext.Current.
        await InTenantAsync(factory, TenantConstants.SystemTenantId, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            Assert.True(await session.Query<App>().AnyAsync(a => !a.IsDeleted && a.Slug == "rc-app", ct),
                "rc-app applied to the current realm via the data-plane endpoint");
        });

        // Export of the current realm works on the same surface (round-trips the manifest).
        var export = await client.GetAsync("/api/admin/realm-config/export", ct);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Contains("rc-app", await export.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Apply_refuses_a_manifest_targeting_a_different_realm()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        // A realm admin may only manage their own realm — a foreign slug is the data-plane boundary.
        var foreign = new
        {
            Realm = new { Slug = "some-other-realm" },
            Apps = new[] { new { Slug = "x", DisplayName = "X", Permissions = new object[0] } },
        };

        var resp = await client.PostAsJsonAsync(
            "/api/admin/realm-config/apply", foreign, factory.JsonOptions, ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Manifest.SlugMismatch", await resp.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Surface_is_gated_for_an_unauthenticated_caller()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var ct = TestContext.Current.CancellationToken;

        var anon = host.Factory.CreateClient();
        var resp = await anon.GetAsync("/api/admin/realm-config/manifest-schema", ct);

        Assert.Contains(resp.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }

    private static async Task InTenantAsync(
        ColdStartWebApplicationFactory factory, string slug, Func<IServiceProvider, Task> body)
    {
        using var _ = TenantContext.Enter(slug);
        using var scope = factory.Services.CreateScope();
        await body(scope.ServiceProvider);
    }
}
