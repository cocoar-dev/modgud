using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.Applications;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR-0011 Phase 3 — proves the settings cascade end-to-end on the NativeGrants
/// Host-time gate: with the realm's NativeGrants OFF, an Application that overrides
/// Enabled=true opens the native-OTP gate on its own subdomain, while a sibling
/// Application with no override inherits the realm's OFF on its subdomain. The gate
/// is observed via whether a code email is actually sent (the endpoint response is
/// uniform for anti-enumeration).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class NativeGrantsAppOverrideTests : IntegrationTestBase
{
    private const string Email = "test@test.com"; // the seeded DefaultUser

    public NativeGrantsAppOverrideTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task App_Override_Opens_NativeGrants_Gate_While_Sibling_Inherits_Realm_Off()
    {
        var ct = TestContext.Current.CancellationToken;
        // Realm NativeGrants left at its default (OFF) — not enabled anywhere.

        var overrideApp = await CreateAppAsync("ng-override-app");
        var plainApp = await CreateAppAsync("ng-plain-app");
        await StoreApplicationSettingsAsync(new ApplicationSettings
        {
            Id = overrideApp.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            NativeGrants = new ApplicationNativeGrantOverrides { Enabled = true },
        });
        await MapApplicationDomainsAsync(
            ("ng-override.localhost", overrideApp.Id),
            ("ng-plain.localhost", plainApp.Id));

        var email = Factory.Services.GetRequiredService<InMemoryEmailService>();

        // Override App subdomain → gate open → a code is sent to the known user.
        email.Clear();
        await RequestNativeOtpAsync("ng-override.localhost");
        Assert.NotNull(email.GetLastEmailTo(Email));

        // Sibling App subdomain (no override) → inherits realm OFF → no code sent.
        email.Clear();
        await RequestNativeOtpAsync("ng-plain.localhost");
        Assert.Null(email.GetLastEmailTo(Email));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task RequestNativeOtpAsync(string host)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/account/native/otp/request")
        {
            Content = JsonContent.Create(new { Email }),
        };
        req.Headers.Host = host;
        var resp = await Client.SendAsync(req, TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode(); // uniform 200 regardless of the gate (anti-enumeration)
    }

    private async Task<App> CreateAppAsync(string slug)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id, Slug: slug, DisplayName: slug, Description: null, Permissions: [], IsSystem: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (await session.LoadAsync<App>(id, TestContext.Current.CancellationToken))!;
    }

    private async Task StoreApplicationSettingsAsync(ApplicationSettings settings)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(settings);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task MapApplicationDomainsAsync(params (string Host, Guid AppId)[] entries)
    {
        var ct = TestContext.Current.CancellationToken;
        var globalStore = Factory.Services.GetRequiredService<IGlobalStore>();
        await using (var session = globalStore.LightweightSession())
        {
            var systemRealm = await session.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == "system", ct);
            Assert.NotNull(systemRealm);
            foreach (var (host, appId) in entries)
                systemRealm!.ApplicationDomains[host] = appId;
            session.Store(systemRealm!);
            await session.SaveChangesAsync(ct);
        }

        Factory.Services.GetRequiredService<IRealmCache>().Invalidate();
    }
}
