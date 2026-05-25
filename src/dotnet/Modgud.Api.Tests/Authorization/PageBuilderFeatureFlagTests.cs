using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modgud.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

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
        Assert.Equal(0, pages.EnumerateObject().Count());
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
        }
        finally
        {
            settings.Features.PageBuilder = false;
        }
    }
}
