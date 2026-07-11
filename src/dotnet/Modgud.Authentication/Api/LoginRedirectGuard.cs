using System.Linq;

namespace Modgud.Authentication.Api;

/// <summary>
/// Open-redirect guard shared by every server-side post-login continuation
/// (external-IdP <c>returnUrl</c>, SAML RelayState, magic-link ReturnUrl). A
/// continuation is only honored when it is a same-origin absolute path — the
/// value is later emitted verbatim into a <c>Location</c> header or appended to
/// an e-mailed URL, so it must not be able to leave our origin.
/// </summary>
public static class LoginRedirectGuard
{
    /// <summary>
    /// True only for a same-origin absolute path: it must start with a single
    /// '/', and must not be protocol-relative ('//…'), backslash-smuggled
    /// ('/\…'), or contain any ASCII control character. The control-char check
    /// is load-bearing: a browser strips TAB/CR/LF while resolving a redirect,
    /// so a value like <c>/\t/evil.com</c> — which passes a naive '//' check —
    /// collapses to <c>//evil.com</c> (protocol-relative → external host) in the
    /// browser's URL parser. A well-formed, single-decoded path never carries a
    /// raw control character, so rejecting them costs nothing legitimate.
    /// </summary>
    public static bool IsSameOriginPath(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value[0] != '/') return false;                                    // must be absolute path
        if (value.StartsWith("//", StringComparison.Ordinal)) return false;   // protocol-relative
        if (value.StartsWith("/\\", StringComparison.Ordinal)) return false;  // backslash-smuggling
        if (value.Any(char.IsControl)) return false;                          // TAB/CR/LF etc. strip to '//'
        return true;
    }
}
