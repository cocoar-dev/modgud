using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Applications;
using Modgud.Authentication.Applications;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// The native-grant refresh-token lifetime was stored, validated and shown, but never applied:
/// a native refresh token gets the client-session idle lifetime (review EG-08). The setting is
/// gone from the realm and App settings; a caller or manifest that still sends it is not
/// refused, the value is simply ignored.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class NativeRefreshLifetimeRemovedTests : IntegrationTestBase
{
    public NativeRefreshLifetimeRemovedTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Realm_settings_neither_show_nor_validate_a_native_refresh_lifetime()
    {
        var ct = TestContext.Current.CancellationToken;

        // An older client still sends the field, with a value the old validation refused.
        var patch = await Client.PatchAsJsonAsync("/api/admin/realm-settings",
            new { NativeGrants = new { RefreshTokenLifetimeDays = 9_999 } }, ct);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var settings = JsonDocument.Parse(await Client.GetStringAsync("/api/admin/realm-settings", ct));
        var native = settings.RootElement.GetProperty("NativeGrants");
        Assert.True(native.TryGetProperty("AccessTokenLifetimeMinutes", out _));
        Assert.False(native.TryGetProperty("RefreshTokenLifetimeDays", out _));
    }

    [Fact]
    public async Task App_settings_show_no_native_refresh_lifetime()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        var appId = await CreateAppAsync("native-rtl");
        var service = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();

        var saved = await service.PatchAsync(appId, new ApplicationSettingsDto
        {
            NativeGrants = new ApplicationNativeGrantsDto { Enabled = true, AccessTokenLifetimeMinutes = 10 },
        }, ct);
        Assert.False(saved.IsError, saved.IsError ? saved.FirstError.Description : "");

        var loaded = await service.GetAsync(appId, ct);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(loaded.Value));
        var native = json.RootElement.GetProperty("NativeGrants");
        Assert.Equal(10, native.GetProperty("AccessTokenLifetimeMinutes").GetInt32());
        Assert.False(native.TryGetProperty("RefreshTokenLifetimeDays", out _));
    }

    private async Task<Guid> CreateAppAsync(string slug)
    {
        await using var session = GetTenantedDocumentSession();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id, Slug: slug, DisplayName: slug, Description: null, Permissions: [], IsSystem: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }
}
