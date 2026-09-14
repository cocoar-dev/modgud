using System.Text.Json;
using BuildingBlocks.Helper;
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
using Modgud.Domain.Common;
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
        // Pinned ids: the v2 apply below has to mean the SAME corp-idp (ADR 0024).
        var providerIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["corp-idp"] = new ShortGuid(Guid.NewGuid()).ToString(),
            ["legacy-idp"] = new ShortGuid(Guid.NewGuid()).ToString(),
        };
        RealmManifestLoginProvider Provider(string pslug, string name) => new()
        {
            Slug = pslug,
            Id = providerIds.GetValueOrDefault(pslug),
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
            LoginProviders = [Provider("corp-idp", "Corp IdP"), Provider("legacy-idp", "Legacy IdP")],
        };
        var import = await ProvisionRealmAsync(factory, Shell(slug), manifest, ct);
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
        var applied = await applier.UpdateRealmAsync(slug, v2, prune: true, deletions: null, ct);
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
        var rejected = await applier.UpdateRealmAsync(slug, withInternal, ct: ct);
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
        var shopId = new ShortGuid(Guid.NewGuid()).ToString();
        var plainId = new ShortGuid(Guid.NewGuid()).ToString();
        RealmManifest Manifest(string productName) => new()
        {
            Apps =
            [
                new RealmManifestApp
                {
                    Slug = "shop",
                    Id = shopId,
                    DisplayName = "Shop",
                    Permissions = [new RealmManifestPermission("order", "read")],
                    Settings = new ApplicationSettingsDto
                    {
                        Branding = new ApplicationBrandingDto { ProductName = productName },
                        Origin = new ApplicationOriginDto { Subdomain = $"shop.{slug}.localhost" },
                    },
                },
                // A second app WITHOUT settings must not grow an override on export.
                new RealmManifestApp { Slug = "plain", Id = plainId, DisplayName = "Plain" },
            ],
        };
        var import = await ProvisionRealmAsync(factory, Shell(slug), Manifest("Shop!"), ct);
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
        Assert.False((await applier.UpdateRealmAsync(slug, Manifest("Shop v2"), ct: ct)).IsError);
        await InTenantAsync(factory, slug, async sp =>
        {
            var settings = await sp.GetRequiredService<IApplicationSettingsService>().GetAsync(shopAppId, ct);
            Assert.Equal("Shop v2", settings.Value.Branding?.ProductName);
        });

        // ── The Origin route is a POST-COMMIT consequence of the apply. A manifest that
        //    moves the subdomain and then fails further down rolls back — and the global
        //    host map must still point the OLD host at the app, with no trace of the new one.
        //    (The route lives in another database; before this it was written mid-apply.) ──
        RealmManifest MovedOrigin(bool andFail) => new()
        {
            Apps =
            [
                new RealmManifestApp
                {
                    Slug = "shop", Id = shopId, DisplayName = "Shop",
                    Permissions = [new RealmManifestPermission("order", "read")],
                    Settings = new ApplicationSettingsDto
                    {
                        Branding = new ApplicationBrandingDto { ProductName = "Shop v2" },
                        Origin = new ApplicationOriginDto { Subdomain = $"shop2.{slug}.localhost" },
                    },
                },
                // Without an Id this entry CREATES, and the taken slug fails the apply (ADR 0024).
                .. andFail ? new[] { new RealmManifestApp { Slug = "plain", DisplayName = "Plain again" } } : [],
            ],
        };
        var failed = await applier.UpdateRealmAsync(slug, MovedOrigin(andFail: true), ct: ct);
        Assert.True(failed.IsError);
        Assert.Equal("App.DuplicateSlug", failed.FirstError.Code);
        var realms = factory.Services.GetRequiredService<IRealmProvisioningService>();
        var afterRollback = (await realms.GetRealmBySlugAsync(slug, ct))!;
        Assert.Equal(shopAppId, afterRollback.ApplicationDomains[$"shop.{slug}.localhost"]);
        Assert.False(afterRollback.ApplicationDomains.ContainsKey($"shop2.{slug}.localhost"), "no route for a rolled-back apply");
        await InTenantAsync(factory, slug, async sp =>
            Assert.Equal($"shop.{slug}.localhost",
                (await sp.GetRequiredService<IApplicationSettingsService>().GetAsync(shopAppId, ct)).Value.Origin?.Subdomain));

        // The same move without the failure lands: route moved, stale host gone.
        Assert.False((await applier.UpdateRealmAsync(slug, MovedOrigin(andFail: false), ct: ct)).IsError);
        var afterMove = (await realms.GetRealmBySlugAsync(slug, ct))!;
        Assert.Equal(shopAppId, afterMove.ApplicationDomains[$"shop2.{slug}.localhost"]);
        Assert.False(afterMove.ApplicationDomains.ContainsKey($"shop.{slug}.localhost"));

        // Pruning the app removes its route — after the commit, like the write.
        var pruned = await applier.UpdateRealmAsync(slug,
            new RealmManifest { Apps = [new RealmManifestApp { Slug = "plain", Id = plainId, DisplayName = "Plain" }] },
            prune: true, deletions: null, ct);
        Assert.False(pruned.IsError, pruned.IsError ? pruned.FirstError.Description : string.Empty);
        var afterPrune = (await realms.GetRealmBySlugAsync(slug, ct))!;
        Assert.DoesNotContain(afterPrune.ApplicationDomains, kv => kv.Value == shopAppId);
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
        // Pinned ids throughout — the apply below has to mean the SAME position, and a
        // staffing grant has to mean a particular person (ADR 0024).
        var aliceKey = new ShortGuid(Guid.NewGuid()).ToString();
        var bobKey = new ShortGuid(Guid.NewGuid()).ToString();
        var positionKey = new ShortGuid(Guid.NewGuid()).ToString();
        ManifestRef Grant(string user) => new()
        {
            Key = user, Id = user == "alice" ? aliceKey : bobKey,
        };
        RealmManifest Manifest(string purpose, params string[] grants) => ManifestFor(slug, purpose, grants);
        RealmManifest ManifestFor(string realmSlug, string purpose, params string[] grants) => new()
        {
            Users =
            [
                new RealmManifestUser { Key = "alice", Id = aliceKey, Email = $"alice@{slug}.test", UserName = "alice", Password = "Passw0rd!23" },
                new RealmManifestUser { Key = "bob", Id = bobKey, Email = $"bob@{slug}.test", UserName = "bob", Password = "Passw0rd!23" },
            ],
            Positions =
            [
                new RealmManifestPosition
                {
                    AccountName = "gate.porter",
                    Id = positionKey,
                    Purpose = purpose,
                    TerminalPolicy = new PositionTerminalPolicyUpdateDto
                    {
                        Enabled = true,
                        AllowedActivationProofs = [ActivationProofMethodIds.PersonalPasskey],
                        AllowedDeviceBindings = [DeviceBindingIds.Dpop],
                        StaffingSessionLifetimeMinutes = 60,
                        MaximumStaffingSessionLifetimeMinutes = 480,
                    },
                    Grants = [.. grants.Select(Grant)],
                },
            ],
        };

        // ── Feature dark: a manifest declaring positions must fail loudly (and the
        //    all-or-nothing import rolls the partial realm back). A separate slug —
        //    the rollback hard-deletes the tenant DB, and recreating the same slug in
        //    the same host would hit the disposed tenant data source.
        appSettings.Features.PositionTerminals = false;
        // Its own realm — the feature-gate probe must not consume the slug the real
        // import below needs (creating a realm and filling it are two operations now).
        var gated = await ProvisionRealmAsync(factory, Shell("posgate"), ManifestFor("posgate", "Gate", "alice"), ct);
        Assert.True(gated.IsError);
        Assert.Equal("Manifest.FeatureDisabled", gated.FirstError.Code);

        // ── Feature on: import creates position + policy + grant. ──────────────────
        appSettings.Features.PositionTerminals = true;
        var import = await ProvisionRealmAsync(factory, Shell(slug), Manifest("Gate", "alice"), ct);
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

        // ── Export: position + grant keys round-trip (slots are covered separately). ─
        var exported = await exporter.ExportRealmAsync(slug, ct);
        Assert.False(exported.IsError);
        var exPos = Assert.Single(exported.Value.Positions);
        Assert.Equal("gate.porter", exPos.AccountName);
        Assert.True(exPos.TerminalPolicy?.Enabled);
        // Exported references are { Key, Id } — the readable half is the user key.
        Assert.Equal(["alice"], exPos.Grants!.Select(g => g.Key));

        // ── Apply: merge purpose + REPLACE the grant set (alice → bob). ────────────
        var applied = await applier.UpdateRealmAsync(slug, Manifest("Gate v2", "bob"), ct: ct);
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
        Assert.False((await applier.UpdateRealmAsync(slug, noPositions, prune: true, deletions: null, ct)).IsError);
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            Assert.False(await session.Query<PositionPrincipal>().AnyAsync(p => !p.IsDeleted && p.AccountName == "gate.porter", ct),
                "position pruned");
        });
    }

    [Fact]
    public async Task Terminal_slots_travel_as_configuration_and_never_as_enrollment()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();
        var exporter = factory.Services.GetRequiredService<RealmManifestExporter>();
        var planner = factory.Services.GetRequiredService<RealmManifestPlanner>();
        factory.Services.GetRequiredService<AppSettings>().Features.PositionTerminals = true;

        const string slug = "termslots";
        var frontKey = new ShortGuid(Guid.NewGuid()).ToString();
        var backKey = new ShortGuid(Guid.NewGuid()).ToString();
        PositionTerminalPolicyUpdateDto Policy() => new()
        {
            Enabled = true,
            AllowedActivationProofs = [ActivationProofMethodIds.PersonalPasskey],
            AllowedDeviceBindings = [DeviceBindingIds.Dpop],
            StaffingSessionLifetimeMinutes = 60,
            MaximumStaffingSessionLifetimeMinutes = 480,
        };
        RealmManifest Manifest(List<RealmManifestTerminal>? terminals) => new()
        {
            Positions =
            [
                // The slot serves a position declared FURTHER DOWN — slots apply after
                // every position, so a forward handle is fine.
                new RealmManifestPosition
                {
                    AccountName = "gate.front", Id = frontKey, TerminalPolicy = Policy(), Terminals = terminals,
                },
                new RealmManifestPosition { AccountName = "gate.back", Id = backKey, TerminalPolicy = Policy() },
            ],
        };

        // ── Create: the slot and its terminal-managed client, Pending (no enrollment). ─
        var import = await ProvisionRealmAsync(factory, Shell(slug), Manifest(
        [
            new RealmManifestTerminal
            {
                DisplayName = "Gate left", WebAuthnRpId = "Kiosk.Example.Test", Location = "Hall A",
                AllowedPositions = [new ManifestRef { Key = "gate.back", Id = backKey }],
            },
        ]), ct);
        Assert.False(import.IsError, import.IsError ? import.FirstError.Description : string.Empty);

        Guid slotId = default;
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var slot = Assert.Single(await session.Query<TerminalEnrollment>().ToListAsync(ct));
            slotId = slot.Id;
            Assert.Equal(new ShortGuid(frontKey).Guid, slot.PositionPrincipalId);
            Assert.Equal("Gate left", slot.DisplayName);
            Assert.Equal("Hall A", slot.Location);
            Assert.Equal("kiosk.example.test", slot.WebAuthnRpId);
            Assert.Equal(DeviceBindingIds.Dpop, slot.Binding);
            Assert.Equal(TerminalEnrollmentStatus.Pending, slot.Status);
            Assert.Null(slot.EnrollmentAuthorizationId);
            Assert.Equal(
                new[] { new ShortGuid(frontKey).Guid, new ShortGuid(backKey).Guid }.Order(),
                slot.EffectiveAllowedPositionIds.Order());
            var client = await session.LoadAsync<Modgud.Domain.OAuth.Applications.OAuthApplicationState>(slot.OAuthApplicationId, ct);
            Assert.NotNull(client);
            Assert.Equal(slot.ClientId, client.ClientId);
        });

        // ── Export carries the slot (by id, served positions by identity); re-applying
        //    the export is a no-op, not a second slot. ──────────────────────────────────
        var exported = await exporter.ExportRealmAsync(slug, ct);
        Assert.False(exported.IsError);
        var exSlot = Assert.Single(exported.Value.Positions.Single(p => p.AccountName == "gate.front").Terminals!);
        Assert.Equal(new ShortGuid(slotId).ToString(), exSlot.Id);
        Assert.Equal("Hall A", exSlot.Location.Value);
        Assert.Equal(["gate.back"], exSlot.AllowedPositions!.Select(r => r.Key));
        Assert.Empty(exported.Value.Positions.Single(p => p.AccountName == "gate.back").Terminals!);
        // The slot's client is terminal-managed and never travels in Clients.
        Assert.DoesNotContain(exported.Value.Clients, c => c.ClientId.StartsWith("terminal.", StringComparison.Ordinal));
        var reapplied = await applier.UpdateRealmAsync(slug, exported.Value, ct: ct);
        Assert.False(reapplied.IsError, reapplied.IsError
            ? $"{reapplied.FirstError.Code}: {reapplied.FirstError.Description} [{string.Join(", ", reapplied.FirstError.Metadata?.Select(kv => $"{kv.Key}={kv.Value}") ?? [])}]"
            : string.Empty);
        await InTenantAsync(factory, slug, async sp =>
            Assert.Single(await sp.GetRequiredService<IDocumentSession>().Query<TerminalEnrollment>().ToListAsync(ct)));

        // ── Update by id: name/location change, the served set shrinks to the owner;
        //    a slot the entry does not list is KEPT (the plan says so). ──────────────────
        var renamed = Manifest(
        [
            new RealmManifestTerminal { Id = exSlot.Id, DisplayName = "Gate left (renamed)", Location = new Optional<string?>(null), AllowedPositions = [] },
            new RealmManifestTerminal { DisplayName = "Gate right", WebAuthnRpId = "kiosk.example.test" },
        ]);
        var plan = await planner.PlanAsync(slug, renamed, prune: false, ct: ct);
        Assert.False(plan.IsError, plan.IsError ? plan.FirstError.Description : string.Empty);
        var frontEntry = plan.Value.Sections.Single(s => s.Name == "positions").Entries.Single(e => e.Key == "gate.front");
        Assert.Contains(frontEntry.Notes, n => n.Contains("'Gate right' is created with a fresh terminal client"));

        Assert.False((await applier.UpdateRealmAsync(slug, renamed, ct: ct)).IsError);
        await InTenantAsync(factory, slug, async sp =>
        {
            var slots = await sp.GetRequiredService<IDocumentSession>().Query<TerminalEnrollment>().ToListAsync(ct);
            Assert.Equal(2, slots.Count);
            var left = slots.Single(s => s.Id == slotId);
            Assert.Equal("Gate left (renamed)", left.DisplayName);
            Assert.Null(left.Location);
            Assert.Equal([new ShortGuid(frontKey).Guid], left.EffectiveAllowedPositionIds);
        });
        Assert.False((await applier.UpdateRealmAsync(slug, Manifest([]), ct: ct)).IsError);
        var kept = await planner.PlanAsync(slug, Manifest([]), prune: false, ct: ct);
        Assert.Contains(kept.Value.Sections.Single(s => s.Name == "positions").Entries.Single(e => e.Key == "gate.front").Notes,
            n => n.Contains("'Gate right' is not listed — it is KEPT"));
        await InTenantAsync(factory, slug, async sp =>
            Assert.Equal(2, (await sp.GetRequiredService<IDocumentSession>().Query<TerminalEnrollment>().ToListAsync(ct)).Count));

        // ── The RP ID is immutable — passkeys hang off it. ─────────────────────────────
        var moved = await applier.UpdateRealmAsync(slug, Manifest(
            [new RealmManifestTerminal { Id = exSlot.Id, DisplayName = "Gate left (renamed)", WebAuthnRpId = "other.example.test" }]), ct: ct);
        Assert.True(moved.IsError);
        Assert.Equal("Terminal.RpIdImmutable", moved.FirstError.Code);

        // ── A slot needs a position that allows terminals; a bare handle is validated. ─
        var noPolicy = await applier.UpdateRealmAsync(slug, new RealmManifest
        {
            Positions =
            [
                new RealmManifestPosition
                {
                    AccountName = "gate.side", Id = "#side",
                    Terminals = [new RealmManifestTerminal { DisplayName = "Side", WebAuthnRpId = "kiosk.example.test" }],
                },
            ],
        }, ct: ct);
        Assert.True(noPolicy.IsError);
        Assert.Equal("Terminal.TerminalPolicyDisabled", noPolicy.FirstError.Code);
    }

    private static async Task InTenantAsync(
        ColdStartWebApplicationFactory factory, string slug, Func<IServiceProvider, Task> body)
    {
        using var _ = TenantContext.Enter(slug);
        using var scope = factory.Services.CreateScope();
        await body(scope.ServiceProvider);
    }
}
