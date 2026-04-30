using System.Security.Claims;
using Cocoar.Auth.Client.AspNetCore;
using Microsoft.Extensions.Options;

namespace Cocoar.Auth.Tests.Unit.Client.AspNetCore;

/// <summary>
/// Pins the resource-server-side claims-transformation. The contract: when
/// the IDP issues a Keycloak-style <c>resource_access[appSlug].roles</c>
/// claim, our transformation flattens it into <c>ClaimTypes.Role</c> claims
/// so <c>[Authorize(Roles="...")]</c> works without per-endpoint plumbing.
/// </summary>
public class CocoarAuthClaimsTransformationTests
{
    private const string AppSlug = "timetodo";

    private static CocoarAuthClaimsTransformation NewSubject() =>
        new(Options.Create(new CocoarAuthOptions { AppSlug = AppSlug }));

    private static ClaimsPrincipal NewAuthenticatedPrincipal(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }

    public class Roles
    {
        [Fact]
        public async Task Flattens_appslug_roles_into_ClaimTypes_Role()
        {
            // Given: the IDP issued a resource_access claim with our app's roles.
            var resourceAccess = """
                {
                  "timetodo":  { "roles": ["Admin", "Editor"] },
                  "knowledge": { "roles": ["Viewer"] }
                }
                """;
            var principal = NewAuthenticatedPrincipal(new Claim("resource_access", resourceAccess));

            // When: the transformation runs.
            var transformed = await NewSubject().TransformAsync(principal);

            // Then: the timetodo roles are added as ClaimTypes.Role and the
            // knowledge roles are NOT (they belong to a different resource server).
            var roles = transformed.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
            Assert.Contains("Admin", roles);
            Assert.Contains("Editor", roles);
            Assert.DoesNotContain("Viewer", roles);
        }

        [Fact]
        public async Task Other_apps_in_resource_access_do_not_leak()
        {
            // Defence-in-depth: only OUR app's block contributes. A
            // misbehaving IDP that puts "Admin" under another app must
            // not give the user admin rights here.
            var resourceAccess = """{ "knowledge": { "roles": ["Admin"] } }""";
            var principal = NewAuthenticatedPrincipal(new Claim("resource_access", resourceAccess));

            var transformed = await NewSubject().TransformAsync(principal);

            Assert.Empty(transformed.FindAll(ClaimTypes.Role));
        }

        [Fact]
        public async Task Missing_resource_access_is_a_no_op()
        {
            var principal = NewAuthenticatedPrincipal(new Claim(ClaimTypes.NameIdentifier, "user-1"));

            var transformed = await NewSubject().TransformAsync(principal);

            Assert.Empty(transformed.FindAll(ClaimTypes.Role));
        }

        [Fact]
        public async Task Malformed_resource_access_json_is_ignored_silently()
        {
            // Don't throw mid-request — that would 500 every endpoint
            // for a cosmetic IDP misconfiguration.
            var principal = NewAuthenticatedPrincipal(new Claim("resource_access", "this is not json"));

            var transformed = await NewSubject().TransformAsync(principal);

            Assert.Empty(transformed.FindAll(ClaimTypes.Role));
        }

        [Fact]
        public async Task App_block_without_roles_array_is_ignored()
        {
            var resourceAccess = """{ "timetodo": { "permissions": ["x"] } }""";
            var principal = NewAuthenticatedPrincipal(new Claim("resource_access", resourceAccess));

            var transformed = await NewSubject().TransformAsync(principal);

            Assert.Empty(transformed.FindAll(ClaimTypes.Role));
        }

        [Fact]
        public async Task Idempotent_double_run_does_not_duplicate_roles()
        {
            // ClaimsTransformation runs on every request, often more than
            // once per request when the principal is rebuilt. Duplicate
            // role claims would multiply policy checks and bloat logs.
            var resourceAccess = """{ "timetodo": { "roles": ["Admin"] } }""";
            var principal = NewAuthenticatedPrincipal(new Claim("resource_access", resourceAccess));
            var subject = NewSubject();

            await subject.TransformAsync(principal);
            await subject.TransformAsync(principal);

            Assert.Single(principal.FindAll(ClaimTypes.Role));
        }

        [Fact]
        public async Task Anonymous_principal_short_circuits()
        {
            // No identity / unauthenticated — nothing to flatten.
            var anon = new ClaimsPrincipal(new ClaimsIdentity());

            var transformed = await NewSubject().TransformAsync(anon);

            Assert.Empty(transformed.FindAll(ClaimTypes.Role));
        }
    }

    public class Groups
    {
        [Fact]
        public async Task Flattens_groups_array_into_group_claims()
        {
            var groups = """["DevOps", "Mitarbeiter"]""";
            var principal = NewAuthenticatedPrincipal(new Claim("groups", groups));

            var transformed = await NewSubject().TransformAsync(principal);

            var resolved = transformed.FindAll("group").Select(c => c.Value).ToList();
            Assert.Equal(2, resolved.Count);
            Assert.Contains("DevOps", resolved);
            Assert.Contains("Mitarbeiter", resolved);
        }

        [Fact]
        public async Task Missing_groups_is_a_no_op()
        {
            var principal = NewAuthenticatedPrincipal(new Claim(ClaimTypes.NameIdentifier, "user-1"));

            var transformed = await NewSubject().TransformAsync(principal);

            Assert.Empty(transformed.FindAll("group"));
        }
    }

    public class Configuration
    {
        [Fact]
        public void Constructor_throws_when_AppSlug_is_missing()
        {
            // Configuration mistake — fail fast at startup so it's caught
            // before any request.
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new CocoarAuthClaimsTransformation(Options.Create(new CocoarAuthOptions { AppSlug = "" })));

            Assert.Contains("AppSlug", ex.Message);
        }
    }
}
