using Modgud.Infrastructure.OpenIddict.Cimd;

namespace Modgud.Tests.Unit.OAuth.Cimd;

/// <summary>
/// Pins the CIMD <c>client_id</c> URL contract
/// (draft-ietf-oauth-client-id-metadata-document §2): the discriminator that
/// decides whether an identifier is a CIMD URL at all, and the strict
/// pre-fetch validation.
/// </summary>
public class CimdClientIdTests
{
    public class IsCimdClientId
    {
        [Theory]
        [InlineData("https://claude.ai/oauth/client-metadata.json")]
        [InlineData("https://example.com/")]   // discriminator is loose (host only); strict shape is TryValidateUrl
        [InlineData("https://app.example.com:8443/mcp")]
        public void True_for_absolute_https_urls(string clientId) =>
            Assert.True(CimdClientId.IsCimdClientId(clientId));

        [Theory]
        [InlineData("dcr-2f1a9c0b")]            // a DCR client_id
        [InlineData("my-admin-client")]         // an opaque admin client_id
        [InlineData("http://example.com/x")]    // not https
        [InlineData("ftp://example.com/x")]
        [InlineData("not a url")]
        [InlineData("")]
        [InlineData(null)]
        public void False_for_non_https_or_opaque(string? clientId) =>
            Assert.False(CimdClientId.IsCimdClientId(clientId));
    }

    public class TryValidateUrl
    {
        [Fact]
        public void Accepts_a_well_formed_https_url_with_a_path()
        {
            var ok = CimdClientId.TryValidateUrl(
                "https://claude.ai/oauth/client-metadata.json", out var uri, out var error);
            Assert.True(ok);
            Assert.NotNull(uri);
            Assert.Null(error);
        }

        [Theory]
        [InlineData("http://example.com/meta")]            // not https
        [InlineData("https://example.com")]                // no path component
        [InlineData("https://example.com/")]               // path is just root
        [InlineData("https://user:pw@example.com/meta")]   // userinfo
        [InlineData("https://example.com/meta#frag")]      // fragment
        [InlineData("https://example.com/a/../b")]         // dot-segment
        [InlineData("https://example.com/./meta")]         // dot-segment
        [InlineData("not-a-url")]
        [InlineData("")]
        public void Rejects_violations(string clientId)
        {
            var ok = CimdClientId.TryValidateUrl(clientId, out var uri, out var error);
            Assert.False(ok);
            Assert.Null(uri);
            Assert.NotNull(error);
        }

        [Fact]
        public void Allows_a_query_string()
        {
            // A query is SHOULD-NOT per the draft, but not rejected.
            var ok = CimdClientId.TryValidateUrl(
                "https://example.com/meta?v=1", out _, out _);
            Assert.True(ok);
        }
    }
}
