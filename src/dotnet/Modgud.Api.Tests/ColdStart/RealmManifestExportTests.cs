using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Admin.Provisioning;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Applications;
using Modgud.Application.DTOs.Realms;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Applications;
using Modgud.Authentication.RealmSettings;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Stage 1c (Export): the structure-only export round-trips with apply. Imports a realm
/// with a passwordless user + a confidential client, exports it, asserts no secrets /
/// passwords / seeded entities leak, re-applies the unedited export idempotently, then edits
/// the export to set the user's password and re-applies — proving the
/// export → edit → "set a password" → apply flow.
/// </summary>
public class RealmManifestExportTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Export_is_structure_only_and_round_trips_with_apply_and_password_set()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();
        var exporter = factory.Services.GetRequiredService<RealmManifestExporter>();

        const string slug = "exporttest";
        var manifest = new RealmManifest
        {
            Apps =
            [
                new RealmManifestApp { Slug = "ex-app", DisplayName = "Ex App",
                    Permissions = [new RealmManifestPermission("ex", "read")] },
            ],
            Clients =
            [
                new RealmManifestClient
                {
                    ClientId = "ex-web",
                    ClientType = "confidential",
                    RedirectUris = ["https://ex.test/cb"],
                    Scopes = ["openid"],
                    AllowedGrantTypes = ["authorization_code", "refresh_token"],
                    AccessTokenType = "Jwt",
                    RequireDpop = true,
                    AccessTokenLifetime = 600,
                    Apps = ["ex-app"],
                },
            ],
            Users = [new RealmManifestUser { Key = "bob", Email = "bob@ex.test", UserName = "bob" }], // passwordless
        };
        Assert.False((await ProvisionRealmAsync(factory, Shell(slug), manifest, ct)).IsError);

        // ── Export ────────────────────────────────────────────────────────────
        var exported = await exporter.ExportRealmAsync(slug, ct);
        Assert.False(exported.IsError, exported.IsError ? exported.FirstError.Description : string.Empty);
        var m = exported.Value;

        // Structure-only: no client secret, no user password.
        var exClient = Assert.Single(m.Clients, c => c.ClientId == "ex-web");
        Assert.Null(exClient.ClientSecret);
        Assert.Contains("openid", exClient.Scopes);
        Assert.Contains("ex-app", exClient.Apps);
        Assert.Equal("Jwt", exClient.AccessTokenType); // token format round-trips through export
        Assert.Equal(true, exClient.RequireDpop);
        Assert.Equal(600, exClient.AccessTokenLifetime);
        var exUser = Assert.Single(m.Users, u => u.UserName == "bob");
        Assert.Null(exUser.Password);

        // Seeded entities that can't cleanly re-apply are excluded; the authored app survives.
        Assert.Contains(m.Apps, a => a.Slug == "ex-app");
        Assert.DoesNotContain(m.Apps, a => a.Slug == "modgud");     // system app
        Assert.DoesNotContain(m.Scopes, s => s.Name == "openid");   // standard scope

        // Settings ARE exported (all sections, current values) so you can see what to change.
        Assert.NotNull(m.Settings);
        Assert.Equal("Optional", m.Settings!.RegistrationFields!.Username); // shipped default
        // Write-only — never exported: the field is ABSENT (None), not an
        // explicit null (which would stage a clear under v2 merge-patch).
        Assert.False(m.Settings.SelfRegistration!.CaptchaSecret.HasValue);
        Assert.NotNull(m.Settings.BrowserSessions);                         // session policies export too
        Assert.NotNull(m.Settings.ClientSessions);
        Assert.NotNull(m.Settings.PositionSecurity);

        // ── Re-apply the UNEDITED export = idempotent ──────────────────────────
        Assert.False((await applier.UpdateRealmAsync(slug, m, ct: ct)).IsError);

        // ── Edit a setting and re-apply → it round-trips ───────────────────────
        var withSetting = m with
        {
            Settings = new UpdateRealmSettingsDto
            {
                RegistrationFields = new UpdateRegistrationFieldsSettingsDto { Username = "Required" },
            },
        };
        Assert.False((await applier.UpdateRealmAsync(slug, withSetting, ct: ct)).IsError);
        var reexport = await exporter.ExportRealmAsync(slug, ct);
        Assert.Equal("Required", reexport.Value.Settings!.RegistrationFields!.Username);

        // ── Edit: set bob's password, re-apply ─────────────────────────────────
        var withPassword = m with
        {
            Users = m.Users.Select(u => u.UserName == "bob" ? u with { Password = "Bobsecret1!" } : u).ToList(),
        };
        Assert.False((await applier.UpdateRealmAsync(slug, withPassword, ct: ct)).IsError);

        await InTenantAsync(factory, slug, async sp =>
        {
            var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var bob = await userManager.FindByNameAsync("bob");
            Assert.NotNull(bob);
            Assert.True(await userManager.HasPasswordAsync(bob!), "bob should have a password after apply");
        });
    }

    /// <summary>
    /// Settings that name entities by raw id — a realm's default self-registration groups,
    /// an App's allowed login providers, branding asset ids — are realm-local WIRING and do
    /// not travel, because an id means nothing in another realm. Carrying them breaks a
    /// transfer either loudly (asset and provider ids ARE validated, so the whole apply
    /// fails on a reference the author never chose) or quietly (default group ids are not,
    /// so they would be stored dangling). They are left out instead, which under merge-patch
    /// is "unchanged" — so a same-realm re-apply keeps the stored wiring exactly as it was,
    /// which is what this test pins down.
    /// </summary>
    [Fact]
    public async Task Export_leaves_realm_local_id_references_in_settings_behind()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();
        var exporter = factory.Services.GetRequiredService<RealmManifestExporter>();

        const string slug = "localrefs";
        var groupId = Guid.NewGuid();
        var groupRef = new BuildingBlocks.Helper.ShortGuid(groupId).ToString();
        var seeded = await ProvisionRealmAsync(factory, Shell(slug), new RealmManifest
        {
            Groups = [new RealmManifestGroup { Name = "Newcomers", Id = groupRef }],
        }, ct);
        Assert.False(seeded.IsError, seeded.IsError ? seeded.FirstError.Description : string.Empty);

        // Wire the realm up the way an admin would: new users land in a default group.
        // (Branding assets and provider ids take the same route out of the export; they
        // cannot be set to an arbitrary id here because the settings service validates
        // them against the realm — which is exactly why they must not travel.)
        await InTenantAsync(factory, slug, async sp =>
        {
            var patched = await sp.GetRequiredService<IRealmSettingsService>().PatchAsync(new UpdateRealmSettingsDto
            {
                SelfRegistration = new UpdateSelfRegistrationDto
                {
                    Enabled = true,
                    DefaultGroupIds = [groupRef],
                },
            }, ct);
            Assert.False(patched.IsError, patched.IsError ? patched.FirstError.Description : string.Empty);
        });

        // ── The export carries the settings, but not the ids. ──────────────────
        var exported = await exporter.ExportRealmAsync(slug, ct);
        Assert.False(exported.IsError, exported.IsError ? exported.FirstError.Description : string.Empty);
        var selfReg = exported.Value.Settings!.SelfRegistration!;
        Assert.True(selfReg.Enabled);                                    // the setting travels
        Assert.Null(selfReg.DefaultGroupIds);                            // the wiring does not
        // Branding travels as values, never as the asset reference.
        Assert.False(exported.Value.Settings.Branding!.LogoAssetId.HasValue);
        Assert.False(exported.Value.Settings.Branding.FaviconAssetId.HasValue);

        // ── Re-applying the export leaves the stored wiring untouched (absent =
        //    unchanged), so "does not travel" never means "gets cleared". ───────
        Assert.False((await applier.UpdateRealmAsync(slug, exported.Value, ct: ct)).IsError);
        await InTenantAsync(factory, slug, async sp =>
        {
            var live = await sp.GetRequiredService<IRealmSettingsService>().GetDtoAsync(ct);
            Assert.Equal([groupRef], live.SelfRegistration.DefaultGroupIds);
        });
    }

    /// <summary>
    /// Regression, and the sharp edge of "these ids do not travel": a PER-APP settings
    /// section is REPLACE, not merge-patch — <c>StageNonOriginAsync</c> rebuilds the whole
    /// section from the DTO whenever the section is present. So stripping the ids out of a
    /// section that still carries other values does not leave them alone, it CLEARS them.
    /// Re-applying an unedited export wiped an App's login-provider allow-list, and
    /// <c>LoginProviderIds = null</c> means "every enabled provider" — a silent widening of
    /// the App's authentication surface, with a plan that read "unchanged".
    /// </summary>
    [Fact]
    public async Task Re_applying_an_export_keeps_per_app_settings_that_do_not_travel()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();
        var exporter = factory.Services.GetRequiredService<RealmManifestExporter>();

        const string slug = "appwiring";
        var groupId = Guid.NewGuid();
        var groupRef = new BuildingBlocks.Helper.ShortGuid(groupId).ToString();
        var providerId = Guid.NewGuid();
        var providerRef = new BuildingBlocks.Helper.ShortGuid(providerId).ToString();

        var provisioned = await ProvisionRealmAsync(factory, Shell(slug), new RealmManifest
        {
            Apps = [new RealmManifestApp { Slug = "shop", DisplayName = "Shop" }],
            Groups = [new RealmManifestGroup { Name = "Newcomers", Id = groupRef }],
            LoginProviders =
            [
                new RealmManifestLoginProvider
                {
                    Slug = "corp", Id = providerRef, Flavor = "GenericOidc", DisplayName = "Corp",
                    ClientId = "corp-client",
                    FlavorData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                        """{"MetadataUri":"https://idp.example.com/.well-known/openid-configuration"}"""),
                },
            ],
        }, ct);
        Assert.False(provisioned.IsError,
            provisioned.IsError ? provisioned.FirstError.Description : string.Empty);

        // Wire the App up through its own admin surface — the only place these belong.
        Guid appId = default;
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            appId = (await session.Query<Modgud.Authorization.Apps.App>()
                .SingleAsync(a => !a.IsDeleted && a.Slug == "shop", ct)).Id;
            var staged = await sp.GetRequiredService<IApplicationSettingsService>().StageNonOriginAsync(appId,
                new ApplicationSettingsDto
                {
                    LoginExperience = new ApplicationLoginExperienceDto { LoginProviderIds = [providerRef] },
                    SelfRegistration = new ApplicationSelfRegistrationDto { Enabled = true, DefaultGroupIds = [groupRef] },
                }, ct);
            Assert.False(staged.IsError, staged.IsError ? staged.FirstError.Description : string.Empty);
            await session.SaveChangesAsync(ct);
        });

        // Export → re-apply unedited. This is the plain "draft from export, apply" flow.
        var exported = await exporter.ExportRealmAsync(slug, ct);
        Assert.False(exported.IsError, exported.IsError ? exported.FirstError.Description : string.Empty);
        Assert.False((await applier.UpdateRealmAsync(slug, exported.Value, ct: ct)).IsError);

        await InTenantAsync(factory, slug, async sp =>
        {
            var live = await sp.GetRequiredService<IApplicationSettingsService>().GetAsync(appId, ct);
            Assert.False(live.IsError, live.IsError ? live.FirstError.Description : string.Empty);
            Assert.Equal([providerRef], live.Value.LoginExperience?.LoginProviderIds);
            Assert.Equal([groupRef], live.Value.SelfRegistration?.DefaultGroupIds);
        });
    }

    private static async Task InTenantAsync(
        ColdStartWebApplicationFactory factory, string slug, Func<IServiceProvider, Task> body)
    {
        using var _ = TenantContext.Enter(slug);
        using var scope = factory.Services.CreateScope();
        await body(scope.ServiceProvider);
    }
}
