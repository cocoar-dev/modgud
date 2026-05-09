namespace Cocoar.Auth.Domain.OAuth.Common;

/// <summary>
/// Validation rules for audience identifiers — values that end up in
/// the JWT <c>aud</c> claim. Used for both <c>OAuthApi.Name</c> (the
/// resource server's own identifier) and <c>OAuthScope.Resources[]</c>
/// (the audiences a scope implicitly grants access to).
///
/// <para>Per <strong>RFC 7519 §2</strong>, a <c>StringOrURI</c> may be
/// either:</para>
/// <list type="bullet">
///   <item>An arbitrary string, <em>provided it does not contain a
///   colon</em> (<c>:</c>) — bare identifiers like <c>"alpha-blog-api"</c>
///   are perfectly legal aud values.</item>
///   <item>If the value contains a colon, it MUST be a valid absolute
///   URI per RFC 3986 — e.g. <c>"https://api.example.com"</c> or
///   <c>"urn:example:my-api"</c>. The check uses <c>Uri.TryCreate</c>
///   with <c>UriKind.Absolute</c>; values like <c>":foo"</c> or
///   <c>"1foo:bar"</c> (scheme must start with a letter) are rejected.</item>
/// </list>
///
/// <para>Whitespace is rejected outright in either form — neither RFC
/// 3986 (URIs must percent-encode spaces) nor RFC 7519 sensibly allow
/// it, even though <c>Uri.TryCreate</c> happens to accept whitespace
/// in the path component.</para>
///
/// <para><strong>Note on RFC 8707:</strong> if a client passes the
/// audience as a <c>resource=</c> parameter, RFC 8707 §2 separately
/// requires it to be an absolute URI without a fragment. Whether to
/// pre-empt that here is a separate (stricter) policy choice; the
/// validation in this helper sticks to plain RFC 7519 §2 so bare
/// identifiers stay legal.</para>
/// </summary>
public static class AudienceUri
{
    public static bool TryValidate(string? value, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Audience identifier must not be empty.";
            return false;
        }

        if (value.Any(char.IsWhiteSpace))
        {
            error = $"Audience identifier '{value}' must not contain whitespace.";
            return false;
        }

        // No colon → bare-string aud, legal per RFC 7519 §2 with no
        // further constraints. Accept as-is.
        if (!value.Contains(':'))
        {
            error = null;
            return true;
        }

        // Has colon → MUST be a valid absolute URI per RFC 3986.
        if (!Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            error = $"Audience identifier '{value}' contains a colon and must therefore " +
                    "be a valid absolute URI per RFC 7519 §2 (e.g. \"https://api.example.com\" " +
                    "or \"urn:example:my-api\"). To use a bare identifier, drop the colon.";
            return false;
        }

        error = null;
        return true;
    }
}
