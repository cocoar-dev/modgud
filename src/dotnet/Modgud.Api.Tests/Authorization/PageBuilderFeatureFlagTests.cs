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

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Pins the <c>AppSettings.Features.PageBuilder</c> gate on the
/// customization-pages surface. While the flag is off (default) the
/// endpoints look invisible (404), and the RealmSettings DTO emits an
/// empty Pages dictionary so the SPA never sees stored schemas.
///
/// <para>Mutates the AppSettings singleton in-process. The fixture
/// uses <c>[Collection]</c> so tests run sequentially within the
/// collection — each test restores the flag in its finally block.</para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PageBuilderFeatureFlagTests : IntegrationTestBase
{
    public PageBuilderFeatureFlagTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task GetPage_returns_404_when_feature_off()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = false;

        var resp = await Client.GetAsync("/api/admin/customization/pages/login",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PutPage_returns_404_when_feature_off()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = false;

        var resp = await Client.PutAsJsonAsync("/api/admin/customization/pages/login",
            new { Schema = "{\"type\":\"page\",\"children\":[]}" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetPage_works_when_feature_on()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        try
        {
            var resp = await Client.GetAsync("/api/admin/customization/pages/login",
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("login", doc.RootElement.GetProperty("Slug").GetString());
        }
        finally
        {
            settings.Features.PageBuilder = false;
        }
    }

    [Fact]
    public async Task PutPage_persists_when_feature_on()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        try
        {
            var schema = "{\"type\":\"page\",\"children\":[{\"type\":\"heading\",\"level\":1}]}";
            var put = await Client.PutAsJsonAsync("/api/admin/customization/pages/login",
                new { Schema = schema },
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            var get = await Client.GetAsync("/api/admin/customization/pages/login",
                TestContext.Current.CancellationToken);
            var body = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(schema, doc.RootElement.GetProperty("Schema").GetString());
        }
        finally
        {
            settings.Features.PageBuilder = false;
        }
    }

    [Fact]
    public async Task RealmSettings_DTO_emits_empty_Pages_when_feature_off()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();

        // Seed a schema while the flag is on, then turn it off and read.
        settings.Features.PageBuilder = true;
        var schema = "{\"type\":\"page\",\"children\":[]}";
        var put = await Client.PutAsJsonAsync("/api/admin/customization/pages/login",
            new { Schema = schema },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        settings.Features.PageBuilder = false;
        var resp = await Client.GetAsync("/api/admin/realm-settings",
            TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("Pages", out var pages));
        Assert.Equal(JsonValueKind.Object, pages.ValueKind);
        Assert.Empty(pages.EnumerateObject());

        var anonymous = await Client.GetAsync("/api/app-info", TestContext.Current.CancellationToken);
        using var appInfo = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(appInfo.RootElement.GetProperty("Pages").EnumerateObject());
    }

    [Fact]
    public async Task RealmSettings_DTO_includes_Pages_when_feature_on()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        try
        {
            var schema = "{\"type\":\"page\",\"children\":[]}";
            var put = await Client.PutAsJsonAsync("/api/admin/customization/pages/login",
                new { Schema = schema },
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            var resp = await Client.GetAsync("/api/admin/realm-settings",
                TestContext.Current.CancellationToken);
            var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(body);

            Assert.True(doc.RootElement.TryGetProperty("Pages", out var pages));
            Assert.Equal(JsonValueKind.Object, pages.ValueKind);
            Assert.True(pages.TryGetProperty("login", out _),
                "Pages dictionary must surface stored slug when flag is on");

            var anonymous = await Client.GetAsync("/api/app-info", TestContext.Current.CancellationToken);
            using var appInfo = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(schema, appInfo.RootElement.GetProperty("Pages").GetProperty("login").GetString());
        }
        finally
        {
            settings.Features.PageBuilder = false;
        }
    }

    [Fact]
    public async Task Application_page_override_inherits_overrides_survives_settings_save_and_can_reset()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            const string realmSchema = "{\"type\":\"page\",\"schemaVersion\":2,\"children\":[]}";
            const string appSchema = "{\"type\":\"page\",\"schemaVersion\":2,\"children\":[{\"id\":\"title\",\"type\":\"heading\",\"props\":{\"text\":\"App\"}}]}";

            var realmPut = await Client.PutAsJsonAsync("/api/admin/customization/pages/logout",
                new { Schema = realmSchema }, ct);
            Assert.Equal(HttpStatusCode.OK, realmPut.StatusCode);

            var slug = $"pb-{Guid.NewGuid():N}";
            var createdResponse = await Client.PostAsJsonAsync("/api/app",
                new CreateAppDto(slug, "PageBuilder App", null, [], null), JsonOptions, ct);
            Assert.Equal(HttpStatusCode.OK, createdResponse.StatusCode);
            using var created = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync(ct));
            var appId = created.RootElement.GetProperty("Id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(appId));
            var endpoint = $"/api/app/{appId}/pages/logout";

            using (var inherited = JsonDocument.Parse(await (await Client.GetAsync(endpoint, ct)).Content.ReadAsStringAsync(ct)))
            {
                Assert.True(inherited.RootElement.GetProperty("InheritsRealm").GetBoolean());
                Assert.False(inherited.RootElement.TryGetProperty("Schema", out _));
                Assert.Equal(realmSchema, inherited.RootElement.GetProperty("EffectiveSchema").GetString());
            }

            var appPut = await Client.PutAsJsonAsync(endpoint, new { Schema = appSchema }, ct);
            Assert.Equal(HttpStatusCode.OK, appPut.StatusCode);

            // A regular App settings replace must leave the separately-managed page tree intact.
            var appUpdate = await Client.PutAsJsonAsync($"/api/app/{appId}",
                new UpdateAppDto("PageBuilder App", null, [], new ApplicationSettingsDto
                {
                    Branding = new ApplicationBrandingDto { ProductName = "Branded" },
                }), JsonOptions, ct);
            Assert.Equal(HttpStatusCode.OK, appUpdate.StatusCode);

            using (var overridden = JsonDocument.Parse(await (await Client.GetAsync(endpoint, ct)).Content.ReadAsStringAsync(ct)))
            {
                Assert.False(overridden.RootElement.GetProperty("InheritsRealm").GetBoolean());
                Assert.Equal(appSchema, overridden.RootElement.GetProperty("Schema").GetString());
                Assert.Equal(appSchema, overridden.RootElement.GetProperty("EffectiveSchema").GetString());
            }

            var delete = await Client.DeleteAsync(endpoint, ct);
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

            using var reset = JsonDocument.Parse(await (await Client.GetAsync(endpoint, ct)).Content.ReadAsStringAsync(ct));
            Assert.True(reset.RootElement.GetProperty("InheritsRealm").GetBoolean());
            Assert.Equal(realmSchema, reset.RootElement.GetProperty("EffectiveSchema").GetString());
        }
        finally
        {
            settings.Features.PageBuilder = false;
        }
    }

    [Fact]
    public async Task AppInfo_resolves_page_override_from_local_authorize_client_context()
    {
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PageBuilder = true;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            const string realmSchema = "{\"type\":\"page\",\"schemaVersion\":2,\"children\":[]}";
            const string appSchema = "{\"type\":\"page\",\"schemaVersion\":2,\"children\":[{\"id\":\"app\",\"type\":\"paragraph\",\"props\":{\"text\":\"App login\"}}]}";
            var realmPut = await Client.PutAsJsonAsync("/api/admin/customization/pages/login",
                new { Schema = realmSchema }, ct);
            Assert.Equal(HttpStatusCode.OK, realmPut.StatusCode);

            var appId = Guid.NewGuid();
            var clientId = $"page-client-{Guid.NewGuid():N}";
            using (var scope = Factory.Services.CreateScope())
            {
                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
                session.Store(new ApplicationSettings
                {
                    Id = appId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Pages = new Dictionary<string, string> { ["login"] = appSchema },
                });
                session.Store(new OAuthApplicationState
                {
                    Id = Guid.NewGuid(),
                    ClientId = clientId,
                    AppIds = [appId],
                });
                await session.SaveChangesAsync(ct);
            }

            var continuation = Uri.EscapeDataString($"/connect/authorize?client_id={clientId}&scope=openid");
            var appInfoResponse = await Client.GetAsync($"/api/app-info?returnUrl={continuation}", ct);
            Assert.Equal(HttpStatusCode.OK, appInfoResponse.StatusCode);
            using var appInfo = JsonDocument.Parse(await appInfoResponse.Content.ReadAsStringAsync(ct));
            Assert.Equal(appSchema, appInfo.RootElement.GetProperty("Pages").GetProperty("login").GetString());

            // An absolute URL is not accepted as presentation context.
            var untrusted = Uri.EscapeDataString($"https://evil.example/connect/authorize?client_id={clientId}");
            using var realmInfo = JsonDocument.Parse(await (await Client.GetAsync($"/api/app-info?returnUrl={untrusted}", ct))
                .Content.ReadAsStringAsync(ct));
            Assert.Equal(realmSchema, realmInfo.RootElement.GetProperty("Pages").GetProperty("login").GetString());
        }
        finally
        {
            settings.Features.PageBuilder = false;
        }
    }
}
