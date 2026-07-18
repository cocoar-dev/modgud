using System.Security.Claims;
using Modgud.Client.AspNetCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Modgud.Tests.Unit.Client.AspNetCore;

/// <summary>
/// Pins the resource-server-side claims-transformation: it reads the
/// <c>resource_access</c> claim that the JWT-bearer middleware populated
/// (from the JWT itself or via UserInfo) and projects the configured
/// audience's block onto the principal as flat ClaimTypes.Role /
/// "permission" claims. Groups are NEVER flattened — the IdP never emits
/// a <c>groups</c> block (hub boundary, federation v1).
///
/// <para>The IdP pre-expands bypass tiers, so the lib is a pure
/// claims-flattener — no HTTP, no cache, no evaluator.</para>
/// </summary>
public class ModgudClaimsTransformationTests
{
    private const string Audience = "https://policy-api.cocoar.dev";

    private static ModgudClaimsTransformation NewSubject(string audience = Audience) =>
        new(Options.Create(new ModgudOptions { Audience = audience }));

    private static ClaimsPrincipal NewAuthenticatedPrincipal(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }

    private static Claim ResourceAccessClaim(string raw) =>
        new(ModgudClaimsTransformation.ResourceAccessClaimType, raw);

    public class Roles
    {
        [Fact]
        public async Task Flattens_audience_block_roles_into_ClaimTypes_Role()
        {
            // Standard happy path — UserInfo emitted a per-audience block
            // and we have the matching audience configured.
            var resourceAccess = $$"""
                {
                  "{{Audience}}": {
                    "permissions": [],
                    "roles":       ["Editor", "Viewer"],
                    "groups":      []
                  },
                  "https://other-api.example.com": {
                    "roles": ["ShouldNotLeak"]
                  }
                }
                """;
            var principal = NewAuthenticatedPrincipal(ResourceAccessClaim(resourceAccess));

            var transformed = await NewSubject().TransformAsync(principal);

            var roles = transformed.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
            Assert.Contains("Editor", roles);
            Assert.Contains("Viewer", roles);
            Assert.DoesNotContain("ShouldNotLeak", roles);
        }

        [Fact]
        public async Task Other_audiences_in_resource_access_do_not_leak()
        {
            // Defence-in-depth: only OUR audience block contributes.
            var resourceAccess = """{ "https://other-api.example.com": { "roles": ["Admin"] } }""";
            var principal = NewAuthenticatedPrincipal(ResourceAccessClaim(resourceAccess));

            var transformed = await NewSubject().TransformAsync(principal);

            Assert.Empty(transformed.FindAll(ClaimTypes.Role));
        }

        [Fact]
        public async Task Idempotent_double_run_does_not_duplicate_roles()
        {
            // ClaimsTransformation runs more than once per pipeline pass.
            var resourceAccess = $$"""{ "{{Audience}}": { "roles": ["Editor"] } }""";
            var principal = NewAuthenticatedPrincipal(ResourceAccessClaim(resourceAccess));
            var subject = NewSubject();

            await subject.TransformAsync(principal);
            await subject.TransformAsync(principal);

            Assert.Single(principal.FindAll(ClaimTypes.Role));
        }
    }

    public class Permissions
    {
        [Fact]
        public async Task Flattens_audience_block_permissions_into_permission_claims()
        {
            var resourceAccess = $$"""
                {
                  "{{Audience}}": {
                    "permissions": ["policy:read", "policy:write"],
                    "roles":       [],
                    "groups":      []
                  }
                }
                """;
            var principal = NewAuthenticatedPrincipal(ResourceAccessClaim(resourceAccess));

            var transformed = await NewSubject().TransformAsync(principal);

            var permissions = transformed
                .FindAll(ModgudClaimsTransformation.PermissionClaimType)
                .Select(c => c.Value)
                .ToList();
            Assert.Contains("policy:read", permissions);
            Assert.Contains("policy:write", permissions);
        }
    }

    public class Groups
    {
        [Fact]
        public async Task Groups_block_is_never_flattened_hub_boundary()
        {
            // Federation v1 hub boundary: the Modgud IdP never emits a "groups"
            // block in resource_access (membership is IdP-internal, expanded into
            // roles/permissions before emission). Even if some upstream put one
            // there, the transformer must NOT surface "group" claims.
            var resourceAccess = $$"""
                {
                  "{{Audience}}": {
                    "permissions": [],
                    "roles":       [],
                    "groups":      [
                      { "id": "g-1", "name": "DevOps" },
                      { "id": "g-2", "name": "Mitarbeiter" }
                    ]
                  }
                }
                """;
            var principal = NewAuthenticatedPrincipal(ResourceAccessClaim(resourceAccess));

            var transformed = await NewSubject().TransformAsync(principal);

            // "group" is the quarantined GroupClaimType value — assert via the
            // literal so the test itself doesn't reference the [Obsolete] symbol.
            Assert.Empty(transformed.FindAll("group"));
        }
    }

    public class ShortCircuits
    {
        [Fact]
        public async Task Anonymous_principal_is_left_untouched()
        {
            var anon = new ClaimsPrincipal(new ClaimsIdentity());

            var transformed = await NewSubject().TransformAsync(anon);

            Assert.Empty(transformed.FindAll(ClaimTypes.Role));
            Assert.Empty(transformed.FindAll(ModgudClaimsTransformation.PermissionClaimType));
        }

        [Fact]
        public async Task Missing_resource_access_claim_is_a_no_op()
        {
            // Pure-auth tokens (no roles scope, etc.) won't have it. Bail
            // gracefully rather than throwing.
            var principal = NewAuthenticatedPrincipal(new Claim(ClaimTypes.NameIdentifier, "user-1"));

            var transformed = await NewSubject().TransformAsync(principal);

            Assert.Empty(transformed.FindAll(ModgudClaimsTransformation.PermissionClaimType));
        }

        [Fact]
        public async Task Malformed_resource_access_json_is_ignored()
        {
            // Don't throw mid-request — that would 500 every endpoint
            // for a cosmetic IDP misconfiguration.
            var principal = NewAuthenticatedPrincipal(ResourceAccessClaim("this is not json"));

            var transformed = await NewSubject().TransformAsync(principal);

            Assert.Empty(transformed.FindAll(ModgudClaimsTransformation.PermissionClaimType));
        }

        [Fact]
        public async Task Configured_audience_not_in_resource_access_is_a_no_op()
        {
            // A token whose aud[] doesn't include this RS still authenticated
            // (signature/issuer valid). It just doesn't grant any of OUR
            // permissions — caller's [Authorize] / RequiresModgudPermission
            // will then return 403 cleanly.
            var resourceAccess = """{ "https://other-api.example.com": { "permissions": ["policy:read"] } }""";
            var principal = NewAuthenticatedPrincipal(ResourceAccessClaim(resourceAccess));

            var transformed = await NewSubject().TransformAsync(principal);

            Assert.Empty(transformed.FindAll(ModgudClaimsTransformation.PermissionClaimType));
        }
    }

    public class Configuration
    {
        [Fact]
        public void Constructor_throws_when_Audience_is_missing()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new ModgudClaimsTransformation(Options.Create(new ModgudOptions { Audience = "" })));
            Assert.Contains("Audience", ex.Message);
        }
    }

    /// <summary>
    /// Issue #116 (Option A): since the access token now carries
    /// <c>resource_access</c> itself, the transformer must flatten it
    /// identically regardless of which path put the claim on the identity.
    /// ASP.NET Core's JwtBearer (JsonWebTokenHandler) maps a JSON-object JWT
    /// payload property to a claim whose <c>Value</c> is the raw JSON text
    /// and whose <c>ValueType</c> is <see cref="JsonClaimValueTypes.Json"/> —
    /// confirmed empirically (CreateToken + ValidateTokenAsync round-trip)
    /// rather than assumed. These tests source the claim that way instead of
    /// the enricher's plain-string shape and expect the exact same output as
    /// the mirrored <see cref="Roles"/> / <see cref="Permissions"/> tests
    /// above — the transformer only ever reads <see cref="Claim.Value"/>, so
    /// <c>ValueType</c> must be irrelevant to it.
    /// </summary>
    public class TokenEmbeddedClaimShape
    {
        private static Claim TokenMappedResourceAccessClaim(string raw) =>
            new(ModgudClaimsTransformation.ResourceAccessClaimType, raw, JsonClaimValueTypes.Json);

        [Fact]
        public async Task Flattens_audience_block_roles_identically_to_the_userinfo_shaped_claim()
        {
            var resourceAccess = $$"""{ "{{Audience}}": { "roles": ["Editor", "Viewer"] } }""";
            var principal = NewAuthenticatedPrincipal(TokenMappedResourceAccessClaim(resourceAccess));

            var transformed = await NewSubject().TransformAsync(principal);

            var roles = transformed.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
            Assert.Contains("Editor", roles);
            Assert.Contains("Viewer", roles);
        }

        [Fact]
        public async Task Flattens_audience_block_permissions_identically_to_the_userinfo_shaped_claim()
        {
            var resourceAccess = $$"""{ "{{Audience}}": { "permissions": ["policy:read", "policy:write"] } }""";
            var principal = NewAuthenticatedPrincipal(TokenMappedResourceAccessClaim(resourceAccess));

            var transformed = await NewSubject().TransformAsync(principal);

            var permissions = transformed
                .FindAll(ModgudClaimsTransformation.PermissionClaimType)
                .Select(c => c.Value)
                .ToList();
            Assert.Contains("policy:read", permissions);
            Assert.Contains("policy:write", permissions);
        }
    }
}
