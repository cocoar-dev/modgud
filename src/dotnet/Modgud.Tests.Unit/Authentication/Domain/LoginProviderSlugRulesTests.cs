using Modgud.Authentication.Domain.LoginProviders;

namespace Modgud.Tests.Unit.Authentication.Domain;

/// <summary>
/// Pins the login-provider slug grammar. The slug becomes part of the provider's
/// public URLs (<c>/signin-oidc/{slug}</c>, <c>/saml/{slug}/...</c>) and is the
/// SAML SP EntityID, so any drift here is a recreate-stability / federation
/// incident. Mirrors the realm-slug grammar but with a 3-64 char range and no
/// reserved-word list (the route shape prevents path collisions; the seeded
/// Internal slug is protected by per-realm uniqueness).
/// </summary>
public class LoginProviderSlugRulesTests
{
    [Theory]
    [InlineData("entra")]
    [InlineData("acme-entra")]
    [InlineData("a-b-c")]
    [InlineData("okta2")]
    [InlineData("a1b")]                                                  // shortest allowed: 3 chars
    [InlineData("internal")]                                            // the seeded provider's slug
    [InlineData("a23456789012345678901234567890123456789012345678901234567890abcd")] // 64 chars
    public void Accepts_well_formed_slugs(string slug) =>
        Assert.True(LoginProviderSlugRules.IsValidFormat(slug));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]                           // too short (3 min)
    [InlineData("1entra")]                        // must start with a letter
    [InlineData("-entra")]                        // must start with a letter
    [InlineData("entra-")]                        // must end alphanumeric
    [InlineData("Entra")]                         // uppercase not allowed
    [InlineData("acme_entra")]                    // underscore not allowed
    [InlineData("acme.entra")]                    // dot not allowed
    [InlineData("acme entra")]                    // space not allowed
    [InlineData("acme/entra")]                    // slash not allowed
    [InlineData("a234567890123456789012345678901234567890123456789012345678901abcde")] // 65 chars (over)
    public void Rejects_malformed_slugs(string? slug) =>
        Assert.False(LoginProviderSlugRules.IsValidFormat(slug));

    [Fact]
    public void Internal_slug_constant_is_valid_format() =>
        Assert.True(LoginProviderSlugRules.IsValidFormat(LoginProviderSlugRules.InternalSlug));
}
