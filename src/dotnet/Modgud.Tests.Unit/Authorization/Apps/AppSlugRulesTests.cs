using Modgud.Authorization.Apps;

namespace Modgud.Tests.Unit.Authorization.Apps;

/// <summary>
/// Pins <see cref="AppSlugRules"/>: the single-source-of-truth validator for
/// app slugs. Both the API endpoint and any UI form validation should agree
/// with these rules — drift here changes the realm's permission grammar.
/// </summary>
public class AppSlugRulesTests
{
    public class IsValidFormat
    {
        [Theory]
        [InlineData("acme-tasks")]
        [InlineData("foo")]                  // exactly 3 chars
        [InlineData("a1b")]                  // ends in digit
        [InlineData("my-app")]               // hyphen allowed in middle
        [InlineData("ab-cd-ef")]
        [InlineData("a23456789012345678901234567890123456789012345678901234567890123")] // 63 chars
        public void Returns_true_for_valid_slugs(string slug)
        {
            Assert.True(AppSlugRules.IsValidFormat(slug));
        }

        [Theory]
        [InlineData("")]                     // empty
        [InlineData(" ")]                    // whitespace
        [InlineData("ab")]                   // too short (2 chars)
        [InlineData("a234567890123456789012345678901234567890123456789012345678901234")] // 64 chars
        [InlineData("1abc")]                 // starts with digit
        [InlineData("-abc")]                 // starts with hyphen
        [InlineData("ABC")]                  // uppercase
        [InlineData("foo_bar")]              // underscore
        [InlineData("foo bar")]              // whitespace inside
        [InlineData("foo:bar")]              // colon (would break permission grammar)
        [InlineData("foo-")]                 // ends with hyphen
        [InlineData("foo.bar")]              // dot
        public void Returns_false_for_invalid_slugs(string slug)
        {
            Assert.False(AppSlugRules.IsValidFormat(slug));
        }

        [Fact]
        public void Returns_false_for_null()
        {
            Assert.False(AppSlugRules.IsValidFormat(null));
        }
    }

    public class IsReserved
    {
        [Fact]
        public void Realm_is_reserved()
        {
            // "realm" is the synthetic namespace for cross-app bypasses
            // ("realm:admin"). Allowing an app with slug "realm" would collide
            // with PermissionEvaluator's bypass rules.
            Assert.True(AppSlugRules.IsReserved("realm"));
        }

        [Fact]
        public void Wildcard_star_is_reserved()
        {
            // "*" means "active in every app" on Group.BoundTo. An app with
            // slug "*" would clash with that wildcard.
            Assert.True(AppSlugRules.IsReserved("*"));
        }

        [Fact]
        public void Cocoar_auth_is_reserved()
        {
            // The system app — seeded automatically, never created via the
            // admin API.
            Assert.True(AppSlugRules.IsReserved(AppSlugs.Modgud));
        }

        [Fact]
        public void Reserved_check_is_case_insensitive()
        {
            // Defence-in-depth: an admin can't sneak around the check by
            // typing "Realm" or "Modgud".
            Assert.True(AppSlugRules.IsReserved("Realm"));
            Assert.True(AppSlugRules.IsReserved("REALM"));
            Assert.True(AppSlugRules.IsReserved("Modgud"));
        }

        [Fact]
        public void Ordinary_slugs_are_not_reserved()
        {
            Assert.False(AppSlugRules.IsReserved("acme-tasks"));
            Assert.False(AppSlugRules.IsReserved("acme-policy"));
            Assert.False(AppSlugRules.IsReserved("my-app"));
        }

        [Fact]
        public void Returns_false_for_null()
        {
            Assert.False(AppSlugRules.IsReserved(null));
        }
    }
}
