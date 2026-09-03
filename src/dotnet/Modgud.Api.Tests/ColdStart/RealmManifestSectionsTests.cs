using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api;
using Modgud.Api.Features.Admin.Provisioning;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Applications;
using Modgud.Application.DTOs.Positions;
using Modgud.Application.DTOs.Realms;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Domain.PositionTerminals;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// The manifest sections added for feature parity with the admin surface: login
/// providers (OIDC/SAML federation), per-App settings overrides (ADR-0011, incl.
/// the Origin → host-routing sync), and positions (MG-FT policy + grants). Each
/// section must import, apply (merge), export, and prune through the SAME
/// canonical operations the admin API uses.
/// </summary>
public class RealmManifestSectionsTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task LoginProviders_import_apply_export_and_prune()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();
        var exporter = factory.Services.GetRequiredService<RealmManifestExporter>();

        const string slug = "lptest";
        RealmManifestLoginProvider Provider(string pslug, string name) => new()
        {
            Slug = pslug,
            Flavor = "GenericOidc",
            DisplayName = name,
            ClientId = $"{pslug}-client",
            ClientSecret = "upstream-secret-1!",
            Scopes = ["openid", "email"],
            FlavorData = JsonSerializer.Deserialize<JsonElement>(
                """{"MetadataUri":"https://idp.example.com/.well-known/openid-configuration"}"""),
            Enabled = false,
            AutoCreateUsers = true,
        };
        var manifest = new RealmManifest
        {
            Realm = new CreateRealmDto
            {
                Slug = slug,
                DisplayName = slug,
                Domains = [$"{slug}.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "admin", Email = $"admin@{slug}.test" },
            },
            LoginProviders = [Provider("corp-idp", "Corp IdP"), Provider("legacy-idp", "Legacy IdP")],
        };
        var import = await applier.ImportNewRealmAsync(manifest, ct);
        Assert.False(import.IsError, import.IsError ? import.FirstError.Description : string.Empty);

        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var corp = await session.Query<LoginProvider>().SingleAsync(p => !p.IsDeleted && p.Slug == "corp-idp", ct);
            Assert.Equal("Corp IdP", corp.DisplayName);
            Assert.Equal(LoginProviderType.Oidc, corp.Type);
            Assert.Equal("corp-idp-client", corp.ClientId);
            Assert.NotNull(corp.ClientSecretEncrypted); // InitialClientSecret stored (encrypted)
            Assert.True(corp.AutoCreateUsers);
            Assert.Contains("email", corp.Scopes);
            Assert.NotNull(corp.FlavorData);
            Assert.True(await session.Query<LoginProvider>().AnyAsync(p => !p.IsDeleted && p.Slug == "legacy-idp", ct));
        });

        // ── Export: providers round-trip WITHOUT the secret; the built-in Internal
        //    provider is seeded infra and never exported. ──────────────────────────
        var exported = await exporter.ExportRealmAsync(slug, ct);
        Assert.False(exported.IsError);
        var exCorp = Assert.Single(exported.Value.LoginProviders, p => p.Slug == "corp-idp");
        Assert.Null(exCorp.ClientSecret);
        Assert.Equal("GenericOidc", exCorp.Flavor);
        Assert.NotNull(exCorp.FlavorData);
        Assert.DoesNotContain(exported.Value.LoginProviders, p => p.Flavor == "internal");

        // ── Apply: update corp-idp in place + PRUNE legacy-idp; Internal survives. ─
        var v2 = manifest with
        {
            LoginProviders = [Provider("corp-idp", "Corp IdP v2") with { ClientSecret = null }],
        };
        var applied = await applier.UpdateRealmAsync(v2, prune: true, deletions: null, ct);
        Assert.False(applied.IsError, applied.IsError ? applied.FirstError.Description : string.Empty);

        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var corp = await session.Query<LoginProvider>().SingleAsync(p => !p.IsDeleted && p.Slug == "corp-idp", ct);
            Assert.Equal("Corp IdP v2", corp.DisplayName);
            Assert.NotNull(corp.ClientSecretEncrypted); // no secret in manifest = keep the stored one
            Assert.False(await session.Query<LoginProvider>().AnyAsync(p => !p.IsDeleted && p.Slug == "legacy-idp", ct),
                "legacy-idp pruned");
            Assert.True(await session.Query<LoginProvider>().AnyAsync(p => !p.IsDeleted && p.IsBuiltIn, ct),
                "built-in Internal provider protected from prune");
        });

        // ── The Internal provider is reserved — declaring one is a contract error. ─
        var withInternal = manifest with
        {
            LoginProviders = [new RealmManifestLoginProvider
            {
                Slug = "my-internal", Flavor = "internal", DisplayName = "Nope", Type = "Internal",
            }],
        };
        var rejected = await applier.UpdateRealmAsync(withInternal, ct: ct);
        Assert.True(rejected.IsError);
        Assert.Equal("Manifest.InternalProviderReserved", rejected.FirstError.Code);
    }

    [Fact]
    public async Task App_settings_override_applies_routes_and_exports()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();
        var exporter = factory.Services.GetRequiredService<RealmManifestExporter>();

        const string slug = "appset";
        RealmManifest Manifest(string productName) => new()
        {
            Realm = new CreateRealmDto
            {
                Slug = slug,
                DisplayName = slug,
                Domains = [$"{slug}.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "admin", Email = $"admin@{slug}.test" },
            },
            Apps =
            [
                new RealmManifestApp
                {
                    Slug = "shop",
                    DisplayName = "Shop",
                    Permissions = [new RealmManifestPermission("order", "read")],
                    Settings = new ApplicationSettingsDto
                    {
                        Branding = new ApplicationBrandingDto { ProductName = productName },
                        Origin = new ApplicationOriginDto { Subdomain = $"shop.{slug}.localhost" },
                    },
                },
                // A second app WITHOUT settings must not grow an override on export.
                new RealmManifestApp { Slug = "plain", DisplayName = "Plain" },
            ],
        };
        var import = await applier.ImportNewRealmAsync(Manifest("Shop!"), ct);
        Assert.False(import.IsError, import.IsError ? import.FirstError.Description : string.Empty);

        Guid shopAppId = default;
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            shopAppId = (await session.Query<App>().SingleAsync(a => !a.IsDeleted && a.Slug == "shop", ct)).Id;
            var settings = await sp.GetRequiredService<IApplicationSettingsService>().GetAsync(shopAppId, ct);
            Assert.False(settings.IsError);
            Assert.Equal("Shop!", settings.Value.Branding?.ProductName);
            Assert.Equal($"shop.{slug}.localhost", settings.Value.Origin?.Subdomain);
        });

        // Origin drives the GLOBAL host→App routing map (ADR-0011).
        var realm = await factory.Services.GetRequiredService<IRealmProvisioningService>()
            .GetRealmBySlugAsync(slug, ct);
        Assert.NotNull(realm);
        Assert.True(realm!.ApplicationDomains.TryGetValue($"shop.{slug}.localhost", out var routedAppId));
        Assert.Equal(shopAppId, routedAppId);

        // ── Export: only the app WITH an override carries Settings. ────────────────
        var exported = await exporter.ExportRealmAsync(slug, ct);
        Assert.False(exported.IsError);
        var exShop = Assert.Single(exported.Value.Apps, a => a.Slug == "shop");
        Assert.Equal("Shop!", exShop.Settings?.Branding?.ProductName);
        Assert.Equal($"shop.{slug}.localhost", exShop.Settings?.Origin?.Subdomain);
        Assert.Null(Assert.Single(exported.Value.Apps, a => a.Slug == "plain").Settings);

        // ── Apply: the settings patch updates in place. ────────────────────────────
        Assert.False((await applier.UpdateRealmAsync(Manifest("Shop v2"), ct: ct)).IsError);
        await InTenantAsync(factory, slug, async sp =>
        {
            var settings = await sp.GetRequiredService<IApplicationSettingsService>().GetAsync(shopAppId, ct);
            Assert.Equal("Shop v2", settings.Value.Branding?.ProductName);
        });
    }

    [Fact]
    public async Task Positions_are_feature_gated_and_import_apply_export_prune()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();
        var exporter = factory.Services.GetRequiredService<RealmManifestExporter>();
        var appSettings = factory.Services.GetRequiredService<AppSettings>();

        const string slug = "posten";
        RealmManifest Manifest(string purpose, params string[] grants) => ManifestFor(slug, purpose, grants);
        RealmManifest ManifestFor(string realmSlug, string purpose, params string[] grants) => new()
        {
            Realm = new CreateRealmDto
            {
                Slug = realmSlug,
                DisplayName = realmSlug,
                Domains = [$"{realmSlug}.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "admin", Email = $"admin@{realmSlug}.test" },
            },
            Users =
            [
                new RealmManifestUser { Key = "alice", Email = $"alice@{slug}.test", UserName = "alice", Password = "Passw0rd!23" },
                new RealmManifestUser { Key = "bob", Email = $"bob@{slug}.test", UserName = "bob", Password = "Passw0rd!23" },
            ],
            Positions =
            [
                new RealmManifestPosition
                {
                    AccountName = "gate.porter",
                    Purpose = purpose,
                    TerminalPolicy = new PositionTerminalPolicyUpdateDto
                    {
                        Enabled = true,
                        AllowedActivationProofs = [ActivationProofMethodIds.PersonalPasskey],
                        AllowedDeviceBindings = [DeviceBindingIds.Dpop],
                        StaffingSessionLifetimeMinutes = 60,
                        MaximumStaffingSessionLifetimeMinutes = 480,
                    },
                    Grants = [.. grants],
                },
            ],
        };

        // ── Feature dark: a manifest declaring positions must fail loudly (and the
        //    all-or-nothing import rolls the partial realm back). A separate slug —
        //    the rollback hard-deletes the tenant DB, and recreating the same slug in
        //    the same host would hit the disposed tenant data source.
        appSettings.Features.PositionTerminals = false;
        var gated = await applier.ImportNewRealmAsync(ManifestFor("posgate", "Gate", "alice"), ct);
        Assert.True(gated.IsError);
        Assert.Equal("Manifest.FeatureDisabled", gated.FirstError.Code);

        // ── Feature on: import creates position + policy + grant. ──────────────────
        appSettings.Features.PositionTerminals = true;
        var import = await applier.ImportNewRealmAsync(Manifest("Gate", "alice"), ct);
        Assert.False(import.IsError, import.IsError ? import.FirstError.Description : string.Empty);

        Guid positionId = default, aliceId = default, bobId = default;
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var fn = await session.Query<PositionPrincipal>().SingleAsync(p => !p.IsDeleted && p.AccountName == "gate.porter", ct);
            positionId = fn.Id;
            Assert.Equal("Gate", fn.Purpose);
            Assert.True(fn.IsActive);
            Assert.True(fn.TerminalPolicy.Enabled);
            Assert.Equal([ActivationProofMethodIds.PersonalPasskey], fn.TerminalPolicy.AllowedActivationProofs);
            Assert.Equal(TimeSpan.FromMinutes(60), fn.TerminalPolicy.StaffingSessionLifetime);

            aliceId = (await session.Query<Person>().SingleAsync(p => p.AccountName == "alice", ct)).Id;
            bobId = (await session.Query<Person>().SingleAsync(p => p.AccountName == "bob", ct)).Id;
            var grant = Assert.Single(await session.Query<PositionGrant>()
                .Where(g => g.PositionPrincipalId == fn.Id && g.Status != PositionGrantStatus.Revoked).ToListAsync(ct));
            Assert.Equal(aliceId, grant.UserId);
        });

        // ── Export: position + grant keys round-trip (terminal slots never do). ────
        var exported = await exporter.ExportRealmAsync(slug, ct);
        Assert.False(exported.IsError);
        var exPos = Assert.Single(exported.Value.Positions);
        Assert.Equal("gate.porter", exPos.AccountName);
        Assert.True(exPos.TerminalPolicy?.Enabled);
        Assert.Equal(["alice"], exPos.Grants);

        // ── Apply: merge purpose + REPLACE the grant set (alice → bob). ────────────
        var applied = await applier.UpdateRealmAsync(Manifest("Gate v2", "bob"), ct: ct);
        Assert.False(applied.IsError, applied.IsError ? applied.FirstError.Description : string.Empty);
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var fn = await session.Query<PositionPrincipal>().SingleAsync(p => !p.IsDeleted && p.AccountName == "gate.porter", ct);
            Assert.Equal(positionId, fn.Id); // in-place, not recreated
            Assert.Equal("Gate v2", fn.Purpose);
            var live = await session.Query<PositionGrant>()
                .Where(g => g.PositionPrincipalId == fn.Id && g.Status != PositionGrantStatus.Revoked).ToListAsync(ct);
            Assert.Equal(bobId, Assert.Single(live).UserId); // alice revoked, bob issued
        });

        // ── Prune: a position absent from the manifest is deleted via the canonical
        //    cascade (soft delete; grants stay history). ─────────────────────────────
        var noPositions = Manifest("unused") with { Positions = [] };
        Assert.False((await applier.UpdateRealmAsync(noPositions, prune: true, deletions: null, ct)).IsError);
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            Assert.False(await session.Query<PositionPrincipal>().AnyAsync(p => !p.IsDeleted && p.AccountName == "gate.porter", ct),
                "position pruned");
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
