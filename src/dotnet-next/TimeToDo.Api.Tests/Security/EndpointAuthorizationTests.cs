using System.Net;
using System.Text.Json;
using TimeToDo.Api.Tests.Infrastructure;

namespace TimeToDo.Api.Tests.Security;

/// <summary>
/// Central security map for every API endpoint. Each route from the OpenAPI
/// spec must appear here with an explicit policy. Tests below ensure:
/// <list type="number">
///   <item>Every discovered endpoint has a policy entry (no silent defaults)</item>
///   <item>Anonymous endpoints are reachable without auth</item>
///   <item>Authenticated endpoints return 401 for anonymous callers</item>
///   <item>RequiresPermission endpoints return 403 for users lacking the permission</item>
/// </list>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class EndpointAuthorizationTests : IntegrationTestBase
{
    public enum EndpointKind
    {
        Anonymous,
        AuthenticatedOnly,
        RequiresPermission,
    }

    /// <param name="TestBody">
    /// Override body for test probes when the handler expects something other than <c>{}</c>
    /// (e.g. <c>[]</c> for <c>List&lt;string&gt;</c>). Without a matching body the request fails
    /// 400 at model-binding, before <c>.RequiresPermission</c> can run.
    /// </param>
    public record EndpointPolicy(EndpointKind Kind, string? Permission = null, string? TestBody = null);

    private static readonly Dictionary<string, EndpointPolicy> EndpointPolicies = new(StringComparer.OrdinalIgnoreCase)
    {
        // ─── Account ────────────────────────────────────────────────
        ["POST /api/account/login"]            = new(EndpointKind.Anonymous),
        ["POST /api/account/logout"]           = new(EndpointKind.AuthenticatedOnly),
        ["GET /api/account/me"]                = new(EndpointKind.AuthenticatedOnly),
        ["POST /api/account/change-password"]  = new(EndpointKind.AuthenticatedOnly),
        ["POST /api/account/forgot-password"]  = new(EndpointKind.Anonymous),
        ["POST /api/account/reset-password"]   = new(EndpointKind.Anonymous),

        // Setup + Status + Health + App-Info
        ["GET /api/setup/status"]          = new(EndpointKind.Anonymous),
        ["POST /api/setup/create-admin"]   = new(EndpointKind.Anonymous),
        ["GET /api/status"]                = new(EndpointKind.AuthenticatedOnly),
        ["GET /api/health"]                = new(EndpointKind.Anonymous),
        ["GET /api/app-info"]              = new(EndpointKind.Anonymous),

        // ─── Email OTP ──────────────────────────────────────────────
        ["GET /api/account/email-otp/status"]         = new(EndpointKind.AuthenticatedOnly),
        ["POST /api/account/email-otp/enable"]        = new(EndpointKind.AuthenticatedOnly),
        ["POST /api/account/email-otp/disable"]       = new(EndpointKind.AuthenticatedOnly),
        ["POST /api/account/email-otp/login/request"] = new(EndpointKind.Anonymous),
        ["POST /api/account/email-otp/login"]         = new(EndpointKind.Anonymous),

        // ─── Magic Link ─────────────────────────────────────────────
        ["POST /api/account/magic-link/request"] = new(EndpointKind.Anonymous),
        ["POST /api/account/magic-link/login"]   = new(EndpointKind.Anonymous),

        // ─── MFA ────────────────────────────────────────────────────
        ["GET /api/account/mfa/status"]   = new(EndpointKind.AuthenticatedOnly),
        ["POST /api/account/mfa/setup"]   = new(EndpointKind.AuthenticatedOnly),
        ["POST /api/account/mfa/verify"]  = new(EndpointKind.AuthenticatedOnly),
        ["POST /api/account/mfa/disable"] = new(EndpointKind.AuthenticatedOnly),
        ["POST /api/account/mfa/login"]   = new(EndpointKind.Anonymous),

        // ─── External Auth (OIDC) ───────────────────────────────────
        ["GET /api/account/external-logins"]                          = new(EndpointKind.Anonymous),
        ["GET /api/account/external-login/{idpConfigId}/start"]       = new(EndpointKind.Anonymous),
        ["GET /api/account/external-login/finish"]                    = new(EndpointKind.Anonymous),
        ["GET /api/account/external-logout/{idpConfigId}"]            = new(EndpointKind.Anonymous),
        ["GET /api/account/external-links"]                           = new(EndpointKind.AuthenticatedOnly),
        ["DELETE /api/account/external-links/{linkId}"]               = new(EndpointKind.AuthenticatedOnly),
        ["GET /api/admin/users/{userId}/external-links"]              = new(EndpointKind.RequiresPermission, "app:admin"),

        // ─── Admin: IdP Config CRUD + lifecycle ─────────────────────
        ["GET /api/admin/idp-config/flavors"]                         = new(EndpointKind.RequiresPermission, "app:admin"),
        ["GET /api/admin/idp-config"]                                 = new(EndpointKind.RequiresPermission, "app:admin"),
        ["GET /api/admin/idp-config/{id}"]                            = new(EndpointKind.RequiresPermission, "app:admin"),
        ["POST /api/admin/idp-config"]                                = new(EndpointKind.RequiresPermission, "app:admin", TestBody: "{\"flavor\":\"GenericOidc\",\"displayName\":\"x\"}"),
        ["PUT /api/admin/idp-config/{id}"]                            = new(EndpointKind.RequiresPermission, "app:admin", TestBody: "{\"displayName\":\"x\",\"clientId\":\"x\",\"scopes\":[],\"claimsTransformScript\":\"x\",\"storeRawClaims\":false,\"autoCreateUsers\":false,\"allowLinking\":true,\"trustForEmailLink\":false}"),
        ["POST /api/admin/idp-config/{id}/enable"]                    = new(EndpointKind.RequiresPermission, "app:admin"),
        ["POST /api/admin/idp-config/{id}/disable"]                   = new(EndpointKind.RequiresPermission, "app:admin"),
        ["DELETE /api/admin/idp-config/{id}"]                         = new(EndpointKind.RequiresPermission, "app:admin"),
        ["POST /api/admin/idp-config/{id}/secret"]                    = new(EndpointKind.RequiresPermission, "app:admin", TestBody: "{\"secret\":\"x\"}"),
        ["POST /api/admin/idp-config/{id}/test-user-update"]          = new(EndpointKind.RequiresPermission, "app:admin", TestBody: "{}"),
        ["GET /api/admin/idp-config/{id}/last-raw-claims"]            = new(EndpointKind.RequiresPermission, "app:admin"),

        // ─── Passkey ────────────────────────────────────────────────
        ["GET /api/account/passkey"]                   = new(EndpointKind.AuthenticatedOnly),
        ["POST /api/account/passkey/register-options"] = new(EndpointKind.AuthenticatedOnly),
        ["POST /api/account/passkey/register"]         = new(EndpointKind.AuthenticatedOnly),
        ["DELETE /api/account/passkey/{id}"]           = new(EndpointKind.AuthenticatedOnly),
        ["POST /api/account/passkey/login-options"]    = new(EndpointKind.Anonymous),
        ["POST /api/account/passkey/login"]            = new(EndpointKind.Anonymous),

        // ─── Profile self-service ───────────────────────────────────
        ["PUT /api/account/profile/request"]                          = new(EndpointKind.AuthenticatedOnly, TestBody: "{}"),
        ["POST /api/account/profile/request/verify-email"]            = new(EndpointKind.Anonymous),
        ["DELETE /api/account/profile/request"]                       = new(EndpointKind.AuthenticatedOnly),
        ["GET /api/account/profile/request"]                          = new(EndpointKind.AuthenticatedOnly),

        // ─── Admin: Change Requests ─────────────────────────────────
        ["GET /api/admin/change-requests"]                            = new(EndpointKind.RequiresPermission, "app:admin"),
        ["POST /api/admin/change-requests/{id}/approve"]              = new(EndpointKind.RequiresPermission, "app:admin", TestBody: "{}"),
        ["POST /api/admin/change-requests/{id}/reject"]               = new(EndpointKind.RequiresPermission, "app:admin", TestBody: "{}"),

        // ─── Admin: Magic Link + Auth Log + Projections ─────────────
        ["POST /api/admin/users/{id}/magic-link"]         = new(EndpointKind.RequiresPermission, "app:admin"),
        ["GET /api/admin/users/{id}/security-info"]       = new(EndpointKind.RequiresPermission, "app:admin"),
        ["POST /api/admin/users/{id}/grace/reset"]        = new(EndpointKind.RequiresPermission, "app:admin"),
        ["PUT /api/admin/users/{id}/grace/policy"]        = new(EndpointKind.RequiresPermission, "app:admin", TestBody: "{}"),
        ["DELETE /api/admin/users/{id}/grace"]            = new(EndpointKind.RequiresPermission, "app:admin"),
        ["POST /api/admin/authorization/simulate"]        = new(EndpointKind.RequiresPermission, "app:admin"),
        ["GET /api/admin/auth-log"]                       = new(EndpointKind.RequiresPermission, "app:admin"),
        ["DELETE /api/admin/auth-log"]                    = new(EndpointKind.RequiresPermission, "app:admin"),
        ["POST /api/admin/projections/rebuild"]           = new(EndpointKind.RequiresPermission, "app:admin"),
        ["GET /api/admin/projections/consistency-check"]  = new(EndpointKind.RequiresPermission, "app:admin"),

        // ─── Comments ───────────────────────────────────────────────
        ["GET /api/comment"]                        = new(EndpointKind.RequiresPermission, "comment:read"),
        ["GET /api/comment/{type}/{referenceId}"]   = new(EndpointKind.RequiresPermission, "comment:read"),
        ["GET /api/comment/{id}"]                   = new(EndpointKind.RequiresPermission, "comment:read"),
        ["POST /api/comment/{type}/{referenceId}"]  = new(EndpointKind.RequiresPermission, "comment:create"),
        ["POST /api/comment/{id}/read"]             = new(EndpointKind.RequiresPermission, "comment:read"),
        ["DELETE /api/comment/{id}"]                = new(EndpointKind.RequiresPermission, "comment:delete"),

        // ─── Customers ──────────────────────────────────────────────
        ["GET /api/customer"]                 = new(EndpointKind.RequiresPermission, "customer:read"),
        ["GET /api/customer/lookup"]          = new(EndpointKind.AuthenticatedOnly),
        ["GET /api/customer/archived"]        = new(EndpointKind.RequiresPermission, "customer:read"),
        ["GET /api/customer/{id}"]            = new(EndpointKind.RequiresPermission, "customer:read"),
        ["POST /api/customer"]                = new(EndpointKind.RequiresPermission, "customer:create"),
        ["PUT /api/customer/{id}"]            = new(EndpointKind.RequiresPermission, "customer:update"),
        ["PUT /api/customer/archive"]         = new(EndpointKind.RequiresPermission, "customer:archive", TestBody: "[]"),
        ["POST /api/customer/archive/{id}"]   = new(EndpointKind.RequiresPermission, "customer:archive"),
        ["POST /api/customer/restore/{id}"]   = new(EndpointKind.RequiresPermission, "customer:restore"),
        ["DELETE /api/customer/{id}"]         = new(EndpointKind.RequiresPermission, "customer:delete"),
        ["DELETE /api/customer"]              = new(EndpointKind.RequiresPermission, "customer:delete", TestBody: "[]"),

        // ─── Dev (Development-only, anonymous) ──────────────────────
        ["GET /api/dev/emails"]              = new(EndpointKind.Anonymous),
        ["GET /api/dev/emails/{to}"]         = new(EndpointKind.Anonymous),
        ["DELETE /api/dev/emails"]           = new(EndpointKind.Anonymous),
        ["POST /api/dev/reset-mfa/{userName}"] = new(EndpointKind.Anonymous),

        // ─── Groups ─────────────────────────────────────────────────
        ["GET /api/group/lookup"]                  = new(EndpointKind.AuthenticatedOnly),
        ["GET /api/group"]                         = new(EndpointKind.RequiresPermission, "app:admin"),
        ["GET /api/group/{id}"]                    = new(EndpointKind.RequiresPermission, "app:admin"),
        ["GET /api/group/{id}/effective-members"]  = new(EndpointKind.RequiresPermission, "app:admin"),
        ["POST /api/group"]                        = new(EndpointKind.RequiresPermission, "app:admin"),
        ["PUT /api/group/{id}"]                    = new(EndpointKind.RequiresPermission, "app:admin"),
        ["DELETE /api/group/{id}"]                 = new(EndpointKind.RequiresPermission, "app:admin"),

        // ─── Migration ──────────────────────────────────────────────
        ["POST /api/migration/users"]            = new(EndpointKind.RequiresPermission, "app:admin"),
        ["POST /api/migration/customers"]        = new(EndpointKind.RequiresPermission, "app:admin"),
        ["POST /api/migration/todos"]            = new(EndpointKind.RequiresPermission, "app:admin"),
        ["POST /api/migration/comments"]         = new(EndpointKind.RequiresPermission, "app:admin"),
        ["POST /api/migration/populate-labels"]  = new(EndpointKind.RequiresPermission, "app:admin"),

        // ─── Principals ─────────────────────────────────────────────
        ["GET /api/principal/lookup"] = new(EndpointKind.AuthenticatedOnly),

        // ─── Roles ──────────────────────────────────────────────────
        ["GET /api/role/lookup"]    = new(EndpointKind.AuthenticatedOnly),
        ["GET /api/role"]           = new(EndpointKind.RequiresPermission, "app:admin"),
        ["GET /api/role/{id}"]      = new(EndpointKind.RequiresPermission, "app:admin"),
        ["POST /api/role"]          = new(EndpointKind.RequiresPermission, "app:admin"),
        ["PUT /api/role/{id}"]      = new(EndpointKind.RequiresPermission, "app:admin"),
        ["DELETE /api/role/{id}"]   = new(EndpointKind.RequiresPermission, "app:admin"),

        // ─── Script Types ───────────────────────────────────────────
        ["GET /api/script-types/principal"] = new(EndpointKind.AuthenticatedOnly),

        // ─── Todo Maintenance ───────────────────────────────────────
        ["POST /api/maintenance/todos/reconcile-children"]     = new(EndpointKind.RequiresPermission, "app:admin"),
        ["POST /api/maintenance/todos/fix-orphaned-children"]  = new(EndpointKind.RequiresPermission, "app:admin"),
        ["GET /api/maintenance/todos/validate-relationships"]  = new(EndpointKind.RequiresPermission, "app:admin"),

        // ─── Todos ──────────────────────────────────────────────────
        ["GET /api/todo"]                                      = new(EndpointKind.RequiresPermission, "todo:read"),
        ["GET /api/todo/archive"]                              = new(EndpointKind.RequiresPermission, "todo:read"),
        ["GET /api/todo/{id}"]                                 = new(EndpointKind.RequiresPermission, "todo:read"),
        ["POST /api/todo"]                                     = new(EndpointKind.RequiresPermission, "todo:create", TestBody: """{"title":"x"}"""),
        ["PUT /api/todo/{id}"]                                 = new(EndpointKind.RequiresPermission, "todo:update"),
        ["PUT /api/todo/update/status"]                        = new(EndpointKind.RequiresPermission, "todo:update"),
        ["PATCH /api/todo/update/flags"]                       = new(EndpointKind.RequiresPermission, "todo:flag"),
        ["POST /api/todo/{subTodoId}/move-into/{parentTodoId}"] = new(EndpointKind.RequiresPermission, "todo:move"),
        ["POST /api/todo/convert-to-parent"]                   = new(EndpointKind.RequiresPermission, "todo:move", TestBody: "[]"),
        ["PUT /api/todo/archive"]                              = new(EndpointKind.RequiresPermission, "todo:archive", TestBody: "[]"),
        ["DELETE /api/todo"]                                   = new(EndpointKind.RequiresPermission, "todo:delete", TestBody: "[]"),

        // ─── Users ──────────────────────────────────────────────────
        ["GET /api/user/lookup"]                       = new(EndpointKind.AuthenticatedOnly),
        ["GET /api/user/{id}"]                         = new(EndpointKind.RequiresPermission, "app:admin"),
        ["GET /api/user"]                              = new(EndpointKind.RequiresPermission, "app:admin"),
        ["POST /api/user"]                             = new(EndpointKind.RequiresPermission, "app:admin"),
        ["PUT /api/user/{id}"]                         = new(EndpointKind.RequiresPermission, "app:admin"),
        ["DELETE /api/user/{id}"]                      = new(EndpointKind.RequiresPermission, "app:admin"),
        ["DELETE /api/user"]                           = new(EndpointKind.RequiresPermission, "app:admin"),
        ["PUT /api/user/{id}/password"]                = new(EndpointKind.RequiresPermission, "app:admin"),
        ["PUT /api/user/{id}/active"]                  = new(EndpointKind.RequiresPermission, "app:admin"),
        ["GET /api/user/{id}/groups"]                  = new(EndpointKind.RequiresPermission, "app:admin"),
        ["POST /api/user/{id}/groups"]                 = new(EndpointKind.RequiresPermission, "app:admin"),
        ["DELETE /api/user/{id}/groups/{groupId}"]     = new(EndpointKind.RequiresPermission, "app:admin"),
    };

    public EndpointAuthorizationTests(SharedPostgresFixture fixture) : base(fixture) { }

    // ═══════════════════════════════════════════════════════════════
    // Test 1: every discovered endpoint has a policy entry
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AllEndpoints_AreInPolicyMap()
    {
        var endpoints = await DiscoverEndpointsAsync();

        var missing = endpoints
            .Select(e => $"{e.Method} {e.Path}")
            .Where(key => !EndpointPolicies.ContainsKey(key))
            .ToList();

        Assert.True(missing.Count == 0,
            $"These endpoints are missing from EndpointPolicies (add them with an explicit policy):\n" +
            string.Join("\n", missing));
    }

    // ═══════════════════════════════════════════════════════════════
    // Test 2: unauthenticated caller gets 401 on non-Anonymous routes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AllEndpoints_WithoutAuthentication_Return401_UnlessAnonymous()
    {
        var anonClient = Factory.CreateClient();
        var endpoints = await DiscoverEndpointsAsync();
        var failures = new List<string>();

        foreach (var (method, path) in endpoints)
        {
            var key = $"{method} {path}";
            if (!EndpointPolicies.TryGetValue(key, out var policy))
                continue; // handled by AllEndpoints_AreInPolicyMap
            if (policy.Kind == EndpointKind.Anonymous)
                continue;

            var response = await SendProbe(anonClient, method, path, policy.TestBody);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                failures.Add($"{key} → {(int)response.StatusCode} {response.StatusCode} (expected 401)");
        }

        Assert.True(failures.Count == 0,
            $"Non-anonymous endpoints reachable without authentication:\n" +
            string.Join("\n", failures));
    }

    // ═══════════════════════════════════════════════════════════════
    // Test 3: RequiresPermission endpoints return 403 for users
    //         without that permission
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AllEndpoints_WithoutRequiredPermission_Return403()
    {
        // User with NO permissions — no app:admin bypass, no resource permissions.
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "No", lastname: "Perms", acronym: "NP",
            email: "noperms@test.com",
            permissions: []);

        using var client = await CreateAuthenticatedClientAsync("np", "TestPass1234");
        var failures = new List<string>();

        foreach (var (key, policy) in EndpointPolicies)
        {
            if (policy.Kind != EndpointKind.RequiresPermission) continue;

            var parts = key.Split(' ', 2);
            var response = await SendProbe(client, parts[0], parts[1], policy.TestBody);

            if (response.StatusCode != HttpStatusCode.Forbidden)
                failures.Add($"{key} (needs '{policy.Permission}') → {(int)response.StatusCode} {response.StatusCode} (expected 403)");
        }

        Assert.True(failures.Count == 0,
            $"RequiresPermission endpoints reachable for users without the permission:\n" +
            string.Join("\n", failures));
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private async Task<List<(string Method, string Path)>> DiscoverEndpointsAsync()
    {
        var specResponse = await Client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.True(specResponse.IsSuccessStatusCode,
            $"Could not fetch OpenAPI spec: {specResponse.StatusCode}");

        var specJson = await specResponse.Content.ReadAsStringAsync();
        var endpoints = ParseEndpoints(specJson);
        Assert.True(endpoints.Count > 0, "No endpoints found in OpenAPI spec");
        return endpoints;
    }

    private static async Task<HttpResponseMessage> SendProbe(HttpClient client, string method, string path, string? testBody = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), BuildTestUrl(path));
        if (method is "POST" or "PUT" or "PATCH")
        {
            var body = testBody ?? "{}";
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Replace path parameters with dummy values so the URL is routable.
    /// The actual value doesn't matter — we're testing the auth layer, not handlers.
    /// </summary>
    private static string BuildTestUrl(string path)
    {
        return path
            .Replace("{id}", "AAAAAAAAAAAAAAAAAAAAAA")
            .Replace("{linkId}", "AAAAAAAAAAAAAAAAAAAAAA")
            .Replace("{userId}", "AAAAAAAAAAAAAAAAAAAAAA")
            .Replace("{roleId}", Guid.Empty.ToString())
            .Replace("{grantId}", "AAAAAAAAAAAAAAAAAAAAAA")
            .Replace("{groupId}", "AAAAAAAAAAAAAAAAAAAAAA")
            .Replace("{type}", "todo")
            .Replace("{referenceId}", "AAAAAAAAAAAAAAAAAAAAAA")
            .Replace("{subTodoId}", "AAAAAAAAAAAAAAAAAAAAAA")
            .Replace("{parentTodoId}", "AAAAAAAAAAAAAAAAAAAAAA")
            .Replace("{userName}", "dummy")
            .Replace("{to}", "dummy@example.com");
    }

    private static List<(string Method, string Path)> ParseEndpoints(string openApiJson)
    {
        var endpoints = new List<(string, string)>();
        using var doc = JsonDocument.Parse(openApiJson);

        if (!doc.RootElement.TryGetProperty("paths", out var paths))
            return endpoints;

        foreach (var pathEntry in paths.EnumerateObject())
        {
            var path = pathEntry.Name;
            foreach (var methodEntry in pathEntry.Value.EnumerateObject())
            {
                var method = methodEntry.Name.ToUpperInvariant();
                if (method is "PARAMETERS" or "SUMMARY" or "DESCRIPTION")
                    continue;
                endpoints.Add((method, path));
            }
        }

        return endpoints;
    }
}
