using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Applications;
using Modgud.Domain.Applications;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR-0011 Phase 3 — the NativeGrants settings cascade via the request-time
/// resolver: with the realm's NativeGrants OFF, an Application that overrides
/// Enabled=true resolves to Enabled=true for a request pinned to that App
/// (Host-time), while a request with no App pinned resolves to the realm's OFF.
/// Driven through <see cref="IApplicationSettingsResolver.ResolveForRequestAsync"/>
/// (the exact path the native-grant gates call) rather than the rate-limited HTTP
/// endpoint, whose end-to-end wiring is covered by the JIT-registration flow test.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class NativeGrantsAppOverrideTests : IntegrationTestBase
{
    public NativeGrantsAppOverrideTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task App_Override_Opens_NativeGrants_While_No_App_Inherits_Realm_Off()
    {
        var ct = TestContext.Current.CancellationToken;
        var appId = Guid.NewGuid();
        // Realm NativeGrants left at its default (OFF) — not enabled at the realm.

        using var scope = NewSystemScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ApplicationSettings
        {
            Id = appId,
            CreatedAt = DateTimeOffset.UtcNow,
            NativeGrants = new ApplicationNativeGrantOverrides { Enabled = true },
        });
        await session.SaveChangesAsync(ct);

        var resolver = scope.ServiceProvider.GetRequiredService<IApplicationSettingsResolver>();
        var http = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext!;

        // App pinned (as if on its subdomain) → the override opens the gate despite
        // the realm being OFF.
        http.Items[TenantConstants.HttpContextApplicationIdKey] = appId;
        var withApp = await resolver.ResolveForRequestAsync(http, clientId: null, ct);
        Assert.True(withApp.NativeGrants!.Enabled);

        // No App pinned (plain tenant host, no client) → inherits the realm OFF.
        http.Items.Remove(TenantConstants.HttpContextApplicationIdKey);
        var withoutApp = await resolver.ResolveForRequestAsync(http, clientId: null, ct);
        Assert.True(withoutApp.NativeGrants is null || !withoutApp.NativeGrants.Enabled);
    }

    private IServiceScope NewSystemScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { [TenantConstants.HttpContextTenantIdKey] = "system" } };
        return scope;
    }
}
