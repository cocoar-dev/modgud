using System.Text.Json;

namespace Cocoar.Auth.Authentication.Identity.ExternalAuth;

/// <summary>
/// Contract every IdP flavor implements. The flavor encapsulates everything
/// that differs between providers (Entra, Okta, Google, ...) — endpoint
/// derivation, default scopes, default claims-transform script, admin-UI
/// schema, and sensible raw-claims-storage default.
/// <para>
/// Flavors are DI-registered as singletons and collected by
/// <see cref="FlavorRegistry"/>. The key returned by <see cref="Key"/> is the
/// stable string stored on <c>IdpConfig.Flavor</c> — immutable after creation.
/// </para>
/// </summary>
public interface IIdentityProviderFlavor
{
    /// <summary>Stable key (see <c>Cocoar.Auth.Domain.Identity.ExternalAuth.IdpFlavor</c> for canonical values).</summary>
    string Key { get; }

    /// <summary>Human-friendly name shown in the "Add provider" picker (e.g. "Microsoft Entra ID").</summary>
    string DisplayName { get; }

    /// <summary>Default icon name (Lucide or custom) suggested for new configs of this flavor.</summary>
    string DefaultIconName { get; }

    /// <summary>Default scopes an admin gets when adding a new config of this flavor.</summary>
    IReadOnlyList<string> DefaultScopes { get; }

    /// <summary>
    /// Default user-update-script body for this flavor. Populated into the
    /// admin editor on "add new config" so the operator has a working starting
    /// point that handles the flavor's typical claim quirks. Signature:
    /// <c>(claims) =&gt; ({ firstname, lastname, email, acronym })</c>.
    /// </summary>
    string DefaultUserUpdateScript { get; }

    /// <summary>
    /// Default for <c>IdpConfig.StoreRawClaims</c> — enterprise flavors (Entra,
    /// Okta, Keycloak) default to <c>true</c> because claim-debugging is
    /// mission-critical; consumer flavors (Google, GitHub) default to
    /// <c>false</c> because raw claims include PII that admins rarely need.
    /// </summary>
    bool DefaultStoreRawClaims { get; }

    /// <summary>Flavor-specific fields shown in the admin "Connection" tab.</summary>
    IReadOnlyList<FlavorConfigField> ConfigSchema { get; }

    /// <summary>
    /// Derive the OIDC endpoint set from the flavor-specific data the admin
    /// provided. Throws if the provided <paramref name="flavorData"/> is
    /// missing required fields (flavors are responsible for their own
    /// validation).
    /// </summary>
    OidcEndpoints DeriveEndpoints(JsonDocument? flavorData);
}
