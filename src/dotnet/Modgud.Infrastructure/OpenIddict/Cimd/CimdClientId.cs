namespace Modgud.Infrastructure.OpenIddict.Cimd;

/// <summary>
/// Pure helpers for the CIMD <c>client_id</c> URL contract
/// (<c>draft-ietf-oauth-client-id-metadata-document</c> §2). Kept
/// dependency-free so the rules are unit-testable without HTTP or Marten.
///
/// <para>A CIMD <c>client_id</c> <em>is</em> an <c>https</c> URL: it must
/// have a path component, and MUST NOT contain a fragment, userinfo, or any
/// dot-segments. A port is allowed; a query is discouraged (SHOULD-NOT) but
/// not rejected. Normal/DCR <c>client_id</c>s are opaque strings
/// (<c>dcr-…</c>, admin-chosen tokens) and never parse as absolute URLs, so
/// <see cref="IsCimdClientId"/> is a safe, cheap discriminator used at every
/// CIMD-aware call site.</para>
/// </summary>
public static class CimdClientId
{
    /// <summary>
    /// Cheap discriminator: does this <c>client_id</c> look like a CIMD URL
    /// (absolute <c>https</c> with a host)? Used by the token-pipeline
    /// handlers and consent endpoint to branch without a DB read. Strict
    /// shape validation (<see cref="TryValidateUrl"/>) runs in the resolver
    /// before any fetch.
    /// </summary>
    public static bool IsCimdClientId(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId)) return false;
        return Uri.TryCreate(clientId, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(uri.Host);
    }

    /// <summary>
    /// Full draft-spec validation, run before the metadata fetch. Returns
    /// the parsed <see cref="Uri"/> on success; on failure sets a
    /// machine-stable reason describing which rule was violated.
    /// </summary>
    public static bool TryValidateUrl(string? clientId, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (string.IsNullOrWhiteSpace(clientId))
        {
            error = "client_id is empty.";
            return false;
        }

        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var parsed))
        {
            error = "client_id is not an absolute URI.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "client_id must use the https scheme.";
            return false;
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            error = "client_id must have a host.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "client_id must not contain userinfo.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "client_id must not contain a fragment.";
            return false;
        }

        // "MUST contain a path component" — a bare authority (path "/") is
        // not a meaningful document location.
        if (parsed.AbsolutePath.Length <= 1)
        {
            error = "client_id must contain a path component.";
            return false;
        }

        // "MUST NOT contain any dot-segments." Uri normalises them away, so
        // inspect the raw input rather than the canonicalised path.
        if (ContainsDotSegment(clientId))
        {
            error = "client_id must not contain dot-segments (./ or ../).";
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool ContainsDotSegment(string raw)
    {
        // Only look at the path portion (strip scheme://authority and any
        // query/fragment) so a literal ".." inside a query value can't
        // trip the check.
        var schemeEnd = raw.IndexOf("://", StringComparison.Ordinal);
        var start = schemeEnd < 0 ? 0 : schemeEnd + 3;
        var authorityEnd = raw.IndexOf('/', start);
        if (authorityEnd < 0) return false;
        var afterPath = raw.IndexOfAny(['?', '#'], authorityEnd);
        var path = afterPath < 0 ? raw[authorityEnd..] : raw[authorityEnd..afterPath];

        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment == "." || segment == "..") return true;
        }
        return false;
    }
}
