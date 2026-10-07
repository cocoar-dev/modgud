using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authentication.Applications;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.Realms;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// The sign-in page presents the App the sign-in is for, by the same rule that picks its
/// sign-in policy (ADR 0025): the client's App, else the App of the requested resource.
/// A dynamically registered client — an MCP connector — is bound to no App, so before
/// this the page showed the realm's face while signing in under the resource App's
/// policy (no password field, the App's passkeys).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class LoginPageFollowsResourceTests : IntegrationTestBase
{
    public LoginPageFollowsResourceTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task A_client_without_an_app_presents_the_app_of_its_resource()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, api) = await SeedAppWithApiAsync("Resource App", ct);
        var client = await SeedClientAsync(appId: null, ct);

        var branding = await BrandingAsync($"/connect/authorize?client_id={client}&response_type=code&resource={Uri.EscapeDataString(api)}", ct);

        Assert.Equal("Resource App", Name(branding));
    }

    [Fact]
    public async Task Without_a_resource_the_page_stays_the_realms()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAppWithApiAsync("Unrequested App", ct);
        var client = await SeedClientAsync(appId: null, ct);

        var branding = await BrandingAsync($"/connect/authorize?client_id={client}&response_type=code", ct);

        Assert.NotEqual("Unrequested App", Name(branding));
    }

    [Fact]
    public async Task Resources_of_two_apps_present_the_realm()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, apiA) = await SeedAppWithApiAsync("App A", ct);
        var (_, apiB) = await SeedAppWithApiAsync("App B", ct);
        var client = await SeedClientAsync(appId: null, ct);

        var branding = await BrandingAsync(
            $"/connect/authorize?client_id={client}&response_type=code&resource={Uri.EscapeDataString(apiA)}&resource={Uri.EscapeDataString(apiB)}", ct);

        var name = Name(branding);
        Assert.NotEqual("App A", name);
        Assert.NotEqual("App B", name);
    }

    [Fact]
    public async Task A_client_bound_to_an_app_keeps_its_own_app()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ownApp, _) = await SeedAppWithApiAsync("Client App", ct);
        var (_, foreignApi) = await SeedAppWithApiAsync("Other App", ct);
        var client = await SeedClientAsync(ownApp, ct);

        var branding = await BrandingAsync(
            $"/connect/authorize?client_id={client}&response_type=code&resource={Uri.EscapeDataString(foreignApi)}", ct);

        Assert.Equal("Client App", Name(branding));
    }

    [Fact]
    public async Task The_resolver_names_the_resource_app_for_mails_and_registrations()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, api) = await SeedAppWithApiAsync("Mail App", ct);
        var client = await SeedClientAsync(appId: null, ct);

        using var scope = Factory.Services.CreateScope();
        var http = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
        var resolver = scope.ServiceProvider.GetRequiredService<IApplicationSettingsResolver>();

        Assert.Equal(app, await resolver.ResolveApplicationIdForReturnUrlAsync(
            http, $"/connect/authorize?client_id={client}&resource={Uri.EscapeDataString(api)}", ct));
        // Only a local authorize continuation is read.
        Assert.Null(await resolver.ResolveApplicationIdForReturnUrlAsync(
            http, $"https://evil.test/connect/authorize?client_id={client}&resource={Uri.EscapeDataString(api)}", ct));
        Assert.Null(await resolver.ResolveApplicationIdForReturnUrlAsync(
            http, $"/profile?resource={Uri.EscapeDataString(api)}", ct));
    }

    private static string? Name(JsonElement branding) =>
        branding.TryGetProperty("ProductName", out var name) ? name.GetString() : null;

    private async Task<JsonElement> BrandingAsync(string returnUrl, CancellationToken ct)
    {
        var body = await Factory.CreateClient().GetFromJsonAsync<JsonElement>(
            $"/api/app-info?returnUrl={Uri.EscapeDataString(returnUrl)}", ct);
        return body.GetProperty("Branding");
    }

    private async Task<(Guid AppId, string ApiName)> SeedAppWithApiAsync(string productName, CancellationToken ct)
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var appId = Guid.NewGuid();
        await using (var session = GetTenantedDocumentSession())
        {
            session.Events.StartStream<App>(appId, new AppCreatedEvent(
                Id: appId, Slug: $"lp-{tag}", DisplayName: productName, Description: null, Permissions: [], IsSystem: false));
            session.Store(new ApplicationSettings
            {
                Id = appId,
                CreatedAt = DateTimeOffset.UtcNow,
                Branding = new BrandingSettings { ProductName = productName },
            });
            await session.SaveChangesAsync(ct);
        }

        var apiName = $"https://lp-{tag}.test/mcp";
        using var scope = NewSystemScope();
        var created = await scope.ServiceProvider.GetRequiredService<OAuthAdminService>().CreateApiAsync(new CreateOAuthApiDto
        {
            Name = apiName, DisplayName = apiName, AppId = new ShortGuid(appId).ToString(),
        }, ct);
        Assert.False(created.IsError, created.IsError ? created.FirstError.Description : "");
        return (appId, apiName);
    }

    private async Task<string> SeedClientAsync(Guid? appId, CancellationToken ct)
    {
        var clientId = $"lp-{Guid.NewGuid():N}"[..16];
        using var scope = NewSystemScope();
        var created = await scope.ServiceProvider.GetRequiredService<OAuthAdminService>().CreateClientAsync(new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientType = OAuthClientTypes.Public,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = ["https://client.example/callback"],
            PostLogoutRedirectUris = [],
            Scopes = ["openid"],
            AllowedGrantTypes = ["authorization_code"],
            RequireConsent = false,
            AppIds = appId is { } id ? [new ShortGuid(id).ToString()] : [],
        }, ct);
        Assert.False(created.IsError, created.IsError ? created.FirstError.Description : "");
        return clientId;
    }

    private IServiceScope NewSystemScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        return scope;
    }
}
