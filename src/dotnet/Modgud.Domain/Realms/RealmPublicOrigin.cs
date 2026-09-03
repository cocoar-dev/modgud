namespace Modgud.Domain.Realms;

/// <summary>
/// The rule for a realm's public origin, in one place: it is <b>declared</b>
/// (<see cref="Realm.PublicBaseUrl"/>), never derived from the hosting
/// environment. Every outbound user-facing link is built against it, and it is
/// an accepted WebAuthn origin.
///
/// <para>It cannot live in <see cref="Realm.PrimaryDomain"/> because that is a
/// host NAME — it doubles as the WebAuthn RP ID and the cookie domain, neither
/// of which may carry a scheme or port. So the two are separate: the primary
/// domain says <i>which host this realm is</i>, the public origin says
/// <i>where users actually reach it</i>.</para>
///
/// <para>A realm without a declared origin — every realm created before the
/// field existed — falls back to <c>https://{PrimaryDomain}</c>, the standard
/// reverse-proxy-on-443 deployment.</para>
/// </summary>
public static class RealmPublicOrigin
{
    /// <summary>
    /// The realm's public base URL, without a trailing slash.
    /// </summary>
    /// <exception cref="InvalidOperationException">The realm declares no origin
    /// AND has no primary domain to fall back to — emitting a host-less
    /// "https://" link that reaches a user would be worse than failing here.</exception>
    public static string Resolve(Realm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);

        if (Normalize(realm.PublicBaseUrl) is { } declared) return declared;

        var host = (realm.PrimaryDomain ?? string.Empty).Trim();
        if (host.Length == 0)
            throw new InvalidOperationException(
                "Realm has no PrimaryDomain — cannot build a public URL. Every realm must have a primary domain.");
        return $"https://{host}";
    }

    /// <summary>
    /// Validates and canonicalizes a candidate origin: an absolute http(s) URL with
    /// no path, query or fragment. Returns it without a trailing slash, or null when
    /// the input is unusable (so callers can reject rather than silently fall back).
    /// </summary>
    public static string? Normalize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        if (uri.AbsolutePath.Trim('/').Length > 0) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }
}
