using System.Net;
using Cocoar.Auth.Api.Tests.Infrastructure;
using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Domain.Realms;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Cocoar.Auth.Infrastructure.Realms;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Api.Tests.Security;

/// <summary>
/// Integration tests for C14 — Control-Plane / Data-Plane separation. Drives
/// the real HTTP pipeline (RealmMiddleware → ControlPlaneGateMiddleware →
/// RequireControlPlaneFilter) against a tenant realm seeded directly into
/// the global store. The pure-unit pin lives next to the middleware
/// implementation; this test verifies the wiring end-to-end.
///
/// <para>Why bother with both: the unit tests pin <em>behaviour given a
/// known TenantInfo</em>, this test pins <em>the pipeline actually
/// resolves a tenant host to that TenantInfo and gates accordingly</em>.
/// A regression in middleware ordering, cache-invalidation, or the host
/// → realm resolution rule would slip past the unit tests.</para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ControlPlaneSeparationTests : IntegrationTestBase
{
    private const string TenantHost = "acme.localhost";

    public ControlPlaneSeparationTests(SharedPostgresFixture fixture) : base(fixture) { }

    /// <summary>
    /// Insert a tenant realm directly into IGlobalStore — we don't need
    /// to provision the actual tenant database for this test, the
    /// routing-gate fires before any tenant work is done. The system
    /// realm survives ResetMartenDataAsync (boot-time seed is one-shot
    /// per host, the global-store schema is stable across resets), so
    /// we only add the tenant. Idempotent for re-runs in the same class.
    /// Invalidates the realm cache so RealmMiddleware picks up the new
    /// entry on the next request.
    /// </summary>
    private async Task SeedTenantRealmAsync(string slug, string host)
    {
        var globalStore = Factory.Services.GetRequiredService<IGlobalStore>();
        await using var session = globalStore.LightweightSession();

        var existing = await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == slug, TestContext.Current.CancellationToken);

        if (existing is null)
        {
            var tenant = new Realm
            {
                Id = Guid.NewGuid(),
                Slug = slug,
                DisplayName = "Acme",
                Domains = [host],
                IsControlPlane = false,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            session.Store(tenant);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Factory.Services.GetRequiredService<IRealmCache>().Invalidate();
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string host)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Host = host;
        return req;
    }

    [Fact]
    public async Task GET_admin_realms_from_tenant_host_returns_404()
    {
        // The whole point of C14: a tenant realm hitting the cross-realm
        // admin surface gets 404 from the routing-gate, before auth runs.
        // 404 (not 401/403) so the existence of the endpoint is hidden.
        await SeedTenantRealmAsync("acme", TenantHost);

        using var resp = await Client.SendAsync(
            Request(HttpMethod.Get, "/api/admin/realms", TenantHost),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GET_admin_realms_from_control_plane_host_returns_200()
    {
        // Positive control: same path, Control-Plane host → 200. Confirms
        // the gate isn't simply broken-by-default.
        await SeedTenantRealmAsync("acme", TenantHost);

        // The default Client uses Host=localhost, which resolves to the
        // system realm (IsControlPlane=true) via the cache.
        using var resp = await Client.GetAsync(
            "/api/admin/realms",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task POST_setup_from_tenant_host_returns_404()
    {
        // The first-run setup wizard is a deployment-global one-shot —
        // exposing it on a tenant host would let a tenant create the first
        // global admin if the deployment hadn't been bootstrapped. The
        // gate covers /api/setup/* the same way it covers /api/admin/realms.
        await SeedTenantRealmAsync("acme", TenantHost);

        using var resp = await Client.SendAsync(
            Request(HttpMethod.Post, "/api/setup/create-admin", TenantHost),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task UpdateRealm_promoting_second_realm_to_ControlPlane_is_blocked()
    {
        // Exactly-one-CP invariant: with system already flagged
        // IsControlPlane=true, promoting a tenant realm via PATCH must
        // fail. UI-smoke-test surfaced this as a real bug — the original
        // C14 only guarded the *demote-last-CP* direction; promoting a
        // second CP slipped through and silently broke the deployment-
        // global "exactly one" assumption (boot-validation, hostname
        // routing, /api/app-info IsControlPlane signal all rely on it).
        await SeedTenantRealmAsync("acme", TenantHost);

        var svc = Factory.Services.GetRequiredService<IRealmProvisioningService>();
        var result = await svc.UpdateRealmAsync("acme",
            new UpdateRealmDto { IsControlPlane = true },
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal("Realm.ControlPlaneAlreadyExists", result.FirstError.Code);
    }

    [Fact]
    public async Task CreateRealm_with_IsControlPlane_when_one_exists_is_blocked()
    {
        // Same invariant via CreateRealmDto: declaring a brand-new realm
        // as Control-Plane while an active CP already exists must 400.
        // Goes via the service (not HTTP) so we don't pay the ~30s
        // tenant-DB-provisioning cost — the validation runs before any
        // raw-SQL CREATE DATABASE.
        var svc = Factory.Services.GetRequiredService<IRealmProvisioningService>();
        var result = await svc.CreateRealmAsync(
            new CreateRealmDto
            {
                Slug = "fresh",
                DisplayName = "Fresh CP",
                Domains = ["fresh.localhost"],
                IsControlPlane = true,
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal("Realm.ControlPlaneAlreadyExists", result.FirstError.Code);
    }

    [Fact]
    public async Task UpdateRealm_demoting_last_ControlPlane_is_blocked()
    {
        // Mirror of the promote-second guard: removing the flag from the
        // last active CP-realm must also fail. Pin both sides so a
        // future refactor can't quietly drop one.
        var svc = Factory.Services.GetRequiredService<IRealmProvisioningService>();
        var result = await svc.UpdateRealmAsync("system",
            new UpdateRealmDto { IsControlPlane = false },
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal("Realm.CannotRemoveControlPlaneFlag", result.FirstError.Code);
    }

    [Fact]
    public async Task GET_app_info_on_control_plane_host_returns_IsControlPlane_true()
    {
        // The SPA bootstraps off /api/app-info anonymously and uses
        // IsControlPlane to decide whether to render Realm-admin links.
        // Pin the Control-Plane direction here; the false-case for tenant
        // hosts is covered by the unit tests on the gate + filter (a
        // tenant-host probe through the integration pipeline trips a
        // tenant-DB lookup elsewhere in the stack since the test setup
        // doesn't provision a real Marten DB for the seeded tenant).
        using var resp = await Client.GetAsync(
            "/api/app-info",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(
            JsonOptions, TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("IsControlPlane").GetBoolean());
    }
}
