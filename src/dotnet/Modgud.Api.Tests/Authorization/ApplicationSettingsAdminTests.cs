using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Applications;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR-0011 — admin API for per-Application settings overrides
/// (<c>GET</c>/<c>PATCH /api/app/{id}/settings</c>): patch→get roundtrip across
/// sections, the Origin write also populating the global host→App routing map,
/// validation, and cross-app subdomain uniqueness.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ApplicationSettingsAdminTests : IntegrationTestBase
{
    public ApplicationSettingsAdminTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Patch_Then_Get_Roundtrips_And_Writes_The_Routing_Map()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await CreateAppAsync("as-admin-app");
        var appShort = new ShortGuid(app.Id).ToString();
        var host = $"as-admin.{await SystemPrimaryDomainAsync()}";

        var patch = new ApplicationSettingsDto
        {
            SelfRegistration = new ApplicationSelfRegistrationDto { Posture = "Off", Enabled = true },
            NativeGrants = new ApplicationNativeGrantsDto { Enabled = true, AccessTokenLifetimeMinutes = 10 },
            Dcr = new ApplicationDcrDto { Enabled = true },
            Cimd = new ApplicationCimdDto { Enabled = true },
            Branding = new ApplicationBrandingDto { ProductName = "Admin Test App" },
            RegistrationFields = new ApplicationRegistrationFieldsDto { Firstname = "Required", Lastname = "Off" },
            Origin = new ApplicationOriginDto { Subdomain = host },
        };
        var patchResp = await Client.PatchAsJsonAsync($"/api/app/{appShort}/settings", patch, JsonOptions, ct);
        Assert.True(patchResp.IsSuccessStatusCode,
            $"PATCH failed ({(int)patchResp.StatusCode}): {await patchResp.Content.ReadAsStringAsync(ct)}");

        var got = await (await Client.GetAsync($"/api/app/{appShort}/settings", ct))
            .Content.ReadFromJsonAsync<ApplicationSettingsDto>(JsonOptions, ct);
        Assert.Equal("Off", got!.SelfRegistration!.Posture);
        Assert.True(got.SelfRegistration.Enabled);
        Assert.True(got.NativeGrants!.Enabled);
        Assert.Equal(10, got.NativeGrants.AccessTokenLifetimeMinutes);
        Assert.True(got.Dcr!.Enabled);
        Assert.True(got.Cimd!.Enabled);
        Assert.Equal("Admin Test App", got.Branding!.ProductName);
        Assert.Equal("Required", got.RegistrationFields!.Firstname);
        Assert.Equal("Off", got.RegistrationFields.Lastname);
        Assert.Null(got.RegistrationFields.Username); // not patched → inherits (null override)
        Assert.Equal(host, got.Origin!.Subdomain);

        // The Origin write populated the GLOBAL host→App routing map.
        var globalStore = Factory.Services.GetRequiredService<IGlobalStore>();
        await using var gs = globalStore.QuerySession();
        var systemRealm = await gs.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == "system", ct);
        Assert.NotNull(systemRealm);
        Assert.True(systemRealm!.ApplicationDomains.TryGetValue(host, out var mappedAppId));
        Assert.Equal(app.Id, mappedAppId);
    }

    [Fact]
    public async Task Patch_Rejects_Invalid_Values()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await CreateAppAsync("as-invalid-app");
        var appShort = new ShortGuid(app.Id).ToString();

        // Bad posture.
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PatchAsJsonAsync($"/api/app/{appShort}/settings",
            new ApplicationSettingsDto { SelfRegistration = new ApplicationSelfRegistrationDto { Posture = "Nope" } },
            JsonOptions, ct)).StatusCode);

        // Subdomain not under the realm's primary domain.
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PatchAsJsonAsync($"/api/app/{appShort}/settings",
            new ApplicationSettingsDto { Origin = new ApplicationOriginDto { Subdomain = "evil.example.com" } },
            JsonOptions, ct)).StatusCode);

        // Out-of-bounds token lifetime.
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PatchAsJsonAsync($"/api/app/{appShort}/settings",
            new ApplicationSettingsDto { NativeGrants = new ApplicationNativeGrantsDto { AccessTokenLifetimeMinutes = 9999 } },
            JsonOptions, ct)).StatusCode);
    }

    [Fact]
    public async Task Subdomain_Is_Unique_Across_Apps()
    {
        var ct = TestContext.Current.CancellationToken;
        var app1 = await CreateAppAsync("as-uniq-1");
        var app2 = await CreateAppAsync("as-uniq-2");
        var host = $"as-uniq.{await SystemPrimaryDomainAsync()}";

        var first = await Client.PatchAsJsonAsync($"/api/app/{new ShortGuid(app1.Id)}/settings",
            new ApplicationSettingsDto { Origin = new ApplicationOriginDto { Subdomain = host } }, JsonOptions, ct);
        Assert.True(first.IsSuccessStatusCode);

        var second = await Client.PatchAsJsonAsync($"/api/app/{new ShortGuid(app2.Id)}/settings",
            new ApplicationSettingsDto { Origin = new ApplicationOriginDto { Subdomain = host } }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    private async Task<string> SystemPrimaryDomainAsync()
    {
        var globalStore = Factory.Services.GetRequiredService<IGlobalStore>();
        await using var gs = globalStore.QuerySession();
        var realm = await gs.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == "system", TestContext.Current.CancellationToken);
        return realm!.PrimaryDomain;
    }
}
