using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Infrastructure.Email;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Tests for Magic Link passwordless login.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public partial class MagicLinkTests : IntegrationTestBase
{
    public MagicLinkTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task MagicLinkRequest_ReturnsOk_EvenForNonExistentEmail()
    {
        var anonClient = Factory.CreateClient();
        var response = await anonClient.PostAsJsonAsync("/api/account/magic-link/request",
            new { Email = "nonexistent@test.com" }, TestContext.Current.CancellationToken);

        // Always 200 — no user enumeration
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MagicLinkRequest_SendsEmail_ForExistingUser()
    {
        var anonClient = Factory.CreateClient();
        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();

        await anonClient.PostAsJsonAsync("/api/account/magic-link/request",
            new { Email = "test@test.com" }, TestContext.Current.CancellationToken);

        var email = emailService.GetLastEmailTo("test@test.com");
        Assert.NotNull(email);
        Assert.Contains("Anmelde-Link", email!.Subject);
        Assert.Contains("magic-login", email.HtmlBody);
    }

    [Fact]
    public async Task MagicLink_FullFlow_CompletesSignIn()
    {
        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();

        // Step 1: Request magic link
        var anonClient = Factory.CreateDefaultClient(new CookieContainerHandler());
        await anonClient.PostAsJsonAsync("/api/account/magic-link/request",
            new { Email = "test@test.com" }, TestContext.Current.CancellationToken);

        // Step 2: Extract token from email
        var email = emailService.GetLastEmailTo("test@test.com");
        Assert.NotNull(email);

        var (userId, token) = ExtractMagicLinkParams(email!.HtmlBody);
        Assert.NotNull(userId);
        Assert.NotNull(token);

        // Step 3: Login with token
        var loginResponse = await anonClient.PostAsJsonAsync("/api/account/magic-link/login",
            new { UserId = userId, Token = token }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        // Step 4: Verify full auth
        var meResponse = await anonClient.GetAsync("/api/account/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
    }

    [Fact]
    public async Task MagicLink_WithInvalidToken_Returns401()
    {
        var anonClient = Factory.CreateClient();
        var response = await anonClient.PostAsJsonAsync("/api/account/magic-link/login",
            new { UserId = DefaultUser!.Id, Token = "invalid-token" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MagicLink_TokenIsOneTimeUse()
    {
        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();

        var anonClient = Factory.CreateClient();
        await anonClient.PostAsJsonAsync("/api/account/magic-link/request",
            new { Email = "test@test.com" }, TestContext.Current.CancellationToken);

        var email = emailService.GetLastEmailTo("test@test.com");
        var (userId, token) = ExtractMagicLinkParams(email!.HtmlBody);

        // First use — succeeds
        var client1 = Factory.CreateDefaultClient(new CookieContainerHandler());
        var response1 = await client1.PostAsJsonAsync("/api/account/magic-link/login",
            new { UserId = userId, Token = token }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);

        // Second use — fails (token deleted)
        var client2 = Factory.CreateDefaultClient(new CookieContainerHandler());
        var response2 = await client2.PostAsJsonAsync("/api/account/magic-link/login",
            new { UserId = userId, Token = token }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response2.StatusCode);
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private static (string? UserId, string? Token) ExtractMagicLinkParams(string htmlBody)
    {
        // Extract full URL from href="...magic-login..."
        var hrefMatch = MagicLinkHrefRegex().Match(htmlBody);
        if (!hrefMatch.Success) return (null, null);

        var url = hrefMatch.Groups[1].Value;
        var uri = new Uri(url);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        return (query["userId"], query["token"]);
    }

    [GeneratedRegex(@"href=""([^""]*magic-login[^""]*)""", RegexOptions.None)]
    private static partial Regex MagicLinkHrefRegex();
}
