using System.Net;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// EG-03 — the token endpoint checked the app-scope rule, the dynamic-client opt-in and
/// Enabled only against request.GetScopes(), which a refresh (or code / device exchange)
/// usually leaves empty, and then re-issued the ORIGINAL granted scopes unchecked. A
/// scope disabled — or a client unlinked from the scope's App — after the grant kept
/// being minted on every refresh. The granted scopes are now revalidated.
/// Placed with these tests for their real auth-code helpers.
/// </summary>
public partial class BackChannelLogoutTests
{
    [Fact]
    public async Task Refresh_refuses_a_scope_disabled_after_the_grant()
    {
        var ct = TestContext.Current.CancellationToken;
        var scopeName = $"eg03-reports-{Guid.NewGuid():N}"[..24];
        var scope = await CreateScopeAsync(scopeName, appId: null);
        var rp = await CreateRelyingPartyAsync("eg03dis", AccessTokenType.Jwt, logoutUri: null,
            scopes: ["openid", "offline_access", scopeName]);
        var refreshToken = await SignInWithScopeAsync("eg03dis", rp, $"openid offline_access {scopeName}");

        using (var svc = Factory.Services.CreateScope())
        {
            var updated = await svc.ServiceProvider.GetRequiredService<OAuthAdminService>()
                .UpdateScopeAsync(scope.Id, new UpdateOAuthScopeDto { Enabled = false }, ct);
            Assert.False(updated.IsError);
        }

        await AssertRefreshRefusedAsync(rp, refreshToken);
    }

    [Fact]
    public async Task Refresh_refuses_an_app_scope_once_the_client_left_the_app()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await CreateScopeAppAsync($"eg03-app-{Guid.NewGuid():N}"[..20]);
        var scopeName = $"eg03-app-scope-{Guid.NewGuid():N}"[..26];
        await CreateScopeAsync(scopeName, appId: app.Id);
        var rp = await CreateRelyingPartyAsync("eg03app", AccessTokenType.Jwt, logoutUri: null,
            appIds: [app.Id], scopes: ["openid", "offline_access", scopeName]);
        var refreshToken = await SignInWithScopeAsync("eg03app", rp, $"openid offline_access {scopeName}");

        using (var svc = Factory.Services.CreateScope())
        {
            var updated = await svc.ServiceProvider.GetRequiredService<OAuthAdminService>()
                .UpdateClientAsync(rp.Id, new UpdateOAuthClientDto { AppIds = [] }, ct);
            Assert.False(updated.IsError);
        }

        await AssertRefreshRefusedAsync(rp, refreshToken);
    }

    private async Task<string> SignInWithScopeAsync(string tag, RelyingParty rp, string scope)
    {
        var (_, userName) = await CreateUserAsync(tag);
        await Factory.WaitForProjectionsAsync();
        var cookieClient = await CreateAuthenticatedClientAsync(userName, Password);
        using var tokens = await DriveAuthCodeFlowAsync(cookieClient, rp, scope);
        return tokens.RootElement.GetProperty("refresh_token").GetString()!;
    }

    private async Task AssertRefreshRefusedAsync(RelyingParty rp, string refreshToken)
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await Factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = rp.ClientId,
            ["client_secret"] = rp.Secret,
        }), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"refresh: {(int)response.StatusCode} {body}");
        Assert.Equal("invalid_scope", JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
    }

    private async Task<OAuthScopeDto> CreateScopeAsync(string name, Guid? appId)
    {
        using var scope = Factory.Services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<OAuthAdminService>().CreateScopeAsync(new CreateOAuthScopeDto
        {
            Name = name,
            DisplayName = name,
            AppId = appId?.ToString(),
        }, TestContext.Current.CancellationToken);
        Assert.False(created.IsError, created.IsError ? created.FirstError.Description : "");
        return created.Value;
    }

    private async Task<App> CreateScopeAppAsync(string slug)
    {
        await using var session = GetTenantedDocumentSession();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id, Slug: slug, DisplayName: slug, Description: null, Permissions: [], IsSystem: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (await session.LoadAsync<App>(id, TestContext.Current.CancellationToken))!;
    }
}
