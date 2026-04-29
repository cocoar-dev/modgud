using System.Security.Claims;
using Cocoar.Auth.Api.Features.Auth.OAuth;
using Cocoar.Auth.Authentication.Domain;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cocoar.Auth.Tests.Unit.Api.Features.Auth.OAuth;

/// <summary>
/// Pinning tests for the OpenIddict claim-routing and userinfo-name helpers.
/// These two helpers are the only thing standing between "every claim leaks
/// into the id_token" and "scope-gated, security-stamp-suppressed". Drift here
/// is silent — RPs simply receive the wrong tokens.
/// </summary>
public class AuthorizationEndpointHelpersTests
{
    public class GetDisplayName
    {
        [Fact]
        public void Returns_username_when_no_first_or_last_name()
        {
            var user = new ApplicationUser { UserName = "alice" };
            Assert.Equal("alice", AuthorizationEndpointHelpers.GetDisplayName(user));
        }

        [Fact]
        public void Returns_first_and_last_name_joined_with_space()
        {
            var user = new ApplicationUser { UserName = "alice", Firstname = "Alice", Lastname = "Smith" };
            Assert.Equal("Alice Smith", AuthorizationEndpointHelpers.GetDisplayName(user));
        }

        [Fact]
        public void Trims_when_only_firstname_present()
        {
            // The implementation joins with a single space and trims; without trim
            // a firstname-only user would get "Alice " (trailing space) which then
            // shows up in id_token claims and breaks UI alignment.
            var user = new ApplicationUser { UserName = "alice", Firstname = "Alice" };
            Assert.Equal("Alice", AuthorizationEndpointHelpers.GetDisplayName(user));
        }

        [Fact]
        public void Trims_when_only_lastname_present()
        {
            var user = new ApplicationUser { UserName = "alice", Lastname = "Smith" };
            Assert.Equal("Smith", AuthorizationEndpointHelpers.GetDisplayName(user));
        }

        [Fact]
        public void Treats_empty_string_first_or_last_as_absent()
        {
            // ApplicationUser fields are nullable but Identity sometimes hands
            // back "" — both should fall through to the username branch instead
            // of returning a single-space string.
            var user = new ApplicationUser { UserName = "alice", Firstname = "", Lastname = "" };
            Assert.Equal("alice", AuthorizationEndpointHelpers.GetDisplayName(user));
        }
    }

    public class GetDestinations
    {
        // Helper: build a Claim attached to a ClaimsIdentity that already carries
        // the scope claim. Two subtleties pinned here:
        //   1. <see cref="ClaimsIdentity.AddClaim"/> CLONES the claim (the original
        //      reference's <c>Subject</c> stays null) and the clone's Subject is set
        //      to the identity. We must return the cloned reference, not our pre-add
        //      object, so production's <c>claim.Subject?.HasScope</c> resolves.
        //   2. We call <c>identity.SetScopes</c> directly — using
        //      <c>principal.SetScopes</c> stores scopes on the principal's primary
        //      identity which (in some setups) is a different ClaimsIdentity than
        //      the one carrying our test claim, so <c>claim.Subject.HasScope</c>
        //      would silently miss them.
        private static Claim ClaimWithScopes(string type, string value, params string[] scopes)
        {
            var identity = new ClaimsIdentity("test");
            identity.AddClaim(new Claim(type, value));
            if (scopes.Length > 0) identity.SetScopes(scopes);
            return identity.FindFirst(type)!;
        }

        [Fact]
        public void Probe_HasScope_resolution_through_claim_subject()
        {
            // Sanity-pin: the production helper relies on <c>claim.Subject?.HasScope(scope)</c>.
            // Our <see cref="ClaimWithScopes"/> setup must produce a claim whose Subject
            // is an identity with the scope registered, otherwise the scope-gated
            // tests below would silently always-fail-closed.
            var c = ClaimWithScopes(Claims.Email, "a@b", Scopes.Email);

            Assert.NotNull(c.Subject);
            Assert.True(c.Subject!.HasScope(Scopes.Email));
            Assert.False(c.Subject!.HasScope(Scopes.Profile));
        }

        [Fact]
        public void Name_claim_goes_to_access_token_only_without_profile_scope()
        {
            var c = ClaimWithScopes(Claims.Name, "Alice");
            Assert.Equal(new[] { Destinations.AccessToken },
                AuthorizationEndpointHelpers.GetDestinations(c).ToArray());
        }

        [Fact]
        public void Name_claim_goes_to_both_tokens_when_profile_scope_present()
        {
            var c = ClaimWithScopes(Claims.Name, "Alice", Scopes.Profile);
            Assert.Equal(new[] { Destinations.AccessToken, Destinations.IdentityToken },
                AuthorizationEndpointHelpers.GetDestinations(c).ToArray());
        }

        [Fact]
        public void PreferredUsername_follows_same_rules_as_name()
        {
            var withProfile = ClaimWithScopes(Claims.PreferredUsername, "alice", Scopes.Profile);
            var withoutProfile = ClaimWithScopes(Claims.PreferredUsername, "alice");

            Assert.Contains(Destinations.IdentityToken,
                AuthorizationEndpointHelpers.GetDestinations(withProfile));
            Assert.DoesNotContain(Destinations.IdentityToken,
                AuthorizationEndpointHelpers.GetDestinations(withoutProfile));
        }

        [Fact]
        public void Email_claim_only_lands_in_id_token_when_email_scope_granted()
        {
            var withEmail = ClaimWithScopes(Claims.Email, "a@x", Scopes.Email);
            var withoutEmail = ClaimWithScopes(Claims.Email, "a@x");

            Assert.Contains(Destinations.IdentityToken,
                AuthorizationEndpointHelpers.GetDestinations(withEmail));
            Assert.DoesNotContain(Destinations.IdentityToken,
                AuthorizationEndpointHelpers.GetDestinations(withoutEmail));
        }

        [Fact]
        public void Role_claim_only_lands_in_id_token_when_roles_scope_granted()
        {
            var withRoles = ClaimWithScopes(Claims.Role, "Admin", Scopes.Roles);
            var withoutRoles = ClaimWithScopes(Claims.Role, "Admin");

            Assert.Contains(Destinations.IdentityToken,
                AuthorizationEndpointHelpers.GetDestinations(withRoles));
            Assert.DoesNotContain(Destinations.IdentityToken,
                AuthorizationEndpointHelpers.GetDestinations(withoutRoles));
        }

        [Fact]
        public void Profile_scope_does_not_leak_email_into_id_token()
        {
            // Each scope must guard its own claim type. A profile scope must NOT
            // unlock the email claim into the id_token.
            var c = ClaimWithScopes(Claims.Email, "a@x", Scopes.Profile);
            Assert.Equal(new[] { Destinations.AccessToken },
                AuthorizationEndpointHelpers.GetDestinations(c).ToArray());
        }

        [Fact]
        public void SecurityStamp_claim_is_suppressed_from_both_tokens()
        {
            // ASP.NET Identity attaches an internal "AspNet.Identity.SecurityStamp"
            // claim that must never reach a relying party.
            var c = ClaimWithScopes("AspNet.Identity.SecurityStamp", "stamp",
                Scopes.OpenId, Scopes.Profile, Scopes.Email, Scopes.Roles);
            Assert.Empty(AuthorizationEndpointHelpers.GetDestinations(c));
        }

        [Fact]
        public void Unknown_claim_types_default_to_access_token_only()
        {
            // The default branch is the safety net for custom app claims —
            // they end up in the access_token and never the id_token.
            var c = ClaimWithScopes("custom_app_claim", "v",
                Scopes.OpenId, Scopes.Profile, Scopes.Email);
            Assert.Equal(new[] { Destinations.AccessToken },
                AuthorizationEndpointHelpers.GetDestinations(c).ToArray());
        }

        [Fact]
        public void Detached_claim_without_subject_still_resolves_to_access_token()
        {
            // Defensive: the helper guards `claim.Subject?` with null-conditional
            // — a Claim built without a ClaimsIdentity should still work and never
            // throw NullReferenceException.
            var c = new Claim(Claims.Name, "alice");
            var dests = AuthorizationEndpointHelpers.GetDestinations(c).ToArray();
            Assert.Equal(new[] { Destinations.AccessToken }, dests);
        }
    }
}
