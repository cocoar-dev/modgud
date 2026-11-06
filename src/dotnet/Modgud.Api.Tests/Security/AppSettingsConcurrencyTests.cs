using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// F7-05 — an app's settings override is written as a whole with the app. A second admin
/// saving the app on a copy loaded before someone else's save must get 409
/// <c>App.SettingsChanged</c>, not silently put the old values back.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AppSettingsConcurrencyTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task An_app_update_based_on_outdated_settings_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var appId = await CreateAppAsync("f705-settings", ct);
        var appShort = new ShortGuid(appId).ToString();

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(appShort, "First", expected: null, ct)).StatusCode);
        var seen = await SettingsVersionAsync(appShort, ct);
        Assert.False(string.IsNullOrEmpty(seen));

        // Another admin saves meanwhile.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(appShort, "Changed by someone else", seen, ct)).StatusCode);

        // The stale modal saves on top of the version it loaded.
        var stale = await PutAsync(appShort, "Stale modal", seen, ct);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("App.SettingsChanged", await stale.Content.ReadAsStringAsync(ct));
        using var doc = JsonDocument.Parse(await Client.GetStringAsync($"/api/app/{appShort}", ct));
        Assert.Equal("Changed by someone else",
            doc.RootElement.GetProperty("Settings").GetProperty("Branding").GetProperty("ProductName").GetString());
    }

    private Task<HttpResponseMessage> PutAsync(string appShort, string productName, string? expected, CancellationToken ct) =>
        Client.PutAsJsonAsync($"/api/app/{appShort}", new
        {
            DisplayName = "f705-settings",
            Description = (string?)null,
            Permissions = Array.Empty<object>(),
            Settings = new { Branding = new { ProductName = productName } },
            ExpectedSettingsVersion = expected,
        }, ct);

    private async Task<string?> SettingsVersionAsync(string appShort, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Client.GetStringAsync($"/api/app/{appShort}", ct));
        return doc.RootElement.GetProperty("Settings").TryGetProperty("Version", out var v) ? v.GetString() : null;
    }

    private async Task<Guid> CreateAppAsync(string slug, CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id, Slug: slug, DisplayName: slug, Description: null, Permissions: [], IsSystem: false));
        await session.SaveChangesAsync(ct);
        return id;
    }
}
