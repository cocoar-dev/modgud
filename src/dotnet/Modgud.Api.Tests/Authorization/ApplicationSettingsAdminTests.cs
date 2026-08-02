using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Admin.Apps;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Applications;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.Applications;
using Modgud.Domain.Assets;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR-0011 — an App is ONE resource: the per-App settings override is carried on the
/// unified App endpoint (<c>POST</c>/<c>PUT</c>/<c>GET /api/app</c>), there is no separate
/// <c>/settings</c> endpoint. Covers the atomic create (App + settings in one transaction,
/// and an invalid settings section rejecting the whole create), the update roundtrip across
/// sections, the Origin write also populating the global host→App routing map, validation,
/// and cross-app subdomain uniqueness.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ApplicationSettingsAdminTests : IntegrationTestBase
{
    public ApplicationSettingsAdminTests(SharedPostgresFixture fixture) : base(fixture) { }

    private sealed record AppRead(string Id, string Slug, ApplicationSettingsDto? Settings);

    [Fact]
    public async Task Create_With_Settings_PersistsBoth_Atomically()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = new ApplicationSettingsDto
        {
            NativeGrants = new ApplicationNativeGrantsDto { Enabled = true, AccessTokenLifetimeMinutes = 10 },
            Branding = new ApplicationBrandingDto { ProductName = "Created Together" },
        };

        var resp = await Client.PostAsJsonAsync("/api/app",
            new CreateAppDto("as-create-app", "As Create App", null, [], settings), JsonOptions, ct);
        Assert.True(resp.IsSuccessStatusCode, $"POST failed ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}");

        var created = await resp.Content.ReadFromJsonAsync<AppRead>(JsonOptions, ct);
        Assert.True(created!.Settings!.NativeGrants!.Enabled);
        Assert.Equal("Created Together", created.Settings.Branding!.ProductName);

        // Re-read confirms the override was committed alongside the App.
        var got = await GetAppAsync(created.Id, ct);
        Assert.Equal(10, got.Settings!.NativeGrants!.AccessTokenLifetimeMinutes);
    }

    [Fact]
    public async Task Create_With_Invalid_Settings_RejectsWholeCreate()
    {
        var ct = TestContext.Current.CancellationToken;

        // The App itself is valid, but the settings section is not → the whole create is
        // rejected and NO App is left behind (one atomic transaction).
        var resp = await Client.PostAsJsonAsync("/api/app",
            new CreateAppDto("as-atomic-app", "As Atomic App", null, [],
                new ApplicationSettingsDto { SelfRegistration = new ApplicationSelfRegistrationDto { Posture = "Nope" } }),
            JsonOptions, ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(await AppExistsAsync("as-atomic-app"), "the App must not be persisted when its settings are invalid");
    }

    [Fact]
    public async Task Update_Settings_Roundtrips_And_Writes_The_Routing_Map()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await SeedAppAsync("as-admin-app");
        var appShort = new ShortGuid(app.Id).ToString();
        var host = $"as-admin.{await SystemPrimaryDomainAsync()}";

        var settings = new ApplicationSettingsDto
        {
            SelfRegistration = new ApplicationSelfRegistrationDto { Posture = "Off", Enabled = true },
            NativeGrants = new ApplicationNativeGrantsDto { Enabled = true, AccessTokenLifetimeMinutes = 10 },
            Dcr = new ApplicationDcrDto { Enabled = true },
            Cimd = new ApplicationCimdDto { Enabled = true },
            Branding = new ApplicationBrandingDto { ProductName = "Admin Test App" },
            RegistrationFields = new ApplicationRegistrationFieldsDto { Firstname = "Required", Lastname = "Off" },
            Origin = new ApplicationOriginDto { Subdomain = host },
        };
        var putResp = await Client.PutAsJsonAsync($"/api/app/{appShort}",
            new UpdateAppDto("As Admin App", null, [], settings), JsonOptions, ct);
        Assert.True(putResp.IsSuccessStatusCode,
            $"PUT failed ({(int)putResp.StatusCode}): {await putResp.Content.ReadAsStringAsync(ct)}");

        var got = (await GetAppAsync(appShort, ct)).Settings;
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
    public async Task Update_Rejects_Invalid_Values()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await SeedAppAsync("as-invalid-app");
        var appShort = new ShortGuid(app.Id).ToString();

        // Bad posture.
        Assert.Equal(HttpStatusCode.BadRequest, (await PutSettingsAsync(appShort,
            new ApplicationSettingsDto { SelfRegistration = new ApplicationSelfRegistrationDto { Posture = "Nope" } }, ct)).StatusCode);

        // Subdomain not under the realm's primary domain.
        Assert.Equal(HttpStatusCode.BadRequest, (await PutSettingsAsync(appShort,
            new ApplicationSettingsDto { Origin = new ApplicationOriginDto { Subdomain = "evil.example.com" } }, ct)).StatusCode);

        // Out-of-bounds token lifetime.
        Assert.Equal(HttpStatusCode.BadRequest, (await PutSettingsAsync(appShort,
            new ApplicationSettingsDto { NativeGrants = new ApplicationNativeGrantsDto { AccessTokenLifetimeMinutes = 9999 } }, ct)).StatusCode);
    }

    [Fact]
    public async Task Subdomain_Is_Unique_Across_Apps()
    {
        var ct = TestContext.Current.CancellationToken;
        var app1 = await SeedAppAsync("as-uniq-1");
        var app2 = await SeedAppAsync("as-uniq-2");
        var host = $"as-uniq.{await SystemPrimaryDomainAsync()}";

        var first = await PutSettingsAsync(new ShortGuid(app1.Id).ToString(),
            new ApplicationSettingsDto { Origin = new ApplicationOriginDto { Subdomain = host } }, ct);
        Assert.True(first.IsSuccessStatusCode);

        var second = await PutSettingsAsync(new ShortGuid(app2.Id).ToString(),
            new ApplicationSettingsDto { Origin = new ApplicationOriginDto { Subdomain = host } }, ct);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Clearing_Origin_Removes_The_Global_Route()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await SeedAppAsync("as-origin-clear");
        var appShort = new ShortGuid(app.Id).ToString();
        var host = $"as-origin-clear.{await SystemPrimaryDomainAsync()}";

        (await PutSettingsAsync(appShort,
            new ApplicationSettingsDto { Origin = new ApplicationOriginDto { Subdomain = host } }, ct))
            .EnsureSuccessStatusCode();
        (await PutSettingsAsync(appShort,
            new ApplicationSettingsDto { Origin = new ApplicationOriginDto { Subdomain = null } }, ct))
            .EnsureSuccessStatusCode();

        Assert.Null((await GetAppAsync(appShort, ct)).Settings!.Origin);
        var globalStore = Factory.Services.GetRequiredService<IGlobalStore>();
        await using var gs = globalStore.QuerySession();
        var realm = await gs.Query<Realm>().FirstAsync(r => r.Slug == "system", ct);
        Assert.DoesNotContain(host, realm.ApplicationDomains.Keys);
    }

    [Fact]
    public async Task Delete_Removes_Settings_And_Global_Route()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await SeedAppAsync("as-delete-cleanup");
        var appShort = new ShortGuid(app.Id).ToString();
        var host = $"as-delete-cleanup.{await SystemPrimaryDomainAsync()}";

        (await PutSettingsAsync(appShort, new ApplicationSettingsDto
        {
            Origin = new ApplicationOriginDto { Subdomain = host },
            Branding = new ApplicationBrandingDto { ProductName = "Temporary" },
        }, ct)).EnsureSuccessStatusCode();

        var response = await Client.DeleteAsync($"/api/app/{appShort}", ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using (var tenant = GetTenantedSession())
            Assert.Null(await tenant.LoadAsync<ApplicationSettings>(app.Id, ct));

        var globalStore = Factory.Services.GetRequiredService<IGlobalStore>();
        await using var gs = globalStore.QuerySession();
        var realm = await gs.Query<Realm>().FirstAsync(r => r.Slug == "system", ct);
        Assert.DoesNotContain(host, realm.ApplicationDomains.Keys);
    }

    [Fact]
    public async Task Branding_Rejects_An_Unknown_Asset()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await SeedAppAsync("as-missing-asset");
        var response = await PutSettingsAsync(new ShortGuid(app.Id).ToString(),
            new ApplicationSettingsDto
            {
                Branding = new ApplicationBrandingDto
                {
                    LogoAssetId = ShortGuid.Encode(Guid.NewGuid()),
                },
            }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Application.AssetNotFound", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Login_Experience_Roundtrips_Provider_Order_And_Explicit_Empty_List()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await SeedAppAsync("as-login-experience");
        var first = new LoginProvider
        {
            Id = Guid.NewGuid(), Type = LoginProviderType.Oidc, Slug = "first-idp",
            DisplayName = "First", Enabled = true, CreatedAt = DateTimeOffset.UtcNow,
        };
        var second = new LoginProvider
        {
            Id = Guid.NewGuid(), Type = LoginProviderType.Saml, Slug = "second-idp",
            DisplayName = "Second", Enabled = true, CreatedAt = DateTimeOffset.UtcNow,
        };
        await using (var session = GetTenantedDocumentSession())
        {
            session.Store(first, second);
            await session.SaveChangesAsync(ct);
        }

        var ids = new[] { ShortGuid.Encode(second.Id), ShortGuid.Encode(first.Id) };
        (await PutSettingsAsync(ShortGuid.Encode(app.Id), new ApplicationSettingsDto
        {
            LoginExperience = new ApplicationLoginExperienceDto
            {
                InternalLoginEnabled = false,
                MagicLinkEnabled = false,
                LoginProviderIds = ids,
            },
        }, ct)).EnsureSuccessStatusCode();

        var got = (await GetAppAsync(ShortGuid.Encode(app.Id), ct)).Settings!.LoginExperience!;
        Assert.False(got.InternalLoginEnabled);
        Assert.False(got.MagicLinkEnabled);
        Assert.Equal(ids, got.LoginProviderIds);

        (await PutSettingsAsync(ShortGuid.Encode(app.Id), new ApplicationSettingsDto
        {
            LoginExperience = new ApplicationLoginExperienceDto { LoginProviderIds = [] },
        }, ct)).EnsureSuccessStatusCode();
        Assert.Empty((await GetAppAsync(ShortGuid.Encode(app.Id), ct)).Settings!
            .LoginExperience!.LoginProviderIds!);
    }

    [Fact]
    public async Task Asset_Delete_Is_Blocked_By_Application_Branding_Reference()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await SeedAppAsync("as-asset-reference");
        var asset = new Asset
        {
            Id = Guid.NewGuid(), FileName = "logo.png", ContentType = "image/png",
            Data = [1, 2, 3], SizeBytes = 3, Sha256 = "test", UploadedAt = DateTimeOffset.UtcNow,
        };
        await using (var session = GetTenantedDocumentSession())
        {
            session.Store(asset);
            await session.SaveChangesAsync(ct);
        }

        (await PutSettingsAsync(ShortGuid.Encode(app.Id), new ApplicationSettingsDto
        {
            Branding = new ApplicationBrandingDto { LogoAssetId = ShortGuid.Encode(asset.Id) },
        }, ct)).EnsureSuccessStatusCode();

        var response = await Client.DeleteAsync($"/api/admin/assets/{ShortGuid.Encode(asset.Id)}", ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains($"application:{ShortGuid.Encode(app.Id)}.branding.logo",
            await response.Content.ReadAsStringAsync(ct));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> PutSettingsAsync(string appShort, ApplicationSettingsDto settings, CancellationToken ct) =>
        Client.PutAsJsonAsync($"/api/app/{appShort}", new UpdateAppDto(appShort, null, [], settings), JsonOptions, ct);

    private async Task<AppRead> GetAppAsync(string appShort, CancellationToken ct) =>
        (await (await Client.GetAsync($"/api/app/{appShort}", ct)).Content.ReadFromJsonAsync<AppRead>(JsonOptions, ct))!;

    private async Task<App> SeedAppAsync(string slug)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id, Slug: slug, DisplayName: slug, Description: null, Permissions: [], IsSystem: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (await session.LoadAsync<App>(id, TestContext.Current.CancellationToken))!;
    }

    private async Task<bool> AppExistsAsync(string slug)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        return await session.Query<App>().Where(a => a.Slug == slug && !a.IsDeleted)
            .AnyAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> SystemPrimaryDomainAsync()
    {
        var globalStore = Factory.Services.GetRequiredService<IGlobalStore>();
        await using var gs = globalStore.QuerySession();
        var realm = await gs.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == "system", TestContext.Current.CancellationToken);
        return realm!.PrimaryDomain;
    }
}
