using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modgud.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Admin.Apps;
using Modgud.Application.DTOs.Applications;
using Marten;
using Modgud.Domain.Applications;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.Realms;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Pins the <c>AppSettings.Features.PageBuilder</c> gate and the ADR-0013
/// variant + activation model on the customization-pages surface. While the
/// flag is off (default) every endpoint is invisible (404). While on, a slot
/// owns a variant library plus an active selection; the effective active
/// schema is what <c>/api/app-info</c> publishes and the runtime renders.
///
/// <para>Mutates the AppSettings singleton in-process. The fixture uses
/// <c>[Collection]</c> so tests run sequentially — each test restores the flag
/// in its finally block.</para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PageBuilderFeatureFlagTests : IntegrationTestBase
{
    public PageBuilderFeatureFlagTests(SharedPostgresFixture fixture) : base(fixture) { }

    // ── helpers ──

    /// <summary>Create a realm variant for the slot and activate it. Returns the
    /// new variant id.</summary>
    private async Task<string> SeedRealmActive(string slug, string name, string schema, CancellationToken ct)
    {
        var post = await Client.PostAsJsonAsync($"/api/admin/customization/pages/{slug}/variants",
            new { Name = name, Schema = schema }, ct);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        using var created = JsonDocument.Parse(await post.Content.ReadAsStringAsync(ct));
        var id = created.RootElement.GetProperty("Id").GetString()!;

        var active = await Client.PutAsJsonAsync($"/api/admin/customization/pages/{slug}/active",
            new { ActiveVariantId = id }, ct);
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        return id;
    }

    private async Task<string?> AppInfoActiveSchema(string slug, CancellationToken ct, string? returnUrl = null)
    {
        var url = returnUrl is null ? "/api/app-info" : $"/api/app-info?returnUrl={returnUrl}";
        using var doc = JsonDocument.Parse(await (await Client.GetAsync(url, ct)).Content.ReadAsStringAsync(ct));
        var pages = doc.RootElement.GetProperty("Pages");
        return pages.TryGetProperty(slug, out var s) ? s.GetString() : null;
    }

    private async Task PublishRealmVariant(string slug, string id, CancellationToken ct)
    {
        var response = await Client.PostAsync(
            $"/api/admin/customization/pages/{slug}/variants/{id}/publish",
            content: null,
            ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── feature-flag gating ──

    [Fact]
    public async Task GetPageSlot_returns_404_when_feature_off()
    {
        Factory.Services.GetRequiredService<AppSettings>().Features.PageBuilder = false;

        var resp = await Client.GetAsync("/api/admin/customization/pages/login",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task CreateVariant_returns_404_when_feature_off()
    {
        Factory.Services.GetRequiredService<AppSettings>().Features.PageBuilder = false;

        var resp = await Client.PostAsJsonAsync("/api/admin/customization/pages/login/variants",
            new { Name = "X", Schema = "{\"type\":\"page\",\"children\":[]}" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── variant CRUD + activation ──

    [Fact]
    public async Task GetPageSlot_works_when_feature_on()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        try
        {
            var resp = await Client.GetAsync("/api/admin/customization/pages/login",
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("login", doc.RootElement.GetProperty("Slug").GetString());
        }
        finally { settings.Features.PageBuilder = false; }
    }

    [Fact]
    public async Task Create_activate_persists_and_surfaces_effective_schema()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var schema = "{\"id\":\"page\",\"type\":\"page\",\"schemaVersion\":4,\"children\":[{\"id\":\"title\",\"type\":\"heading\",\"props\":{\"text\":\"Primary\",\"level\":1}}]}";
            var id = await SeedRealmActive("login", "Primary", schema, ct);

            // The variant round-trips with its schema.
            using (var variant = JsonDocument.Parse(await (await Client.GetAsync(
                $"/api/admin/customization/pages/login/variants/{id}", ct)).Content.ReadAsStringAsync(ct)))
            {
                Assert.Equal(schema, variant.RootElement.GetProperty("Schema").GetString());
            }

            // The slot reports it as active.
            using (var slot = JsonDocument.Parse(await (await Client.GetAsync(
                "/api/admin/customization/pages/login", ct)).Content.ReadAsStringAsync(ct)))
            {
                Assert.Equal(id, slot.RootElement.GetProperty("ActiveVariantId").GetString());
                Assert.Single(slot.RootElement.GetProperty("Variants").EnumerateArray());
            }

            Assert.Equal(schema, await AppInfoActiveSchema("login", ct));
        }
        finally { settings.Features.PageBuilder = false; }
    }

    [Fact]
    public async Task Variant_without_activation_stays_builtin()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            // Creating a variant does NOT activate it — the slot stays built-in
            // until explicitly activated (ADR-0013: existence ≠ active).
            var post = await Client.PostAsJsonAsync("/api/admin/customization/pages/logout/variants",
                new { Name = "Draft", Schema = "{\"type\":\"page\",\"children\":[]}" }, ct);
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);

            Assert.Null(await AppInfoActiveSchema("logout", ct));
        }
        finally { settings.Features.PageBuilder = false; }
    }

    [Fact]
    public async Task Saving_active_variant_keeps_published_revision_live_until_publish()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            const string published = "{\"id\":\"page\",\"type\":\"page\",\"schemaVersion\":4,\"children\":[{\"id\":\"v1\",\"type\":\"paragraph\",\"props\":{\"text\":\"Published\"}}]}";
            const string draft = "{\"id\":\"page\",\"type\":\"page\",\"schemaVersion\":4,\"children\":[{\"id\":\"v2\",\"type\":\"paragraph\",\"props\":{\"text\":\"Draft\"}}]}";
            var id = await SeedRealmActive("login", "Revisioned", published, ct);

            var save = await Client.PutAsJsonAsync($"/api/admin/customization/pages/login/variants/{id}",
                new { Name = "Revisioned", Schema = draft }, ct);
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            Assert.Equal(published, await AppInfoActiveSchema("login", ct));

            await PublishRealmVariant("login", id, ct);
            Assert.Equal(draft, await AppInfoActiveSchema("login", ct));

            using var variant = JsonDocument.Parse(await (await Client.GetAsync(
                $"/api/admin/customization/pages/login/variants/{id}", ct)).Content.ReadAsStringAsync(ct));
            Assert.Equal(2, variant.RootElement.GetProperty("PublishedRevision").GetInt32());
            Assert.False(variant.RootElement.GetProperty("HasUnpublishedChanges").GetBoolean());
        }
        finally { settings.Features.PageBuilder = false; }
    }

    [Fact]
    public async Task Deactivating_reverts_to_builtin_without_deleting_the_variant()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var schema = "{\"id\":\"page\",\"type\":\"page\",\"schemaVersion\":4,\"children\":[]}";
            var id = await SeedRealmActive("password-forgot", "V", schema, ct);
            Assert.Equal(schema, await AppInfoActiveSchema("password-forgot", ct));

            // Deactivate (built-in) — variant stays in the library.
            var deactivate = await Client.PutAsJsonAsync("/api/admin/customization/pages/password-forgot/active",
                new { ActiveVariantId = (string?)null }, ct);
            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

            Assert.Null(await AppInfoActiveSchema("password-forgot", ct));
            using var slot = JsonDocument.Parse(await (await Client.GetAsync(
                "/api/admin/customization/pages/password-forgot", ct)).Content.ReadAsStringAsync(ct));
            Assert.Single(slot.RootElement.GetProperty("Variants").EnumerateArray()); // not deleted
            Assert.Equal(id, slot.RootElement.GetProperty("Variants")[0].GetProperty("Id").GetString());
        }
        finally { settings.Features.PageBuilder = false; }
    }

    [Fact]
    public async Task Activating_unknown_variant_is_rejected()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var resp = await Client.PutAsJsonAsync("/api/admin/customization/pages/login/active",
                new { ActiveVariantId = "does-not-exist" }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { settings.Features.PageBuilder = false; }
    }

    // ── legacy migration ──

    [Fact]
    public async Task Legacy_single_schema_migrates_to_an_active_variant()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            const string legacy = "{\"type\":\"page\",\"schemaVersion\":2,\"children\":[{\"id\":\"x\",\"type\":\"heading\",\"props\":{\"text\":\"Legacy\"}}]}";
            using (var scope = Factory.Services.CreateScope())
            {
                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
                var existing = await session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId, ct)
                    ?? new RealmSettingsDoc { Id = RealmSettingsDoc.SingletonId, CreatedAt = DateTimeOffset.UtcNow };
                existing.Pages = new Dictionary<string, string> { ["login"] = legacy };
                existing.PageSlots = null;
                session.Store(existing);
                await session.SaveChangesAsync(ct);
            }

            // Listing the slot migrates it: one active "Custom" variant.
            using (var slot = JsonDocument.Parse(await (await Client.GetAsync(
                "/api/admin/customization/pages/login", ct)).Content.ReadAsStringAsync(ct)))
            {
                var variants = slot.RootElement.GetProperty("Variants").EnumerateArray().ToArray();
                Assert.Single(variants);
                Assert.Equal(slot.RootElement.GetProperty("ActiveVariantId").GetString(),
                    variants[0].GetProperty("Id").GetString());
            }

            Assert.Equal(legacy, await AppInfoActiveSchema("login", ct));
        }
        finally { settings.Features.PageBuilder = false; }
    }

    // ── application: select from realm variants ──

    [Fact]
    public async Task Application_selects_a_realm_variant_and_survives_settings_save()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            const string realmSchema = "{\"id\":\"page\",\"type\":\"page\",\"schemaVersion\":4,\"children\":[]}";
            const string altSchema = "{\"id\":\"page\",\"type\":\"page\",\"schemaVersion\":4,\"children\":[{\"id\":\"t\",\"type\":\"heading\",\"props\":{\"text\":\"Alt\"}}]}";

            await SeedRealmActive("logout", "Realm", realmSchema, ct);
            // A second, non-active realm variant the App can pick.
            var altPost = await Client.PostAsJsonAsync("/api/admin/customization/pages/logout/variants",
                new { Name = "Alt", Schema = altSchema }, ct);
            using var altCreated = JsonDocument.Parse(await altPost.Content.ReadAsStringAsync(ct));
            var altId = altCreated.RootElement.GetProperty("Id").GetString();
            await PublishRealmVariant("logout", altId!, ct);

            var slug = $"pb-{Guid.NewGuid():N}";
            var createdResponse = await Client.PostAsJsonAsync("/api/app",
                new CreateAppDto(slug, "PageBuilder App", null, [], null), JsonOptions, ct);
            Assert.Equal(HttpStatusCode.OK, createdResponse.StatusCode);
            using var created = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync(ct));
            var appId = created.RootElement.GetProperty("Id").GetString();
            var basePath = $"/api/app/{appId}/pages";

            // Fresh app inherits, and can see the realm variants as options.
            using (var list = JsonDocument.Parse(await (await Client.GetAsync(basePath, ct)).Content.ReadAsStringAsync(ct)))
            {
                var slot = list.RootElement.GetProperty("Slots").EnumerateArray()
                    .Single(s => s.GetProperty("Slug").GetString() == "logout");
                Assert.True(slot.GetProperty("InheritActive").GetBoolean());
                Assert.Equal(2, slot.GetProperty("AvailableVariants").EnumerateArray().Count());
            }

            // App overrides to the Alt realm variant.
            var setActive = await Client.PutAsJsonAsync($"{basePath}/logout/active",
                new { Inherit = false, ActiveVariantId = altId }, ct);
            Assert.Equal(HttpStatusCode.OK, setActive.StatusCode);

            // A regular App settings replace must leave the page selection intact.
            var appUpdate = await Client.PutAsJsonAsync($"/api/app/{appId}",
                new UpdateAppDto("PageBuilder App", null, [], new ApplicationSettingsDto
                {
                    Branding = new ApplicationBrandingDto { ProductName = "Branded" },
                }), JsonOptions, ct);
            Assert.Equal(HttpStatusCode.OK, appUpdate.StatusCode);

            using (var list = JsonDocument.Parse(await (await Client.GetAsync(basePath, ct)).Content.ReadAsStringAsync(ct)))
            {
                var slot = list.RootElement.GetProperty("Slots").EnumerateArray()
                    .Single(s => s.GetProperty("Slug").GetString() == "logout");
                Assert.False(slot.GetProperty("InheritActive").GetBoolean());
                Assert.Equal(altId, slot.GetProperty("ActiveVariantId").GetString());
            }

            // The realm grid shows the Alt variant is used by this app.
            using (var realmSlot = JsonDocument.Parse(await (await Client.GetAsync(
                "/api/admin/customization/pages/logout", ct)).Content.ReadAsStringAsync(ct)))
            {
                var alt = realmSlot.RootElement.GetProperty("Variants").EnumerateArray()
                    .Single(v => v.GetProperty("Id").GetString() == altId);
                Assert.Contains("PageBuilder App", alt.GetProperty("UsedByApps").EnumerateArray().Select(x => x.GetString()));
            }

            // Back to inherit.
            var inherit = await Client.PutAsJsonAsync($"{basePath}/logout/active",
                new { Inherit = true, ActiveVariantId = (string?)null }, ct);
            Assert.Equal(HttpStatusCode.OK, inherit.StatusCode);
            using (var list = JsonDocument.Parse(await (await Client.GetAsync(basePath, ct)).Content.ReadAsStringAsync(ct)))
            {
                var slot = list.RootElement.GetProperty("Slots").EnumerateArray()
                    .Single(s => s.GetProperty("Slug").GetString() == "logout");
                Assert.True(slot.GetProperty("InheritActive").GetBoolean());
            }
        }
        finally { settings.Features.PageBuilder = false; }
    }

    [Fact]
    public async Task Application_activating_a_non_realm_variant_is_rejected()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var slug = $"pb-{Guid.NewGuid():N}";
            var createdResponse = await Client.PostAsJsonAsync("/api/app",
                new CreateAppDto(slug, "PB App 2", null, [], null), JsonOptions, ct);
            using var created = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync(ct));
            var appId = created.RootElement.GetProperty("Id").GetString();

            var resp = await Client.PutAsJsonAsync($"/api/app/{appId}/pages/login/active",
                new { Inherit = false, ActiveVariantId = "not-a-realm-variant" }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { settings.Features.PageBuilder = false; }
    }

    [Fact]
    public async Task AppInfo_resolves_app_selected_realm_variant_from_local_authorize_client_context()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            const string realmSchema = "{\"id\":\"page\",\"type\":\"page\",\"schemaVersion\":4,\"children\":[]}";
            const string altSchema = "{\"id\":\"page\",\"type\":\"page\",\"schemaVersion\":4,\"children\":[{\"id\":\"a\",\"type\":\"paragraph\",\"props\":{\"text\":\"Alt login\"}}]}";
            await SeedRealmActive("login", "Realm", realmSchema, ct);
            // A second realm variant the App will select.
            var altPost = await Client.PostAsJsonAsync("/api/admin/customization/pages/login/variants",
                new { Name = "Alt", Schema = altSchema }, ct);
            using var altCreated = JsonDocument.Parse(await altPost.Content.ReadAsStringAsync(ct));
            var altId = altCreated.RootElement.GetProperty("Id").GetString();
            await PublishRealmVariant("login", altId!, ct);

            var appId = Guid.NewGuid();
            var clientId = $"page-client-{Guid.NewGuid():N}";
            using (var scope = Factory.Services.CreateScope())
            {
                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
                session.Store(new ApplicationSettings
                {
                    Id = appId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    PageSlots = new Dictionary<string, AppPageSlot>
                    {
                        // App selects the Alt *realm* variant (no app-owned variants).
                        ["login"] = new AppPageSlot { InheritActive = false, ActiveVariantId = altId },
                    },
                });
                session.Store(new OAuthApplicationState { Id = Guid.NewGuid(), ClientId = clientId, AppIds = [appId] });
                await session.SaveChangesAsync(ct);
            }

            var continuation = Uri.EscapeDataString($"/connect/authorize?client_id={clientId}&scope=openid");
            Assert.Equal(altSchema, await AppInfoActiveSchema("login", ct, continuation));

            // An absolute URL is not accepted as presentation context → realm schema.
            var untrusted = Uri.EscapeDataString($"https://evil.example/connect/authorize?client_id={clientId}");
            Assert.Equal(realmSchema, await AppInfoActiveSchema("login", ct, untrusted));
        }
        finally { settings.Features.PageBuilder = false; }
    }
}
