using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Applications;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.Applications;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// WebAuthn related origin requests for an App's passkeys: the Modgud login page is not
/// served under the App's RP ID, the App publishes /.well-known/webauthn listing it, and
/// a browser that supports related origins may then use the App's passkeys there. The
/// server only picks the App's RP ID when the App opted in AND the browser says it can.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PasskeyRelatedOriginsTests : IntegrationTestBase
{
    public PasskeyRelatedOriginsTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string AppRpId = "app-dev.example-app.test";

    [Theory]
    [InlineData(true, true, AppRpId, true)]       // opted in + capable browser → the App's RP ID
    [InlineData(true, false, null, false)]        // opted in, browser cannot → realm RP ID
    [InlineData(false, true, null, false)]        // not opted in → realm RP ID
    public async Task Login_options_use_the_apps_rp_id_only_with_related_origins_and_a_capable_browser(
        bool appOptedIn, bool browserCapable, string? expectedRpId, bool expectRelatedCeremony)
    {
        var ct = TestContext.Current.CancellationToken;
        var clientId = await SeedAppClientAsync(new ApplicationSignInOverrides
        {
            PasskeyRpId = AppRpId,
            PasskeyRelatedOrigins = appOptedIn,
        });

        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());
        var response = await browser.PostAsJsonAsync("/api/account/passkey/login-options", new
        {
            ReturnUrl = $"/connect/authorize?client_id={clientId}&response_type=code",
            RelatedOrigins = browserCapable,
        }, ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rpId = body.GetProperty("rpId").GetString();
        if (expectedRpId is not null) Assert.Equal(expectedRpId, rpId);
        else Assert.NotEqual(AppRpId, rpId);

        await using var session = GetTenantedDocumentSession();
        var ceremony = await session.Query<PasskeyCeremony>().OrderByDescending(c => c.CreatedAt).FirstAsync(ct);
        Assert.Equal(rpId, ceremony.RpId);
        Assert.Equal(expectRelatedCeremony, ceremony.RelatedOrigin);
    }

    [Theory]
    [InlineData(true, true)]    // opted in, page not under the RP ID → only via related origins
    [InlineData(false, false)]  // not opted in → the app's passkeys never reach this page
    public async Task App_info_says_when_the_apps_passkeys_need_related_origins_on_this_page(
        bool appOptedIn, bool expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var clientId = await SeedAppClientAsync(new ApplicationSignInOverrides
        {
            PasskeyRpId = AppRpId,
            PasskeyRelatedOrigins = appOptedIn,
        });

        var returnUrl = Uri.EscapeDataString($"/connect/authorize?client_id={clientId}&response_type=code");
        var body = await Factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/app-info?returnUrl={returnUrl}", ct);

        Assert.Equal(expected, body.GetProperty("SignIn").GetProperty("PasskeyNeedsRelatedOrigins").GetBoolean());
        Assert.True(body.GetProperty("SignIn").GetProperty("Passkey").GetBoolean());
    }

    [Fact]
    public async Task Related_origins_without_a_passkey_rp_id_are_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        var app = await CreateAppAsync("ro-norp");
        var service = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();

        var result = await service.PatchAsync(app.Id, new ApplicationSettingsDto
        {
            SignIn = new ApplicationSignInDto { PasskeyRelatedOrigins = true },
        }, ct);

        Assert.True(result.IsError);
        Assert.Equal("SignIn.RelatedOriginsWithoutRpId", result.FirstError.Code);
    }

    private async Task<string> SeedAppClientAsync(ApplicationSignInOverrides signIn)
    {
        var ct = TestContext.Current.CancellationToken;
        var clientId = $"ro-{Guid.NewGuid():N}"[..16];
        var app = await CreateAppAsync(clientId);

        await using (var session = GetTenantedDocumentSession())
        {
            session.Store(new ApplicationSettings { Id = app.Id, CreatedAt = DateTimeOffset.UtcNow, SignIn = signIn });
            await session.SaveChangesAsync(ct);
        }

        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var created = await oauthAdmin.CreateClientAsync(new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientType = OAuthClientTypes.Public,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = ["https://app.example/callback"],
            PostLogoutRedirectUris = [],
            Scopes = ["openid"],
            AllowedGrantTypes = ["authorization_code"],
            RequireConsent = false,
            AppIds = [new ShortGuid(app.Id).ToString()],
        }, ct);
        Assert.False(created.IsError, created.IsError ? created.FirstError.Description : "");
        return clientId;
    }

    private async Task<App> CreateAppAsync(string slug)
    {
        await using var session = GetTenantedDocumentSession();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id, Slug: slug, DisplayName: slug, Description: null, Permissions: [], IsSystem: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (await session.LoadAsync<App>(id, TestContext.Current.CancellationToken))!;
    }
}
