using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Sessions;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ExternalAuth;

[Collection(IntegrationTestCollection.Name)]
public class FederatedLogoutTests : IntegrationTestBase
{
    public FederatedLogoutTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData(LoginProviderType.Oidc, true, true)]
    [InlineData(LoginProviderType.Oidc, false, false)]
    [InlineData(LoginProviderType.Saml, true, false)]
    [InlineData(LoginProviderType.Ldap, true, false)]
    public async Task Logout_ReturnsUpstreamUrlOnlyForEnabledOidc(
        LoginProviderType providerType,
        bool enabled,
        bool expectsUpstreamLogout)
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = await StoreProviderAsync(providerType, enabled, ct);
        using var client = await CreateFederatedCookieClientAsync(provider.Id, ct);

        var response = await client.PostAsJsonAsync(
            "/api/account/logout",
            new { EndIdpSession = true },
            ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LogoutResponse>(JsonOptions, ct);
        Assert.NotNull(body);
        Assert.Equal(
            expectsUpstreamLogout
                ? $"/api/account/external-logout/{provider.Id}"
                : null,
            body.ExternalLogoutUrl);
    }

    [Fact]
    public async Task Logout_OidcOptOut_EndsOnlyTheLocalSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = await StoreProviderAsync(LoginProviderType.Oidc, enabled: true, ct);
        using var client = await CreateFederatedCookieClientAsync(provider.Id, ct);

        var response = await client.PostAsJsonAsync(
            "/api/account/logout",
            new { EndIdpSession = false },
            ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LogoutResponse>(JsonOptions, ct);
        Assert.NotNull(body);
        Assert.Null(body.ExternalLogoutUrl);
    }

    [Fact]
    public async Task Logout_UnknownProvider_StillEndsTheLocalSession()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await CreateFederatedCookieClientAsync(Guid.NewGuid(), ct);

        var response = await client.PostAsJsonAsync(
            "/api/account/logout",
            new { EndIdpSession = true },
            ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LogoutResponse>(JsonOptions, ct);
        Assert.NotNull(body);
        Assert.Null(body.ExternalLogoutUrl);
    }

    [Fact]
    public async Task ExternalLogout_SamlProvider_DegradesToLocalLoggedOutPage()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = await StoreProviderAsync(LoginProviderType.Saml, enabled: true, ct);
        using var client = Factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Referrer = new Uri("http://localhost/profile");

        var response = await client.GetAsync(
            $"/api/account/external-logout/{provider.Id}",
            ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/logged-out", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ExternalLogout_UnknownProvider_DegradesToLocalLoggedOutPage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Referrer = new Uri("http://localhost/profile");

        var response = await client.GetAsync(
            $"/api/account/external-logout/{Guid.NewGuid()}",
            ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/logged-out", response.Headers.Location?.OriginalString);
    }

    private async Task<LoginProvider> StoreProviderAsync(
        LoginProviderType type,
        bool enabled,
        CancellationToken ct)
    {
        var provider = new LoginProvider
        {
            Id = Guid.NewGuid(),
            Type = type,
            Flavor = type switch
            {
                LoginProviderType.Oidc => LoginProviderFlavor.GenericOidc,
                LoginProviderType.Saml => LoginProviderFlavor.GenericSaml,
                _ => type.ToString().ToLowerInvariant(),
            },
            Slug = $"logout-{Guid.NewGuid():N}"[..24],
            DisplayName = $"{type} logout test",
            Enabled = enabled,
            ClientId = type == LoginProviderType.Oidc ? "logout-client" : string.Empty,
        };

        await using var session = GetTenantedDocumentSession();
        session.Store(provider);
        await session.SaveChangesAsync(ct);
        return provider;
    }

    private async Task<HttpClient> CreateFederatedCookieClientAsync(
        Guid loginProviderId,
        CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();

        var user = await userManager.FindByIdAsync(DefaultUser!.Id.ToString())
            ?? throw new InvalidOperationException("Default test user not found.");
        var principal = await signInManager.CreateUserPrincipalAsync(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(
            "modgud.external.loginProviderId",
            loginProviderId.ToString()));

        using (TenantContext.Enter(TenantConstants.SystemTenantId))
        {
            var createdSession = await scope.ServiceProvider
                .GetRequiredService<ISessionService>()
                .CreateSessionAsync(
                    user.Id,
                    ipAddress: null,
                    userAgent: "FederatedLogoutTests",
                    ct);
            Assert.False(
                createdSession.IsError,
                createdSession.IsError ? createdSession.FirstError.Description : null);
            identity.AddClaim(new Claim(
                SessionClaimTypes.BrowserSessionId,
                createdSession.Value.Id.ToString()));
        }

        var cookieOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var ticket = new AuthenticationTicket(
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
            },
            IdentityConstants.ApplicationScheme);

        string cookieValue;
        using (TenantContext.Enter(TenantConstants.SystemTenantId))
            cookieValue = cookieOptions.TicketDataFormat.Protect(ticket);

        var handler = new CookieContainerHandler();
        handler.Seed(new Uri("http://localhost"), cookieOptions.Cookie.Name!, cookieValue);
        return Factory.CreateDefaultClient(handler);
    }

    private sealed record LogoutResponse(string Message, string? ExternalLogoutUrl);
}
