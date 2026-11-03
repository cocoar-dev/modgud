using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// EG-02 — <c>prompt=login</c> forced the login challenge, but the return URL kept the
/// unchanged query, so every re-entry after a successful sign-in challenged again and a
/// relying party using it never got a code. A fresh sign-in after the prompt now
/// satisfies it; the session that existed before the prompt does not.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PromptLoginTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string RedirectUri = "https://prompt.rp.example/callback";

    [Fact]
    public async Task Prompt_login_completes_after_a_fresh_sign_in_and_not_without_one()
    {
        var clientId = await CreateClientAsync();
        var browser = await CreateAuthenticatedClientAsync("tu", DefaultPassword);
        await Task.Delay(TimeSpan.FromSeconds(1.2), Ct); // the existing sign-in is older than the prompt

        // 1. prompt=login: the signed-in user is sent to the login page.
        using var first = await browser.GetAsync(AuthorizeUri(clientId), Ct);
        var loginRedirect = AssertLoginChallenge(first);

        // 2. Following the return URL WITHOUT signing in again does not yield a code.
        using var stale = await browser.GetAsync(loginRedirect, Ct);
        AssertLoginChallenge(stale);

        // 3. After a fresh sign-in the very same return URL completes with a code.
        using var login = await browser.PostAsJsonAsync("/api/account/login",
            new { UserName = "tu", Password = DefaultPassword, ReturnUrl = loginRedirect }, Ct);
        login.EnsureSuccessStatusCode();
        using var resumed = await browser.GetAsync(loginRedirect, Ct);
        Assert.True((int)resumed.StatusCode is >= 300 and < 400, $"resume: {(int)resumed.StatusCode}");
        Assert.StartsWith(RedirectUri, resumed.Headers.Location!.ToString());
        Assert.Contains("code=", resumed.Headers.Location!.Query);
    }

    private static string AssertLoginChallenge(HttpResponseMessage response)
    {
        Assert.True((int)response.StatusCode is >= 300 and < 400, $"expected a redirect, got {(int)response.StatusCode}");
        var location = response.Headers.Location!;
        Assert.StartsWith("/login", location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString);
        var query = System.Web.HttpUtility.ParseQueryString(location.IsAbsoluteUri ? location.Query : new Uri("http://x" + location.OriginalString).Query);
        return query["redirect"] ?? throw new Xunit.Sdk.XunitException($"no redirect in {location}");
    }

    private static string AuthorizeUri(string clientId)
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "/connect/authorize?" + string.Join("&",
        [
            "response_type=code",
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"redirect_uri={Uri.EscapeDataString(RedirectUri)}",
            "scope=openid",
            "prompt=login",
            $"state={Guid.NewGuid():N}",
            $"code_challenge={challenge}",
            "code_challenge_method=S256",
        ]);
    }

    private async Task<string> CreateClientAsync()
    {
        var clientId = $"prompt-{Guid.NewGuid():N}"[..24];
        using var scope = Factory.Services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<OAuthAdminService>().CreateClientAsync(new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientSecret = $"secret-{Guid.NewGuid():N}",
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            DisplayName = clientId,
            RedirectUris = [RedirectUri],
            Scopes = ["openid"],
            AllowedGrantTypes = ["authorization_code"],
            RequireConsent = false,
        }, Ct);
        Assert.False(created.IsError, created.IsError ? created.FirstError.Description : "");
        return clientId;
    }
}
