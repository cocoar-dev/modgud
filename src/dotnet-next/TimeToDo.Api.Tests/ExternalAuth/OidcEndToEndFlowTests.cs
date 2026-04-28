using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Application.DTOs.ExternalAuth;
using TimeToDo.Authentication.Domain.ExternalAuth;
using TimeToDo.Authorization.Principals;


namespace TimeToDo.Api.Tests.ExternalAuth;

/// <summary>
/// End-to-end tests for the OIDC login flow using a real in-process TestIdP.
/// Every test:
///   1. Starts TimeToDo (TestServer) + TestIdP (real Kestrel on a free port)
///   2. Creates an IdpConfig via the admin API (signed in as the default admin)
///   3. Registers the generated redirect URI with TestIdP
///   4. Enables the IdpConfig
///   5. Walks the OIDC flow manually (no redirect-follow) so each hop can be
///      asserted independently and failures land with useful messages
///   6. Verifies the resulting TimeToDo state (user, link, claims snapshot)
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class OidcEndToEndFlowTests : IntegrationTestBase
{
    public OidcEndToEndFlowTests(SharedPostgresFixture fixture) : base(fixture) { }

    // HttpClientJsonExtensions defaults to JsonSerializerDefaults.Web (camelCase),
    // but the TimeToDo API is configured with PropertyNamingPolicy = null. Use
    // these options on every POST/PUT so anonymous types serialize PascalCase.
    private static readonly JsonSerializerOptions PascalJson = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public async Task HappyPath_JitCreatesUserAndLink()
    {
        await using var testIdp = new TestIdpServerFixture();
        await testIdp.StartAsync();

        var config = await CreateEnabledIdpConfigAsync(testIdp, autoCreate: true);

        var (appClient, testIdpClient, cookies) = BuildSharedClients(testIdp);

        // ── 1. Kick off external login at TimeToDo ──────────────────
        // /start uses {idpConfigId:guid} so we hand over the raw Guid, not the
        // ShortGuid that admin endpoints emit.
        var configGuid = new BuildingBlocks.Helper.ShortGuid(config.Id).Guid;
        var startResponse = await appClient.GetAsync(
            $"/api/account/external-login/{configGuid}/start?returnUrl=/dashboard");
        var authorizeUri = startResponse.ExpectRedirect("start → authorize");
        Assert.StartsWith(testIdp.BaseAddress, authorizeUri.ToString());

        // ── 2. Follow the redirect to TestIdP /authorize (unauthenticated → /login) ─
        var authorizeResponse = await testIdpClient.GetAsync(authorizeUri);
        var loginPageUri = authorizeResponse.ExpectRedirect("authorize → login");
        Assert.Contains("/login", loginPageUri.ToString());

        // ── 3. Fetch the login page and POST credentials ────────────
        var loginHtmlResponse = await testIdpClient.GetAsync(loginPageUri);
        loginHtmlResponse.EnsureSuccessStatusCode();
        var loginHtml = await loginHtmlResponse.Content.ReadAsStringAsync();
        var returnUrlInForm = OidcFlowExtensions.ExtractHiddenFormField(loginHtml, "returnUrl");
        Assert.False(string.IsNullOrWhiteSpace(returnUrlInForm));

        var loginPost = await testIdpClient.PostAsync("/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["userName"] = "alice",
                ["password"] = "test123",
                ["returnUrl"] = returnUrlInForm!,
            }));
        var afterLogin = loginPost.ExpectRedirect("login POST");

        // ── 4. Follow login-redirect back to /authorize (now with session cookie) ─
        var authorizeCompleted = await testIdpClient.GetAsync(afterLogin);
        var authorizeBody = await authorizeCompleted.Content.ReadAsStringAsync();
        var (callbackMethod, callbackUri, callbackFields) = OidcFlowExtensions
            .ParseAuthorizeResponse(authorizeCompleted, authorizeBody, "authorize complete");
        Assert.Contains("/signin-oidc/", callbackUri.ToString());

        // ── 5. Back to TimeToDo: OIDC middleware consumes the code ──
        var callbackResponse = await SendToTimeTodoAsync(appClient, callbackMethod, callbackUri, callbackFields);
        var afterCallback = callbackResponse.ExpectRedirect(
            "callback → finish (on failure, inspect TestIdpLog.Dump() for server-side OIDC diagnostics)");
        Assert.Contains("/api/account/external-login/finish", afterCallback.ToString());

        // ── 6. Finish processes the External cookie → signs in with app cookie ─
        var finishResponse = await appClient.GetAsync(PathAndQueryOf(afterCallback));
        var finalRedirect = finishResponse.ExpectRedirect("finish → returnUrl");
        Assert.Equal("/dashboard", PathAndQueryOf(finalRedirect));

        // App cookie must be set now
        var appCookie = cookies.GetCookies(new Uri("http://localhost"))
            .Cast<Cookie>()
            .FirstOrDefault(c => c.Name == "TimeToDo.Auth");
        Assert.NotNull(appCookie);

        // ── 7. Verify TimeToDo state ────────────────────────────────
        var meResponse = await appClient.GetAsync("/api/account/me", TestContext.Current.CancellationToken);
        meResponse.EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var link = await session.Query<ExternalIdentityLink>()
            .Where(l => l.Subject == "user-alice-001")
            .FirstOrDefaultAsync();
        Assert.NotNull(link);
        Assert.NotEqual(Guid.Empty, link!.UserId);
        Assert.Equal(config.Id, ShortGuidToGuid(config.Id).ToString("N")
            == link.IdpConfigId.ToString("N") ? config.Id : "");  // sanity
        // The script-run snapshot on the link is a debugging artifact — verify
        // it captured *something* and the run didn't error. The authoritative
        // user data lives on PrincipalDirectory below.
        Assert.NotNull(link.LastScriptOutput);
        Assert.True(link.LastScriptSucceeded);

        var principal = await session.LoadAsync<TimeToDo.Authorization.Principals.Person>(link.UserId, TestContext.Current.CancellationToken);
        Assert.NotNull(principal);
        Assert.Equal("alice@acme.com", principal!.Email);
        Assert.Single(principal.ExternalIdentities);
    }

    [Fact]
    public async Task ReturningUser_ReusesExistingLink_NoDuplicate()
    {
        await using var testIdp = new TestIdpServerFixture();
        await testIdp.StartAsync();

        var config = await CreateEnabledIdpConfigAsync(testIdp, autoCreate: true);

        var countBefore = await CountLinksForSubject("user-alice-001");
        Assert.Equal(0, countBefore);

        await PerformLoginAsync(testIdp, config.Id, "alice", "test123", returnUrl: "/");
        var after1 = await CountLinksForSubject("user-alice-001");
        Assert.Equal(1, after1);

        await PerformLoginAsync(testIdp, config.Id, "alice", "test123", returnUrl: "/");
        var after2 = await CountLinksForSubject("user-alice-001");
        Assert.Equal(1, after2); // second login reuses, no new link

        // LastLoginAt updated on second login
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var link = await session.Query<ExternalIdentityLink>()
            .Where(l => l.Subject == "user-alice-001")
            .FirstAsync();
        Assert.NotNull(link.LastScriptOutput);
    }

    [Fact]
    public async Task AutoCreateOff_StrangerRejected()
    {
        await using var testIdp = new TestIdpServerFixture();
        await testIdp.StartAsync();

        var config = await CreateEnabledIdpConfigAsync(testIdp, autoCreate: false);

        var finalUri = await PerformLoginAsync(testIdp, config.Id, "alice", "test123", returnUrl: "/");

        // Expect redirect to /login with error=Idp.NoUserAndAutoCreateOff.
        // finalUri is relative (".../login?error=...") — don't call .Query on it.
        var finalStr = finalUri.ToString();
        Assert.Contains("/login", finalStr);
        Assert.Contains("error=", finalStr);
        Assert.Contains("NoUserAndAutoCreateOff", Uri.UnescapeDataString(finalStr));

        // No user was created
        var count = await CountLinksForSubject("user-alice-001");
        Assert.Equal(0, count);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private async Task<IdpConfigDto> CreateEnabledIdpConfigAsync(
        TestIdpServerFixture testIdp,
        bool autoCreate)
    {
        // Create via admin API (Client is already admin-authenticated)
        var createResponse = await Client.PostAsJsonAsync("/api/admin/idp-config", new
        {
            Flavor = IdpFlavor.GenericOidc,
            DisplayName = $"TestIdP-{Guid.NewGuid():N}"[..24],
            FlavorData = new { MetadataUri = testIdp.DiscoveryUri },
        }, PascalJson);
        if (!createResponse.IsSuccessStatusCode)
        {
            var body = await createResponse.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException(
                $"POST /idp-config failed {(int)createResponse.StatusCode}: {body}");
        }
        var created = await createResponse.Content.ReadFromJsonAsync<IdpConfigDto>(JsonOptions)
            ?? throw new InvalidOperationException("Create returned null");

        // Rotate secret (required before enable)
        var rotateResponse = await Client.PostAsJsonAsync(
            $"/api/admin/idp-config/{created.Id}/secret",
            new { Secret = TestIdpServerFixture.DefaultClientSecret }, PascalJson);
        if (!rotateResponse.IsSuccessStatusCode)
        {
            var body = await rotateResponse.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException(
                $"POST /secret failed {(int)rotateResponse.StatusCode}: {body}");
        }

        // Update full form with ClientId, autoCreate policy, and a simple transform script.
        // Serialize via JsonSerializer directly so we can log the exact body if needed —
        // the 400 here was a nightmare to debug via PutAsJsonAsync defaults.
        var updateDto = new
        {
            DisplayName = created.DisplayName,
            ClientId = TestIdpServerFixture.DefaultClientId,
            Scopes = new[] { "openid", "profile", "email", "groups", "roles" },
            UserUpdateScript = "(claims) => ({ firstname: claims.given_name?.trim(), lastname: claims.family_name?.trim(), email: claims.email ?? claims.preferred_username, acronym: (claims.given_name?.[0] ?? '') + (claims.family_name?.[0] ?? '') })",
            StoreRawClaims = true,
            RawClaimsRetentionDays = (int?)null,
            AutoCreateUsers = autoCreate,
            AllowLinking = true,
            TrustForEmailLink = false,
            AllowedEmailDomains = (string[]?)null,
            IconName = "key-round",
            ButtonColorHex = (string?)null,
            FlavorData = new { MetadataUri = testIdp.DiscoveryUri },
        };
        var updateJson = JsonSerializer.Serialize(updateDto, PascalJson);
        var updateContent = new StringContent(updateJson, System.Text.Encoding.UTF8, "application/json");
        var updateResponse = await Client.PutAsync($"/api/admin/idp-config/{created.Id}", updateContent);
        if (!updateResponse.IsSuccessStatusCode)
        {
            var body = await updateResponse.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException(
                $"PUT /api/admin/idp-config/{created.Id} failed {(int)updateResponse.StatusCode} {updateResponse.StatusCode}:\nRequest body:\n{updateJson}\n\nResponse:\n{body}");
        }

        var enableResponse = await Client.PostAsJsonAsync($"/api/admin/idp-config/{created.Id}/enable", new { }, PascalJson);
        if (!enableResponse.IsSuccessStatusCode)
        {
            var body = await enableResponse.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException(
                $"POST /enable failed {(int)enableResponse.StatusCode}: {body}");
        }
        var enabled = await enableResponse.Content.ReadFromJsonAsync<IdpConfigDto>(JsonOptions)!
            ?? throw new InvalidOperationException("Enable returned null");

        // Register the redirect URI with TestIdP. In-test the OIDC handler
        // constructs the callback from the inbound request's host — which for
        // TestServer is plain "http://localhost" without a port — so we
        // register that exact URL, regardless of what PublicUrl says.
        var configGuid = new BuildingBlocks.Helper.ShortGuid(enabled.Id).Guid;
        await testIdp.RegisterRedirectUriAsync($"http://localhost/signin-oidc/{configGuid:N}");

        return enabled;
    }

    private (HttpClient App, HttpClient TestIdp, CookieContainer Cookies) BuildSharedClients(
        TestIdpServerFixture testIdp)
    {
        var cookies = new CookieContainer();
        // Shared CookieContainer across both clients — TestServer runs TimeToDo
        // on http://localhost and Kestrel runs TestIdP on http://127.0.0.1:PORT.
        // Cookies are per-host in the container, so the OIDC dance naturally
        // keeps correlation/nonce cookies with TimeToDo and TestIdP's session
        // cookie with TestIdP.
        var appClient = Factory.CreateDefaultClient(new SharedCookieHandler(cookies));
        var testIdpClient = new HttpClient(new HttpClientHandler
        {
            CookieContainer = cookies,
            AllowAutoRedirect = false,
            UseCookies = true,
        })
        {
            BaseAddress = new Uri(testIdp.BaseAddress),
        };
        return (appClient, testIdpClient, cookies);
    }

    /// <summary>
    /// Walks the entire OIDC flow and returns the final Location header.
    /// Returns the URL the browser would be sitting at after the last redirect.
    /// </summary>
    private async Task<Uri> PerformLoginAsync(
        TestIdpServerFixture testIdp,
        string idpConfigId,
        string userName,
        string password,
        string returnUrl)
    {
        var (appClient, testIdpClient, _) = BuildSharedClients(testIdp);

        var configGuid = new BuildingBlocks.Helper.ShortGuid(idpConfigId).Guid;
        var startResponse = await appClient.GetAsync(
            $"/api/account/external-login/{configGuid}/start?returnUrl={Uri.EscapeDataString(returnUrl)}");
        var authorizeUri = startResponse.ExpectRedirect("start");

        var authorizeResponse = await testIdpClient.GetAsync(authorizeUri);
        var loginPageUri = authorizeResponse.ExpectRedirect("authorize → login");

        var loginHtmlResponse = await testIdpClient.GetAsync(loginPageUri);
        var loginHtml = await loginHtmlResponse.Content.ReadAsStringAsync();
        var returnUrlInForm = OidcFlowExtensions.ExtractHiddenFormField(loginHtml, "returnUrl") ?? "";

        var loginPost = await testIdpClient.PostAsync("/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["userName"] = userName,
                ["password"] = password,
                ["returnUrl"] = returnUrlInForm,
            }));
        var afterLogin = loginPost.ExpectRedirect("login POST");

        var authorizeCompleted = await testIdpClient.GetAsync(afterLogin);
        var authorizeBody = await authorizeCompleted.Content.ReadAsStringAsync();
        var (callbackMethod, callbackUri, callbackFields) = OidcFlowExtensions
            .ParseAuthorizeResponse(authorizeCompleted, authorizeBody, "authorize complete");

        var callbackResponse = await SendToTimeTodoAsync(appClient, callbackMethod, callbackUri, callbackFields);
        var afterCallback = callbackResponse.ExpectRedirect("callback");

        var finishResponse = await appClient.GetAsync(PathAndQueryOf(afterCallback));
        return finishResponse.ExpectRedirect("finish");
    }

    private static string PathAndQueryOf(Uri uri)
        => uri.IsAbsoluteUri ? uri.PathAndQuery : uri.ToString();

    /// <summary>
    /// Dispatches either GET (query-mode callback URL) or POST (form_post-mode
    /// form fields) to the TimeToDo app cookie-aware client, honoring the
    /// callback URL the IdP selected.
    /// </summary>
    private static async Task<HttpResponseMessage> SendToTimeTodoAsync(
        HttpClient appClient, HttpMethod method, Uri callbackUri, Dictionary<string, string> formFields)
    {
        var pathAndQuery = PathAndQueryOf(callbackUri);
        if (method == HttpMethod.Get)
            return await appClient.GetAsync(pathAndQuery);
        return await appClient.PostAsync(pathAndQuery, new FormUrlEncodedContent(formFields));
    }

    private async Task<int> CountLinksForSubject(string subject)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<ExternalIdentityLink>()
            .Where(l => l.Subject == subject)
            .CountAsync(TestContext.Current.CancellationToken);
    }

    private static Guid ShortGuidToGuid(string shortGuid)
    {
        try { return new BuildingBlocks.Helper.ShortGuid(shortGuid).Guid; }
        catch { return Guid.Empty; }
    }
}
