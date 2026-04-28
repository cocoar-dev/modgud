using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Tests.Infrastructure;

namespace Cocoar.Auth.Tests.Security;

/// <summary>
/// Security tests aligned with OWASP Top 10 (2021).
/// Validates that the IAM system is hardened against common web application vulnerabilities.
/// </summary>
[Collection(PlatformCollection.Name)]
[Trait("Category", "Security")]
public class OwaspTop10Tests : IAsyncLifetime
{
    private readonly SharedPostgresFixture _fixture;
    private CocoarAuthWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public OwaspTop10Tests(SharedPostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        var connectionString = await _fixture.CreateIsolatedDatabasesAsync();
        _factory = new CocoarAuthWebApplicationFactory(connectionString);
        _client = _factory.CreateClientWithCookies();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    // A01:2021 – Broken Access Control
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task A01_AdminEndpoints_RequireAuthentication()
    {
        // Unauthenticated requests to admin endpoints should return 401
        var endpoints = new[]
        {
            "/api/admin/users",
            "/api/admin/roles",
            "/api/admin/realms",
            "/api/admin/oauth/clients",
            "/api/admin/oauth/scopes",
            "/api/admin/oauth/apis",
            "/api/admin/login-providers",
            "/api/admin/groups",
        };

        foreach (var endpoint in endpoints)
        {
            var response = await _client.GetAsync(endpoint);
            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"Expected 401 for {endpoint}, got {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task A01_AdminEndpoints_RequireAdminRole()
    {
        // Create a non-admin user and authenticate
        var password = "Test123!@#";
        await _factory.CreateTestUserAsync("regularuser", password);
        await _client.LoginAsync("regularuser", password, _factory.JsonOptions);

        var response = await _client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A01_UserCannotAccessOtherUsersProfile()
    {
        // Create two users
        var password = "Test123!@#";
        var user1 = await _factory.CreateTestUserAsync("user1", password);
        var user2 = await _factory.CreateTestUserAsync("user2", password);

        // Login as user1
        await _client.LoginAsync("user1", password, _factory.JsonOptions);

        // Try to access user2's sessions (should fail)
        var response = await _client.GetAsync($"/api/admin/users/{user2.Id}/sessions");
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden,
            $"Expected 401/403, got {(int)response.StatusCode}");
    }

    // ═══════════════════════════════════════════════════════════════
    // A02:2021 – Cryptographic Failures
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task A02_AuthCookie_IsHttpOnly()
    {
        var password = "Admin123!@#";
        await _factory.CreateTestUserAsync("admin", password, isAdmin: true);

        var handler = new CookieInspectorHandler();
        var inspectClient = _factory.CreateDefaultClient(handler);
        inspectClient.DefaultRequestHeaders.Host = "system.localhost";

        var loginDto = new { UserName = "admin", Password = password, RememberMe = false };
        var response = await inspectClient.PostAsJsonAsync("/api/auth/login", loginDto, _factory.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookieHeaders = response.Headers
            .Where(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value)
            .ToList();

        // Auth cookies must be HttpOnly
        var authCookies = setCookieHeaders.Where(c => c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(authCookies);
    }

    [Fact]
    public async Task A02_PasswordNeverReturnedInResponses()
    {
        var password = "Admin123!@#";
        var user = await _factory.CreateTestUserAsync("admin", password, isAdmin: true);
        await _client.LoginAsync("admin", password, _factory.JsonOptions);

        // Get user details via admin API
        var response = await _client.GetAsync($"/api/admin/users/{user.Id}");
        var body = await response.Content.ReadAsStringAsync();

        // Password hash must never appear in any API response
        Assert.DoesNotContain("PasswordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(password, body);
    }

    [Fact]
    public async Task A02_LoginResponse_DoesNotRevealUserExistence()
    {
        // Login with non-existent user
        var response1 = await _client.LoginAsync("nonexistent", "Wrong123!@#", _factory.JsonOptions);
        var body1 = await response1.Content.ReadAsStringAsync();

        // Create a real user and login with wrong password
        await _factory.CreateTestUserAsync("realuser", "Real123!@#");
        var response2 = await _client.LoginAsync("realuser", "Wrong123!@#", _factory.JsonOptions);
        var body2 = await response2.Content.ReadAsStringAsync();

        // Both should return identical error structure
        var result1 = await response1.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        var result2 = await response2.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);

        Assert.Equal(result1?.ErrorMessage, result2?.ErrorMessage);
    }

    // ═══════════════════════════════════════════════════════════════
    // A03:2021 – Injection
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task A03_SqlInjection_InLoginUsername()
    {
        var response = await _client.LoginAsync("admin'; DROP TABLE users; --", "Test123!@#", _factory.JsonOptions);

        // Should not crash — just return normal error
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);
        Assert.False(result?.Succeeded);
    }

    [Fact]
    public async Task A03_XssPayload_InRegistration()
    {
        var xssPayload = "<script>alert('xss')</script>";
        var registerDto = new RegisterDto
        {
            UserName = "xssuser",
            Email = "xss@test.com",
            Password = "Secure123!@#",
            FirstName = xssPayload,
            LastName = xssPayload
        };

        var response = await _client.PostAsJsonAsync("/api/auth/register", registerDto, _factory.JsonOptions);

        // Should either reject or store safely (no script execution)
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            // If stored, verify it's not reflected as executable HTML
            Assert.DoesNotContain("<script>", body);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // A05:2021 – Security Misconfiguration
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task A05_ErrorResponses_DoNotLeakStackTraces()
    {
        // Trigger an error with an invalid request
        var response = await _client.PostAsJsonAsync("/api/auth/reset-password",
            new { Email = "x", Token = "x", NewPassword = "x" }, _factory.JsonOptions);

        var body = await response.Content.ReadAsStringAsync();

        // Must not contain .NET internals
        Assert.DoesNotContain("System.", body);
        Assert.DoesNotContain("   at ", body);  // Stack trace pattern
        Assert.DoesNotContain("NullReferenceException", body);
        Assert.DoesNotContain("SqlException", body);
    }

    [Fact]
    public async Task A05_SecurityHeaders_ArePresent()
    {
        var response = await _client.GetAsync("/health");

        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
        Assert.True(response.Headers.Contains("X-Frame-Options"));
        Assert.True(response.Headers.Contains("Referrer-Policy"));
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
        Assert.True(response.Headers.Contains("Strict-Transport-Security"));
        Assert.True(response.Headers.Contains("Permissions-Policy"));
    }

    // ═══════════════════════════════════════════════════════════════
    // A07:2021 – Identification and Authentication Failures
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task A07_BruteForce_LocksOutAccount()
    {
        var password = "Correct123!@#";
        var user = await _factory.CreateTestUserAsync("bruteforce", password);

        // 5 failed attempts
        for (int i = 0; i < 5; i++)
        {
            await _client.LoginAsync(user.UserName!, "Wrong123!@#", _factory.JsonOptions);
        }

        // 6th attempt with correct password should still fail (locked out)
        var response = await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);
        var result = await response.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);

        Assert.False(result?.Succeeded);
    }

    [Fact]
    public async Task A07_WeakPassword_IsRejected()
    {
        var registerDto = new RegisterDto
        {
            UserName = "weakpwd",
            Email = "weak@test.com",
            Password = "123", // Too weak
        };

        var response = await _client.PostAsJsonAsync("/api/auth/register", registerDto, _factory.JsonOptions);
        var result = await response.ReadFromJsonAsync<RegisterResultDto>(_factory.JsonOptions);

        // Registration should fail — weak password rejected by Identity
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task A07_DeactivatedUser_CannotLogin()
    {
        var password = "Test123!@#";
        var user = await _factory.CreateTestUserAsync(password: password, isActive: false);

        var response = await _client.LoginAsync(user.UserName!, password, _factory.JsonOptions);
        var result = await response.ReadFromJsonAsync<LoginResultDto>(_factory.JsonOptions);

        Assert.False(result?.Succeeded);
    }

    [Fact]
    public async Task A07_ForgotPassword_DoesNotRevealUserExistence()
    {
        // Request reset for non-existent email
        var response = await _client.PostAsJsonAsync("/api/auth/forgot-password",
            new { Email = "nobody@nowhere.com" }, _factory.JsonOptions);

        // Must always return 200 regardless
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════
    // Helper: Inspect raw HTTP headers (cookies are hidden by CookieContainer)
    // ═══════════════════════════════════════════════════════════════

    private class CookieInspectorHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            return response;
        }
    }
}
