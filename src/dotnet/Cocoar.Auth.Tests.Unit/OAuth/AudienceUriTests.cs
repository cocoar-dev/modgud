using Cocoar.Auth.Domain.OAuth.Common;

namespace Cocoar.Auth.Tests.Unit.OAuth;

/// <summary>
/// Pins the audience-identifier validation rules per RFC 7519 §2:
/// arbitrary strings are valid, but any value containing a colon
/// MUST be a valid absolute URI per RFC 3986. Bare identifiers without
/// colons stay legal — this is the StringOrURI shape, not a stricter
/// "must always be a URI" reading.
/// </summary>
public class AudienceUriTests
{
    [Theory]
    [InlineData("alpha-blog-api")]                  // bare slug
    [InlineData("MyAudience")]                      // mixed case
    [InlineData("api_v1")]                          // underscore + digits
    [InlineData("/relative/path")]                  // path-shaped but no colon → still bare-string ok
    [InlineData("https://api.example.com")]         // https URI
    [InlineData("https://api.example.com/v1")]      // https URI with path
    [InlineData("urn:example:my-api")]              // urn scheme
    [InlineData("https://example.com/#fragment")]   // RFC 7519 §2 doesn't ban fragments — RFC 8707 does, separate concern
    public void Accepts_bare_strings_and_valid_absolute_URIs(string value)
    {
        var ok = AudienceUri.TryValidate(value, out var error);
        Assert.True(ok, $"Expected '{value}' to validate but got: {error}");
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null,            "must not be empty")]
    [InlineData("",              "must not be empty")]
    [InlineData("   ",           "must not be empty")]
    [InlineData("alpha api",     "whitespace")]            // bare-string with space
    [InlineData("foo bar:baz",   "whitespace")]            // space in scheme position
    [InlineData("https://a b.c", "whitespace")]            // space inside URI host
    [InlineData(":foo",          "valid absolute URI")]    // empty scheme
    [InlineData("1foo:bar",      "valid absolute URI")]    // scheme must start with a letter
    public void Rejects_invalid_inputs(string? value, string expectedFragment)
    {
        var ok = AudienceUri.TryValidate(value, out var error);
        Assert.False(ok, $"Expected '{value}' to be rejected.");
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Colon_with_only_scheme_is_accepted_per_dotnet_uri_parser()
    {
        // .NET's Uri.TryCreate treats "foo:" as scheme="foo", empty
        // hier-part — technically RFC-3986-valid (a "URI reference"
        // with empty path). We follow the parser; if a tighter rule
        // is ever needed, layer it on top.
        Assert.True(AudienceUri.TryValidate("foo:", out var error), error);
        Assert.True(AudienceUri.TryValidate("foo:bar:baz", out _));
    }
}
