using System.Collections.Concurrent;

namespace Modgud.Authentication.Api.ExternalAuth;

/// <summary>
/// Maps a dynamically-registered OIDC scheme name to the realm slug it belongs
/// to. Pure in-memory bookkeeping — never user-facing, never in a URL.
/// <para>
/// Needed because the external-OIDC callback path is now the admin-chosen
/// provider <c>slug</c> (<c>/signin-oidc/{slug}</c>), and slugs are only unique
/// per realm. Two realms can register schemes with the same callback path. The
/// built-in <see cref="Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler"/>
/// matches the callback purely by path and is host-blind, so without a tenant
/// tiebreaker the wrong realm's scheme could claim the callback.
/// <see cref="HostAwareOpenIdConnectHandler"/> consults this registry to compare
/// the scheme's realm against the realm the current request resolved to.
/// </para>
/// <para>
/// Populated by <see cref="DynamicOidcSchemeManager"/> on register and pruned on
/// unregister, mirroring how the SAML side carries <c>RealmSlug</c> on its cached
/// <c>RegisteredSamlProvider</c> entries.
/// </para>
/// </summary>
public sealed class OidcSchemeRealmRegistry
{
    private readonly ConcurrentDictionary<string, string> _schemeToRealm = new(StringComparer.Ordinal);

    /// <summary>Record (or overwrite) the realm a scheme belongs to.</summary>
    public void Set(string schemeName, string realmSlug) => _schemeToRealm[schemeName] = realmSlug;

    /// <summary>Drop the mapping for a scheme. Idempotent.</summary>
    public void Remove(string schemeName) => _schemeToRealm.TryRemove(schemeName, out _);

    /// <summary>Resolve the realm a scheme belongs to. <c>false</c> if untracked.</summary>
    public bool TryGetRealm(string schemeName, out string? realmSlug) =>
        _schemeToRealm.TryGetValue(schemeName, out realmSlug);
}
