using Modgud.Authentication.Api;

namespace Modgud.Tests.Unit.Authentication;

/// <summary>
/// Pinning tests for the shared open-redirect guard behind every server-side
/// post-login continuation (external-IdP returnUrl, SAML RelayState, magic-link
/// ReturnUrl). This guard is the only barrier between an attacker-controlled
/// continuation and a verbatim <c>Location</c> header, so every accept/reject
/// case — especially the control-char smuggling that a naive '//' check misses
/// — is pinned here.
/// </summary>
public class LoginRedirectGuardTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/dashboard")]
    [InlineData("/connect/authorize?response_type=code&client_id=x&scope=openid%20profile")]
    [InlineData("/path/with spaces")]        // literal space is not a control char; browser %-encodes it, no '//' collapse
    public void Accepts_same_origin_absolute_paths(string value)
    {
        Assert.True(LoginRedirectGuard.IsSameOriginPath(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dashboard")]                  // not rooted
    [InlineData("https://evil.com")]           // absolute external
    [InlineData("//evil.com")]                 // protocol-relative
    [InlineData("/\\evil.com")]                // backslash-smuggling
    [InlineData("/\t/evil.com")]               // TAB → browser strips → //evil.com
    [InlineData("/\n/evil.com")]               // LF
    [InlineData("/\r/evil.com")]               // CR
    [InlineData("/foo\tbar")]                  // embedded TAB anywhere
    public void Rejects_non_same_origin_or_control_char_values(string? value)
    {
        Assert.False(LoginRedirectGuard.IsSameOriginPath(value));
    }
}
