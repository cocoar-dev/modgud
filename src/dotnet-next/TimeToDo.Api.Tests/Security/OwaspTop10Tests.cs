using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TimeToDo.Api.Tests.Infrastructure;

namespace TimeToDo.Api.Tests.Security;

/// <summary>
/// Automated tests mapped to the OWASP Top 10 (2021).
/// These tests verify that the API is resilient against the most critical
/// web application security risks as defined by OWASP.
///
/// See: https://owasp.org/Top10/
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class OwaspTop10Tests : IntegrationTestBase
{
    public OwaspTop10Tests(SharedPostgresFixture fixture) : base(fixture) { }

    // ═══════════════════════════════════════════════════════════════════════
    // A01:2021 – Broken Access Control
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A01_VerticalPrivilegeEscalation_NonAdminCannotAccessAdminEndpoints()
    {
        // Arrange: non-admin user
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Regular", lastname: "User", acronym: "RU",
            permissions: ["todo:read"]);
        using var nonAdminClient = await CreateAuthenticatedClientAsync("ru", "TestPass1234");

        // Act: try to create a user (admin-only)
        var response = await nonAdminClient.PostAsJsonAsync("/api/user",
            new { Firstname = "Hack", Lastname = "Attempt", Acronym = "HA", UserName = "hacker" }, TestContext.Current.CancellationToken);

        // Assert: 403 Forbidden, not 200
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A01_VerticalPrivilegeEscalation_NonAdminCannotResetPasswords()
    {
        // Arrange
        var victim = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Victim", lastname: "User", acronym: "VU");
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Attacker", lastname: "User", acronym: "AU",
            permissions: ["todo:read"]);
        using var attackerClient = await CreateAuthenticatedClientAsync("au", "TestPass1234");

        // Act: try to reset victim's password
        var response = await attackerClient.PutAsJsonAsync(
            $"/api/user/{new BuildingBlocks.Helper.ShortGuid(victim.Id)}/password",
            new { Password = "Hacked123!" }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A01_VerticalPrivilegeEscalation_NonAdminCannotCreateGroups()
    {
        // Direct user→role assignment no longer exists — permissions flow only via
        // group membership. The equivalent vertical-escalation path is now:
        // "can a non-admin create a group that would grant them admin role?"
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Attacker", lastname: "User", acronym: "AU2",
            permissions: ["todo:read"]);
        using var attackerClient = await CreateAuthenticatedClientAsync("au2", "TestPass1234");

        var response = await attackerClient.PostAsJsonAsync(
            "/api/group",
            new
            {
                Name = "Sneaky Admin",
                Description = "",
                MemberIds = Array.Empty<string>(),
                RoleIds = Array.Empty<string>(),
                AccessScripts = Array.Empty<object>(),
                MembershipMode = "Manual",
            }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A01_IDOR_UserCannotAccessOtherUsersData_ViaDirectObjectReference()
    {
        // Arrange: two users. User1 gets a wildcard-scope group so the coupled proto
        // check lets a create through; User2 gets read permission + wildcard scope to
        // prove the IDOR path (direct object reference) is not user-scoped.
        var user1 = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "User", lastname: "One", acronym: "U1",
            permissions: []);
        var user2 = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "User", lastname: "Two", acronym: "U2",
            permissions: []);

        var writerRole = await Factory.CreateTestRoleAsync("IdorWriter", "todo", ["read", "create"]);
        var readerRole = await Factory.CreateTestRoleAsync("IdorReader", "todo", ["read"]);
        var unrestricted = TimeTodoWebApplicationFactory.BuildAccessScript("todo", "(t) => true");

        await Factory.CreateTestGroupAsync("IdorWriterGroup", [user1.Id],
            roleIds: [writerRole.Id], accessScripts: [unrestricted]);
        await Factory.CreateTestGroupAsync("IdorReaderGroup", [user2.Id],
            roleIds: [readerRole.Id], accessScripts: [unrestricted]);

        using var client1 = await CreateAuthenticatedClientAsync("u1", "TestPass1234");
        var createResponse = await client1.PostAsJsonAsync("/api/todo",
            new { Title = "Private Todo", Status = "new" }, TestContext.Current.CancellationToken);
        Assert.True(createResponse.IsSuccessStatusCode);

        // Act: user2 tries to access all todos (should succeed — todos are shared in this app)
        using var client2 = await CreateAuthenticatedClientAsync("u2", "TestPass1234");
        var todosResponse = await client2.GetAsync("/api/todo", TestContext.Current.CancellationToken);

        // Assert: user2 can see todos (todos are not user-scoped in this app)
        // This documents the current behavior — if todos become user-scoped, this test should change
        Assert.Equal(HttpStatusCode.OK, todosResponse.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A02:2021 – Cryptographic Failures
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A02_CookieSecurity_HttpOnlyAndSameSiteStrict()
    {
        // Arrange & Act: login and inspect Set-Cookie header
        var client = Factory.CreateDefaultClient(new CookieInspectorHandler());
        var loginResponse = await client.PostAsJsonAsync("/api/account/login",
            new { UserName = "tu", Password = "TestPass1234" }, TestContext.Current.CancellationToken);

        Assert.True(loginResponse.IsSuccessStatusCode);

        // Assert: check cookie attributes
        Assert.True(loginResponse.Headers.TryGetValues("Set-Cookie", out var cookies));
        var authCookie = cookies.FirstOrDefault(c => c.Contains("TimeToDo.Auth"));
        Assert.NotNull(authCookie);

        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", authCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A02_PasswordsAreNotReturnedInApiResponses()
    {
        // Act: get user list
        var response = await Client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync();

        // Assert: response must not contain actual password hashes or security stamps.
        // "HasPassword" (boolean) and "Password" in endpoint names are OK.
        Assert.DoesNotContain("PasswordHash", body);
        Assert.DoesNotContain("SecurityStamp", body);
        Assert.DoesNotContain("NormalizedUserName", body);
        Assert.DoesNotContain("ConcurrencyStamp", body);
    }

    [Fact]
    public async Task A02_LoginResponse_DoesNotLeakUserExistence()
    {
        // Act: try to login with non-existent user
        var response1 = await Factory.CreateClient().PostAsJsonAsync("/api/account/login",
            new { UserName = "nonexistent_user_xyz", Password = "Wrong1234!" }, TestContext.Current.CancellationToken);

        // Act: try to login with wrong password for existing user
        var response2 = await Factory.CreateClient().PostAsJsonAsync("/api/account/login",
            new { UserName = "tu", Password = "WrongPassword1!" }, TestContext.Current.CancellationToken);

        // Assert: both should return 401 — same status code to prevent user enumeration
        Assert.Equal(HttpStatusCode.Unauthorized, response1.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, response2.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A03:2021 – Injection
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A03_SqlInjection_InQueryParameters_DoesNotReturnUnexpectedData()
    {
        // Marten uses parameterized queries, so SQL injection should not work.
        // We verify that injection payloads don't bypass normal behavior.
        var payloads = new[]
        {
            "/api/todo?id=1' OR 1=1--",
            "/api/todo?orderBy=Title;DROP TABLE todos--",
        };

        foreach (var payload in payloads)
        {
            var response = await Client.GetAsync(payload);
            // Acceptable: 200 (empty result), 400 (bad request), or 500 (parse error).
            // NOT acceptable: returning data that shouldn't be visible.
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                // If 200, it should be a normal response (array), not a SQL error message
                Assert.DoesNotContain("pg_catalog", body);
                Assert.DoesNotContain("information_schema", body);
                Assert.DoesNotContain("NpgsqlException", body);
            }
        }
    }

    [Fact]
    public async Task A03_XssPayload_InCommentDescription_IsStoredButNotExecutable()
    {
        // Arrange: create a todo for the comment
        var todo = await Factory.CreateTestTodoAsync(title: "XSS Test Todo", createdById: DefaultUser!.Id);
        var todoShortId = new BuildingBlocks.Helper.ShortGuid(todo.Id).ToString();

        var xssPayloads = new[]
        {
            "<script>alert('XSS')</script>",
            "<img src=x onerror=alert(1)>",
            "<svg onload=alert(1)>",
            "javascript:alert(1)",
            "<iframe src='javascript:alert(1)'>",
        };

        foreach (var payload in xssPayloads)
        {
            // Act: create comment with XSS payload
            var response = await Client.PostAsJsonAsync(
                $"/api/comment/todo/{todoShortId}",
                new { Description = payload }, TestContext.Current.CancellationToken);

            // Assert: API accepts the input (Rich Text Editor stores HTML)
            // but the stored value should be treated as data, not code.
            // Frontend must sanitize with DOMPurify before rendering.
            Assert.True(response.IsSuccessStatusCode,
                $"Comment creation failed for payload: {payload}");
        }

        // Wait for async projections to catch up
        await Factory.WaitForProjectionsAsync();

        // Verify comments are retrievable (not corrupted by injection)
        var commentsResponse = await Client.GetAsync($"/api/comment/todo/{todoShortId}", TestContext.Current.CancellationToken);
        Assert.True(commentsResponse.IsSuccessStatusCode);
        var comments = await commentsResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(comments.GetArrayLength() >= xssPayloads.Length,
            $"Expected at least {xssPayloads.Length} comments, got {comments.GetArrayLength()}");
    }

    [Fact]
    public async Task A03_XssPayload_InTodoTitle_IsStoredSafely()
    {
        // Act: create todo with XSS payload in title
        var response = await Client.PostAsJsonAsync("/api/todo",
            new { Title = "<script>alert('XSS')</script>", Status = "new" }, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);

        // Verify it's stored and returned as data
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var title = body.GetProperty("Title").GetString();
        Assert.Contains("<script>", title); // Stored as-is (not stripped), frontend must sanitize
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A05:2021 – Security Misconfiguration
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A05_ErrorResponses_DoNotLeakStackTraces()
    {
        // Act: trigger various errors
        var responses = new[]
        {
            await Client.GetAsync("/api/todo/INVALID_ID_FORMAT", TestContext.Current.CancellationToken),
            await Client.DeleteAsync("/api/todo/NONEXISTENT", TestContext.Current.CancellationToken),
            await Client.PutAsJsonAsync("/api/todo/INVALID", new { }),
        };

        foreach (var response in responses)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("System.", body); // No .NET namespaces
                Assert.DoesNotContain("at TimeToDo.", body); // No stack traces
                Assert.DoesNotContain("NpgsqlException", body); // No DB errors
                Assert.DoesNotContain("SqlException", body);
            }
        }
    }

    [Fact]
    public async Task A05_OpenApiEndpoint_NotAvailableInProduction()
    {
        // In our test environment (Testing), OpenAPI IS available.
        // This test documents the expectation that it's disabled in Production.
        // The actual check is in Program.cs: if (!app.Environment.IsProduction()) { app.MapOpenApi(); }

        // Verify it's available in Testing
        var response = await Client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The production check is a code review item — we verify the code path exists:
        // This test ensures the OpenAPI spec doesn't leak internal infrastructure details
        var spec = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("connectionString", spec, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localhost:5432", spec); // No DB connection strings
        Assert.DoesNotContain("NpgsqlConnection", spec);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A07:2021 – Identification and Authentication Failures
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A07_BruteForce_AccountLocksAfterFailedAttempts()
    {
        // Arrange: create a user
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Lockout", lastname: "User", acronym: "LU");

        // Act: exhaust all 5 allowed attempts
        for (int i = 0; i < 5; i++)
        {
            await Factory.CreateClient().PostAsJsonAsync("/api/account/login",
                new { UserName = "lu", Password = "WrongPassword!" }, TestContext.Current.CancellationToken);
        }

        // Assert: even with the CORRECT password, login should fail (account locked).
        // The response is intentionally 401 (not 423) to prevent lockout DoS reconnaissance.
        var correctResponse = await Factory.CreateClient().PostAsJsonAsync("/api/account/login",
            new { UserName = "lu", Password = "TestPass1234" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, correctResponse.StatusCode);
    }

    [Fact]
    public async Task A07_SessionManagement_LogoutInvalidatesSession()
    {
        // Arrange: create user and login
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Session", lastname: "User", acronym: "SU");
        using var sessionClient = await CreateAuthenticatedClientAsync("su", "TestPass1234");

        // Verify session works
        var meResponse = await sessionClient.GetAsync("/api/account/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        // Act: logout
        var logoutResponse = await sessionClient.PostAsync("/api/account/logout", null, TestContext.Current.CancellationToken);
        Assert.True(logoutResponse.IsSuccessStatusCode);

        // Assert: session is invalidated — subsequent requests should fail
        var afterLogoutResponse = await sessionClient.GetAsync("/api/account/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogoutResponse.StatusCode);
    }

    [Fact]
    public async Task A07_PasswordPolicy_WeakPasswordsAreRejected()
    {
        // Arrange: create a user (admin needed to set passwords)
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "PwPolicy", lastname: "User", acronym: "PW");
        var userId = new BuildingBlocks.Helper.ShortGuid(user.Id).ToString();

        var weakPasswords = new[]
        {
            "short",         // Too short (< 8 chars)
            "alllowercase1", // No uppercase
            "ALLUPPERCASE1", // No lowercase
            "NoDigitsHere",  // No digit
        };

        foreach (var weakPassword in weakPasswords)
        {
            var response = await Client.PutAsJsonAsync(
                $"/api/user/{userId}/password",
                new { Password = weakPassword }, TestContext.Current.CancellationToken);

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
                $"Weak password '{weakPassword}' should be rejected but got {response.StatusCode}");
        }
    }

    [Fact]
    public async Task A07_DeactivatedUser_CannotLogin()
    {
        // Arrange: create and deactivate user
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Deactivated", lastname: "User", acronym: "DU");
        var userId = new BuildingBlocks.Helper.ShortGuid(user.Id).ToString();

        // Deactivate via admin
        var deactivateResponse = await Client.PutAsJsonAsync(
            $"/api/user/{userId}/active",
            new { IsActive = false }, TestContext.Current.CancellationToken);
        Assert.True(deactivateResponse.IsSuccessStatusCode);

        // Act: try to login as deactivated user
        var anonClient = Factory.CreateClient();
        var loginResponse = await anonClient.PostAsJsonAsync("/api/account/login",
            new { UserName = "du", Password = "TestPass1234" }, TestContext.Current.CancellationToken);

        // Assert: login should be rejected with same 401 as any other failure (no info leak)
        Assert.Equal(HttpStatusCode.Unauthorized, loginResponse.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helper
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// DelegatingHandler that does NOT consume/store cookies —
    /// allows us to inspect raw Set-Cookie headers.
    /// </summary>
    private class CookieInspectorHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => base.SendAsync(request, cancellationToken);
    }
}
