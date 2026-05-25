using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modgud.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Domain;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Security tests aligned with the <see href="https://owasp.org/www-project-top-ten/">
/// OWASP Top 10 (2021)</see>. Each section pins behaviour the IDP must
/// guarantee against the named vulnerability class.
///
/// <para>The categories without tests in this file are deliberate:
/// <list type="bullet">
///   <item><b>A04 Insecure Design</b> — covered by the architecture itself
///         (no direct user→permission grants, no shared secrets in tokens,
///         realm-per-DB, …) rather than a single assertable contract.</item>
///   <item><b>A06 Vulnerable Components</b> — handled outside the test
///         suite via Dependabot / package audit.</item>
///   <item><b>A08 Software and Data Integrity</b> — covered by the
///         event-sourced storage + Marten projections (no out-of-band
///         mutation paths).</item>
///   <item><b>A09 Logging and Monitoring</b> — pinned indirectly through
///         the AuthLog tests; no negative-space test fits here.</item>
///   <item><b>A10 SSRF</b> — the IDP makes outbound calls only to
///         configured OIDC providers; URL validation lives in the
///         flavor-specific connection tests.</item>
/// </list></para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Security")]
[Trait("OWASP", "Top10")]
public class OwaspTop10Tests : IntegrationTestBase
{
    public OwaspTop10Tests(SharedPostgresFixture fixture) : base(fixture) { }

    // ═══════════════════════════════════════════════════════════════
    // A01:2021 – Broken Access Control
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task A01_AdminEndpoints_Require_Authentication()
    {
        // Unauthenticated requests to admin surfaces must never leak data.
        // The expected status is 401 (auth required), never 200.
        var anon = Factory.CreateClient();
        var endpoints = new[]
        {
            "/api/user",
            "/api/role",
            "/api/group",
            "/api/admin/oauth/clients",
            "/api/admin/oauth/scopes",
            "/api/admin/oauth/apis",
            "/api/admin/login-providers",
            "/api/admin/auth-log",
            "/api/admin/change-requests",
        };

        foreach (var endpoint in endpoints)
        {
            var response = await anon.GetAsync(endpoint, TestContext.Current.CancellationToken);
            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"Expected 401 for {endpoint}, got {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task A01_AdminEndpoints_Require_AdminPermission_Not_Just_Auth()
    {
        // A signed-in user without any permissions must hit 403, not 200.
        // Pins that authentication alone never grants access — gating is
        // explicit per endpoint via RequiresPermission.
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Reg", lastname: "Ular", acronym: "ru",
            email: "ru@test.com", password: "TestPass1234");
        var client = await CreateAuthenticatedClientAsync("ru", "TestPass1234");

        var response = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A01_NonAdmin_Cannot_Read_Other_Users_Sessions()
    {
        // Direct horizontal escalation guard: a regular user must not be
        // able to read another user's sessions via the admin endpoint
        // even when they know the other user's id.
        var other = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Other", lastname: "Person", acronym: "op",
            email: "op@test.com", password: "TestPass1234");
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Plain", lastname: "User", acronym: "pu",
            email: "pu@test.com", password: "TestPass1234");
        var client = await CreateAuthenticatedClientAsync("pu", "TestPass1234");

        var response = await client.GetAsync(
            $"/api/admin/users/{other.Id}/sessions",
            TestContext.Current.CancellationToken);
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized
                || response.StatusCode == HttpStatusCode.Forbidden,
            $"Expected 401/403, got {(int)response.StatusCode}");
    }

    // ═══════════════════════════════════════════════════════════════
    // A02:2021 – Cryptographic Failures
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task A02_AuthCookie_Is_HttpOnly()
    {
        // The session cookie must be flagged HttpOnly so a JavaScript XSS
        // payload cannot exfiltrate it. Inspect Set-Cookie directly rather
        // than the CookieContainer, which strips the flags.
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Cookie", lastname: "Test", acronym: "ct",
            email: "ct@test.com", password: "TestPass1234");

        var inspect = Factory.CreateClient();
        var response = await inspect.PostAsJsonAsync("/api/account/login",
            new { UserName = "ct", Password = "TestPass1234", RememberMe = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookie = response.Headers
            .Where(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value)
            .ToList();

        Assert.NotEmpty(setCookie);
        Assert.Contains(setCookie, c => c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A02_PasswordHash_Never_Returned_In_Responses()
    {
        // The user-detail response must never include password material —
        // not the hash, not the salt, not the recovery codes. Guards
        // against an accidental "include all fields in the DTO" rewrite.
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Hash", lastname: "Hide", acronym: "hh",
            email: "hh@test.com", password: "TestPass1234",
            isRealmAdmin: true);
        var client = await CreateAuthenticatedClientAsync("hh", "TestPass1234");

        var response = await client.GetAsync(
            $"/api/user/{user.Id}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("PasswordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password_hash", body, StringComparison.OrdinalIgnoreCase);
        // The cleartext password used during creation must not surface either.
        Assert.DoesNotContain("TestPass1234", body);
    }

    [Fact]
    public async Task A02_Login_Does_Not_Reveal_User_Existence()
    {
        // Both "user does not exist" and "user exists, wrong password"
        // must return identical 401 + identical body shape. Otherwise
        // an attacker can enumerate valid usernames.
        var anon = Factory.CreateClient();

        var nonExisting = await anon.PostAsJsonAsync("/api/account/login",
            new { UserName = "no-such-user", Password = "Wrong123!@#", RememberMe = false },
            TestContext.Current.CancellationToken);

        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Real", lastname: "User", acronym: "rl",
            email: "rl@test.com", password: "TestPass1234");
        var existingWrongPwd = await anon.PostAsJsonAsync("/api/account/login",
            new { UserName = "rl", Password = "Wrong123!@#", RememberMe = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(nonExisting.StatusCode, existingWrongPwd.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, nonExisting.StatusCode);

        var nonExistingBody = await nonExisting.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var existingBody = await existingWrongPwd.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(nonExistingBody, existingBody);
    }

    // ═══════════════════════════════════════════════════════════════
    // A03:2021 – Injection
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task A03_SqlInjection_In_Login_Username_Is_Harmless()
    {
        // Marten parameterises every query, so the classic injection
        // payload should land in `userManager.FindByNameAsync` as a
        // literal string, return null, and produce a vanilla 401.
        // Pins the absence of any string-concatenated SQL in the auth
        // path.
        var anon = Factory.CreateClient();
        var response = await anon.PostAsJsonAsync("/api/account/login",
            new
            {
                UserName = "admin'; DROP TABLE users; --",
                Password = "Wrong123!@#",
                RememberMe = false,
            },
            TestContext.Current.CancellationToken);

        // Specifically not 500 — the injection must not crash anything.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════
    // A05:2021 – Security Misconfiguration
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task A05_Error_Responses_Do_Not_Leak_Stack_Traces()
    {
        // Any failure surface that the unauthenticated public can hit
        // must not leak .NET internals. Pick reset-password because it
        // accepts arbitrary strings in production and is reachable
        // anonymously.
        var anon = Factory.CreateClient();
        var response = await anon.PostAsJsonAsync("/api/account/reset-password",
            new { UserId = "not-a-guid", Token = "garbage", NewPassword = "x" },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Common stack-trace markers — never desired in a public response.
        Assert.DoesNotContain("System.NullReferenceException", body);
        Assert.DoesNotContain("System.InvalidOperationException", body);
        Assert.DoesNotContain("Npgsql.", body);
        Assert.DoesNotContain("Marten.", body);
        Assert.DoesNotContain("   at ", body);  // typical "   at Method(...)" stack line
    }

    // ═══════════════════════════════════════════════════════════════
    // A07:2021 – Identification and Authentication Failures
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task A07_BruteForce_Locks_Account_After_Configured_Failures()
    {
        // ASP.NET Identity's lockout is enabled with a 5-attempt
        // threshold; the 6th attempt with the correct password must
        // still fail because the account is locked. Pins that
        // `lockoutOnFailure: true` is wired through.
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Brute", lastname: "Force", acronym: "bf",
            email: "bf@test.com", password: "TestPass1234");

        var anon = Factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            await anon.PostAsJsonAsync("/api/account/login",
                new { UserName = "bf", Password = "Wrong123!@#", RememberMe = false },
                TestContext.Current.CancellationToken);
        }

        var sixth = await anon.PostAsJsonAsync("/api/account/login",
            new { UserName = "bf", Password = "TestPass1234", RememberMe = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, sixth.StatusCode);
    }

    [Fact]
    public async Task A07_Weak_Password_Is_Rejected_By_Identity_Policy()
    {
        // Identity's password policy must reject obviously weak
        // passwords. We use the change-password path because there is
        // no /register endpoint.
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Weak", lastname: "Pwd", acronym: "wp",
            email: "wp@test.com", password: "TestPass1234");
        var client = await CreateAuthenticatedClientAsync("wp", "TestPass1234");

        var response = await client.PostAsJsonAsync("/api/account/change-password",
            new { CurrentPassword = "TestPass1234", NewPassword = "123" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A07_Deactivated_User_Cannot_Login()
    {
        // The login endpoint short-circuits deactivated users at the
        // user-lookup stage with the same generic 401 used for unknown
        // usernames — pinning both the security gate and the no-leak
        // contract from A02.
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Inactive", lastname: "User", acronym: "iu",
            email: "iu@test.com", password: "TestPass1234");

        // Flip IsActive off via Identity directly — the integration
        // path that gets exercised is the same.
        using (var scope = Factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var appUser = await userManager.FindByIdAsync(user.Id.ToString());
            Assert.NotNull(appUser);
            appUser!.IsActive = false;
            await userManager.UpdateAsync(appUser);
        }

        var anon = Factory.CreateClient();
        var response = await anon.PostAsJsonAsync("/api/account/login",
            new { UserName = "iu", Password = "TestPass1234", RememberMe = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A07_ForgotPassword_Always_Returns_200()
    {
        // The forgot-password endpoint must respond with 200 regardless
        // of whether the email exists, otherwise an attacker can
        // enumerate registered accounts. Inspecting the response body
        // would reveal the same generic message in both cases.
        var anon = Factory.CreateClient();

        // Unknown user
        var unknown = await anon.PostAsJsonAsync("/api/account/forgot-password",
            new { UserName = "nobody@nowhere.invalid" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);

        // Known user
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Forgot", lastname: "Me", acronym: "fm",
            email: "fm@test.com", password: "TestPass1234");
        var known = await anon.PostAsJsonAsync("/api/account/forgot-password",
            new { UserName = "fm" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);

        // Generic-message contract: both responses must be byte-identical.
        var unknownBody = await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var knownBody = await known.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(unknownBody, knownBody);
    }
}
