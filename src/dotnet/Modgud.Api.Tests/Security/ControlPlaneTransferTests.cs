using System.Net;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Realms;
using Modgud.Authorization.Apps;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Integration tests for the transferable control plane (stored
/// <see cref="Realm.IsControlPlane"/> flag). Pins: the flag moves to the
/// target and clears every other holder, the guards (missing / inactive
/// target), the boot-time durability guard (a reboot must not steal the flag
/// back), and that the routing-gate follows the flag.
///
/// <para>State hygiene: the control plane starts on the <c>system</c> realm
/// (boot seed) and many other tests in this collection assume that. Every test
/// here that flips the flag restores it to <c>system</c> in a <c>finally</c> so
/// the shared host is left as it was found.</para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ControlPlaneTransferTests : IntegrationTestBase
{
    // A distinct slug from the gate tests' "acme" so the two classes don't
    // collide in the shared global store. This realm gets a real tenant DB.
    private const string TargetSlug = "cptarget";
    private const string TargetHost = "cptarget.localhost";

    public ControlPlaneTransferTests(SharedPostgresFixture fixture) : base(fixture) { }

    private IRealmProvisioningService Svc =>
        Factory.Services.GetRequiredService<IRealmProvisioningService>();

    private void InvalidateRealmCache() =>
        Factory.Services.GetRequiredService<IRealmCache>().Invalidate();

    /// <summary>
    /// Idempotently provision a real tenant realm (own DB + registry record),
    /// so a control-plane transfer onto it can re-seed the control-plane app.
    /// </summary>
    private async Task EnsureTargetRealmAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var existing = await Svc.GetRealmBySlugAsync(TargetSlug, ct);
        if (existing is null)
        {
            var result = await Svc.CreateRealmAsync(new CreateRealmDto
            {
                Slug = TargetSlug,
                DisplayName = "CP Target",
                Domains = [TargetHost],
                InitialAdmin = new InitialAdminDto { UserName = "admin", Email = "admin@cptarget.test" },
            }, ct);
            Assert.False(result.IsError,
                result.IsError ? result.FirstError.Description : string.Empty);
        }
        InvalidateRealmCache();
    }

    private async Task RestoreControlPlaneToSystemAsync()
    {
        await Svc.TransferControlPlaneAsync(TenantConstants.SystemTenantId,
            TestContext.Current.CancellationToken);
        InvalidateRealmCache();
    }

    private async Task<Realm?> GetRealmAsync(string slug) =>
        await Svc.GetRealmBySlugAsync(slug, TestContext.Current.CancellationToken);

    private HttpRequestMessage Request(HttpMethod method, string path, string host)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Host = host;
        return req;
    }

    [Fact]
    public async Task TransferControlPlane_moves_flag_to_target_and_clears_system()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureTargetRealmAsync();

        try
        {
            var result = await Svc.TransferControlPlaneAsync(TargetSlug, ct);
            Assert.False(result.IsError,
                result.IsError ? result.FirstError.Description : string.Empty);

            var cp = await Svc.GetControlPlaneRealmAsync(ct);
            Assert.NotNull(cp);
            Assert.Equal(TargetSlug, cp!.Slug);

            Assert.True((await GetRealmAsync(TargetSlug))!.IsControlPlane);
            Assert.False((await GetRealmAsync(TenantConstants.SystemTenantId))!.IsControlPlane);

            // The transfer re-seeds the control-plane app into the target's DB
            // so scoped control-plane:realm:* roles can be granted there.
            await using var targetSession = GetTenantedSession(TargetSlug);
            var controlPlaneApp = await targetSession.Query<App>()
                .FirstOrDefaultAsync(a => a.Slug == AppSlugs.ControlPlane && !a.IsDeleted, ct);
            Assert.NotNull(controlPlaneApp);
        }
        finally
        {
            await RestoreControlPlaneToSystemAsync();
        }
    }

    [Fact]
    public async Task TransferControlPlane_to_missing_realm_returns_NotFound()
    {
        // No flag flip happens on failure — no restore needed.
        var result = await Svc.TransferControlPlaneAsync("does-not-exist",
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal("Realm.NotFound", result.FirstError.Code);
        Assert.Equal(TenantConstants.SystemTenantId,
            (await Svc.GetControlPlaneRealmAsync(TestContext.Current.CancellationToken))!.Slug);
    }

    [Fact]
    public async Task TransferControlPlane_to_inactive_realm_returns_TargetInactive()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed an inactive realm doc directly (no tenant DB needed — the guard
        // fires before any re-seed). Idempotent across re-runs.
        var globalStore = Factory.Services.GetRequiredService<IGlobalStore>();
        await using (var session = globalStore.LightweightSession())
        {
            var existing = await session.Query<Realm>()
                .FirstOrDefaultAsync(r => r.Slug == "inactive-target", ct);
            if (existing is null)
            {
                session.Store(new Realm
                {
                    Id = Guid.NewGuid(),
                    Slug = "inactive-target",
                    DisplayName = "Inactive",
                    Domains = ["inactive-target.localhost"],
                    IsControlPlane = false,
                    IsActive = false,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await session.SaveChangesAsync(ct);
            }
        }

        var result = await Svc.TransferControlPlaneAsync("inactive-target", ct);

        Assert.True(result.IsError);
        Assert.Equal("Realm.TargetInactive", result.FirstError.Code);
        Assert.Equal(TenantConstants.SystemTenantId,
            (await Svc.GetControlPlaneRealmAsync(ct))!.Slug);
    }

    [Fact]
    public async Task EnsureSystemRealm_does_not_steal_flag_back_after_transfer()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureTargetRealmAsync();

        try
        {
            await Svc.TransferControlPlaneAsync(TargetSlug, ct);

            // Simulate a reboot's bootstrap step. The "only stamp when no holder"
            // guard must leave the transferred flag where it is.
            await Svc.EnsureSystemRealmExistsAsync(ct);

            Assert.False((await GetRealmAsync(TenantConstants.SystemTenantId))!.IsControlPlane);
            Assert.True((await GetRealmAsync(TargetSlug))!.IsControlPlane);
            Assert.Equal(TargetSlug, (await Svc.GetControlPlaneRealmAsync(ct))!.Slug);
        }
        finally
        {
            await RestoreControlPlaneToSystemAsync();
        }
    }

    [Fact]
    public async Task ControlPlaneGate_follows_the_flag_after_transfer()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureTargetRealmAsync();

        try
        {
            await Svc.TransferControlPlaneAsync(TargetSlug, ct);
            InvalidateRealmCache();

            using var anon = Factory.CreateClient();

            // Old control-plane host (system, now demoted) → gate 404s the
            // realm-management surface (the gate runs before auth).
            using var oldHostResp = await anon.SendAsync(
                Request(HttpMethod.Get, "/api/admin/realms", "localhost"), ct);
            Assert.Equal(HttpStatusCode.NotFound, oldHostResp.StatusCode);

            // New control-plane host → gate PASSES (no longer 404); the request
            // then falls through to auth, which rejects the anonymous client.
            using var newHostResp = await anon.SendAsync(
                Request(HttpMethod.Get, "/api/admin/realms", TargetHost), ct);
            Assert.NotEqual(HttpStatusCode.NotFound, newHostResp.StatusCode);
        }
        finally
        {
            await RestoreControlPlaneToSystemAsync();
        }
    }
}
