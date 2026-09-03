using Modgud.Domain.Realms;

namespace Modgud.Authentication;

/// <summary>
/// Single source of truth for a realm's public base URL — the origin every
/// outbound user-facing link is built against (magic-link, password-reset,
/// email-verify, bootstrap-invite, login-provider callbacks).
///
/// <para>Thin facade over <see cref="RealmPublicOrigin"/>, which holds the rule
/// (it lives in the domain layer so realm provisioning can validate against the
/// same definition): the origin is CONFIGURED on the realm, never inferred from
/// the hosting environment. First installation records the origin its
/// installation link was issued for; <c>recover realm-set-public-url</c> changes
/// it afterwards. That is what lets a deployment on a non-default port (a
/// container published on :8081, the SPA dev server on :4300) state the truth
/// instead of having a port guessed for it.</para>
/// </summary>
public static class RealmPublicUrl
{
    /// <summary>
    /// Returns the realm's public base URL (no trailing slash): its declared
    /// <see cref="Realm.PublicBaseUrl"/>, else <c>https://{PrimaryDomain}</c>.
    /// </summary>
    public static string RealmPublicBaseUrl(Realm realm) => RealmPublicOrigin.Resolve(realm);

    /// <inheritdoc cref="RealmPublicOrigin.Normalize"/>
    public static string? NormalizeOrigin(string? candidate) => RealmPublicOrigin.Normalize(candidate);
}
