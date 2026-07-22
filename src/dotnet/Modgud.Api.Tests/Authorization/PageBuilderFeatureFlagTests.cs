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
/// Pins the <c>AppSettings.Features.PageBuilder</c> gate and the ADR-0001
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
            var schema = "{\"type\":\"page\",\"children\":[{\"type\":\"heading\",\"level\":1}]}";
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
            // until explicitly activated (ADR-0001: existence ≠ active).
            var post = await Client.PostAsJsonAsync("/api/admin/customization/pages/logout/variants",
                new { Name = "Draft", Schema = "{\"type\":\"page\",\"children\":[]}" }, ct);
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);

            Assert.Null(await AppInfoActiveSchema("logout", ct));
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
            var schema = "{\"type\":\"page\",\"children\":[]}";
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

    // ── application inheritance / override / deactivate ──

    [Fact]
    public async Task Application_inherits_then_overrides_then_deactivates_and_survives_settings_save()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            const string realmSchema = "{\"type\":\"page\",\"schemaVersion\":2,\"children\":[]}";
            const string appSchema = "{\"type\":\"page\",\"schemaVersion\":2,\"children\":[{\"id\":\"t\",\"type\":\"heading\",\"props\":{\"text\":\"App\"}}]}";

            await SeedRealmActive("logout", "Realm", realmSchema, ct);

            var slug = $"pb-{Guid.NewGuid():N}";
            var createdResponse = await Client.PostAsJsonAsync("/api/app",
                new CreateAppDto(slug, "PageBuilder App", null, [], null), JsonOptions, ct);
            Assert.Equal(HttpStatusCode.OK, createdResponse.StatusCode);
            using var created = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync(ct));
            var appId = created.RootElement.GetProperty("Id").GetString();
            var basePath = $"/api/app/{appId}/pages";

            // Fresh app inherits: its slot list is empty.
            using (var list = JsonDocument.Parse(await (await Client.GetAsync(basePath, ct)).Content.ReadAsStringAsync(ct)))
            {
                Assert.Empty(list.RootElement.GetProperty("Slots").EnumerateArray());
            }

            // App authors its own variant and activates it (non-inheriting).
            var post = await Client.PostAsJsonAsync($"{basePath}/logout/variants",
                new { Name = "App", Schema = appSchema }, ct);
            using var appVariant = JsonDocument.Parse(await post.Content.ReadAsStringAsync(ct));
            var appVariantId = appVariant.RootElement.GetProperty("Id").GetString();
            var setActive = await Client.PutAsJsonAsync($"{basePath}/logout/active",
                new { Inherit = false, ActiveVariantId = appVariantId }, ct);
            Assert.Equal(HttpStatusCode.OK, setActive.StatusCode);

            // A regular App settings replace must leave the page tree intact.
            var appUpdate = await Client.PutAsJsonAsync($"/api/app/{appId}",
                new UpdateAppDto("PageBuilder App", null, [], new ApplicationSettingsDto
                {
                    Branding = new ApplicationBrandingDto { ProductName = "Branded" },
                }), JsonOptions, ct);
            Assert.Equal(HttpStatusCode.OK, appUpdate.StatusCode);

            using (var list = JsonDocument.Parse(await (await Client.GetAsync(basePath, ct)).Content.ReadAsStringAsync(ct)))
            {
                var slot = list.RootElement.GetProperty("Slots").EnumerateArray().Single();
                Assert.False(slot.GetProperty("InheritActive").GetBoolean());
                Assert.Equal(appVariantId, slot.GetProperty("ActiveVariantId").GetString());
                Assert.Single(slot.GetProperty("Variants").EnumerateArray());
            }

            // Back to inherit — app variant is retained, realm selection stands.
            var inherit = await Client.PutAsJsonAsync($"{basePath}/logout/active",
                new { Inherit = true, ActiveVariantId = (string?)null }, ct);
            Assert.Equal(HttpStatusCode.OK, inherit.StatusCode);
            using (var list = JsonDocument.Parse(await (await Client.GetAsync(basePath, ct)).Content.ReadAsStringAsync(ct)))
            {
                var slot = list.RootElement.GetProperty("Slots").EnumerateArray().Single();
                Assert.True(slot.GetProperty("InheritActive").GetBoolean());
                Assert.Single(slot.GetProperty("Variants").EnumerateArray()); // retained
            }
        }
        finally { settings.Features.PageBuilder = false; }
    }

    [Fact]
    public async Task AppInfo_resolves_app_override_from_local_authorize_client_context()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            const string realmSchema = "{\"type\":\"page\",\"schemaVersion\":2,\"children\":[]}";
            const string appSchema = "{\"type\":\"page\",\"schemaVersion\":2,\"children\":[{\"id\":\"a\",\"type\":\"paragraph\",\"props\":{\"text\":\"App login\"}}]}";
            await SeedRealmActive("login", "Realm", realmSchema, ct);

            var appId = Guid.NewGuid();
            var clientId = $"page-client-{Guid.NewGuid():N}";
            var appVariantId = Guid.NewGuid().ToString("N");
            using (var scope = Factory.Services.CreateScope())
            {
                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
                session.Store(new ApplicationSettings
                {
                    Id = appId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    PageSlots = new Dictionary<string, AppPageSlot>
                    {
                        ["login"] = new AppPageSlot
                        {
                            InheritActive = false,
                            Variants = [new PageVariant { Id = appVariantId, Name = "App", Schema = appSchema }],
                            ActiveVariantId = appVariantId,
                        },
                    },
                });
                session.Store(new OAuthApplicationState { Id = Guid.NewGuid(), ClientId = clientId, AppIds = [appId] });
                await session.SaveChangesAsync(ct);
            }

            var continuation = Uri.EscapeDataString($"/connect/authorize?client_id={clientId}&scope=openid");
            Assert.Equal(appSchema, await AppInfoActiveSchema("login", ct, continuation));

            // An absolute URL is not accepted as presentation context → realm schema.
            var untrusted = Uri.EscapeDataString($"https://evil.example/connect/authorize?client_id={clientId}");
            Assert.Equal(realmSchema, await AppInfoActiveSchema("login", ct, untrusted));
        }
        finally { settings.Features.PageBuilder = false; }
    }
}
