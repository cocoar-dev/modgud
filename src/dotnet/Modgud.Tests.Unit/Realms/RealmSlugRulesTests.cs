using Modgud.Domain.Realms;

namespace Modgud.Tests.Unit.Realms;

/// <summary>
/// Pins the realm-slug grammar. Slugs become PostgreSQL DB names (<c>{mainDb}_{slug}</c>)
/// and URL hostnames (<c>{slug}.localhost</c>), so any drift here is a backwards-
/// compatibility incident.
/// </summary>
public class RealmSlugRulesTests
{
    public class IsValidFormat
    {
        [Theory]
        [InlineData("acme")]
        [InlineData("alpine")]
        [InlineData("a-c-m-e")]
        [InlineData("acme123")]
        [InlineData("a1b")]                                                // shortest still allowed: 3 chars
        [InlineData("a-1")]
        [InlineData("a23456789012345678901234567890123456789012345678901234567890123")] // 63 chars total
        public void Accepts_well_formed_slugs(string slug) =>
            Assert.True(RealmSlugRules.IsValidFormat(slug));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("a")]                            // too short
        [InlineData("ab")]                           // still too short (3 char min)
        [InlineData("1acme")]                        // must start with a letter
        [InlineData("-acme")]                        // must start with a letter
        [InlineData("acme-")]                        // must end with letter or digit
        [InlineData("Acme")]                         // uppercase not allowed
        [InlineData("acme_thing")]                   // underscore not allowed
        [InlineData("acme.thing")]                   // dot not allowed
        [InlineData("acme thing")]                   // space not allowed
        [InlineData("acme/thing")]                   // slash not allowed
        [InlineData("a234567890123456789012345678901234567890123456789012345678901234")] // 64 chars (over)
        public void Rejects_malformed_slugs(string? slug) =>
            Assert.False(RealmSlugRules.IsValidFormat(slug));
    }

    public class IsReserved
    {
        [Theory]
        [InlineData("system")]
        [InlineData("health")]
        [InlineData("swagger")]
        [InlineData("openapi")]
        [InlineData("_framework")]
        public void Rejects_canonical_reserved_names(string slug) =>
            Assert.True(RealmSlugRules.IsReserved(slug));

        [Theory]
        [InlineData("System")]
        [InlineData("HEALTH")]
        [InlineData("Swagger")]
        public void Reservation_check_is_case_insensitive(string slug) =>
            Assert.True(RealmSlugRules.IsReserved(slug));

        [Theory]
        [InlineData("acme")]
        [InlineData("systems")]                  // plural is fine
        [InlineData("system-acme")]              // suffix is fine
        public void Allows_non_reserved_names(string slug) =>
            Assert.False(RealmSlugRules.IsReserved(slug));

        [Fact]
        public void Null_is_not_reserved() =>
            Assert.False(RealmSlugRules.IsReserved(null));

        [Fact]
        public void Reserved_set_is_not_empty() =>
            Assert.NotEmpty(RealmSlugRules.ReservedSlugs);
    }
}
