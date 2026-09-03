using Modgud.Domain.Realms;

namespace Modgud.Tests.Unit.Domain;

/// <summary>
/// The realm's public origin is DECLARED, never derived from the hosting
/// environment — that is the whole point of the field. These pin both halves of
/// the rule: a declared origin wins verbatim (port included), and a realm that
/// declares none falls back to the reverse-proxy-on-443 shape.
/// </summary>
public class RealmPublicOriginTests
{
    private static Realm RealmWith(string? publicBaseUrl, string primaryDomain = "auth.example.com")
        => new() { Slug = "r", PrimaryDomain = primaryDomain, PublicBaseUrl = publicBaseUrl };

    [Theory]
    [InlineData("https://auth.example.com")]
    [InlineData("http://localhost:4300")]
    [InlineData("http://localhost:8081")]
    [InlineData("https://auth.example.com:8443")]
    public void A_declared_origin_is_used_verbatim(string declared)
        => Assert.Equal(declared, RealmPublicOrigin.Resolve(RealmWith(declared)));

    [Fact]
    public void A_declared_origin_keeps_its_port_regardless_of_the_primary_domain()
    {
        // The operator installed on :4300 but the realm's canonical host — the
        // WebAuthn RP ID and cookie domain, which may carry neither scheme nor
        // port — is the bare name. Links must follow the declared origin.
        var realm = RealmWith("http://localhost:4300", primaryDomain: "localhost");
        Assert.Equal("http://localhost:4300", RealmPublicOrigin.Resolve(realm));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_declared_origin_it_falls_back_to_https_on_the_primary_domain(string? declared)
        => Assert.Equal("https://auth.example.com", RealmPublicOrigin.Resolve(RealmWith(declared)));

    [Fact]
    public void A_trailing_slash_is_normalized_away()
        => Assert.Equal("https://auth.example.com", RealmPublicOrigin.Resolve(RealmWith("https://auth.example.com/")));

    [Fact]
    public void A_realm_without_any_host_fails_loudly_instead_of_emitting_a_hostless_link()
        => Assert.Throws<InvalidOperationException>(
            () => RealmPublicOrigin.Resolve(RealmWith(null, primaryDomain: "")));

    [Theory]
    [InlineData("auth.example.com")]                 // no scheme
    [InlineData("ftp://auth.example.com")]           // not http(s)
    [InlineData("https://auth.example.com/base")]    // paths would silently vanish
    [InlineData("https://auth.example.com?x=1")]
    [InlineData("https://auth.example.com#frag")]
    [InlineData("not a url")]
    public void An_unusable_origin_is_rejected_rather_than_silently_ignored(string candidate)
        => Assert.Null(RealmPublicOrigin.Normalize(candidate));

    [Theory]
    [InlineData("https://auth.example.com/", "https://auth.example.com")]
    [InlineData("HTTP://Localhost:4300", "http://localhost:4300")]
    [InlineData("  https://auth.example.com  ", "https://auth.example.com")]
    public void A_usable_origin_is_canonicalized(string candidate, string expected)
        => Assert.Equal(expected, RealmPublicOrigin.Normalize(candidate));
}
