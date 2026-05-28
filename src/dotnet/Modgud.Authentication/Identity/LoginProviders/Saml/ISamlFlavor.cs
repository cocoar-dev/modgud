namespace Modgud.Authentication.Identity.LoginProviders.Saml;

/// <summary>
/// Contract every SAML 2.0 SP login-provider flavor implements. Mirrors
/// <see cref="Modgud.Authentication.Identity.LoginProviders.ILoginProviderFlavor"/>
/// but tailored to SAML — no <c>DefaultScopes</c> (SAML has no scopes), and
/// the endpoint-derivation step is replaced by <see cref="ApplyDefaults"/>
/// which seeds vendor-specific defaults onto the admin-provided
/// <see cref="SamlFlavorData"/> blob.
/// <para>
/// Flavors are DI-registered as singletons and collected by
/// <see cref="SamlFlavorRegistry"/>. The key returned by <see cref="Key"/>
/// is the stable string stored on <c>LoginProvider.Flavor</c> — immutable
/// after creation.
/// </para>
/// </summary>
public interface ISamlFlavor
{
    /// <summary>
    /// Stable key (see <c>Modgud.Authentication.Domain.LoginProviders.LoginProviderFlavor</c>
    /// for canonical SAML values: <c>EntraIdSaml</c>, <c>AdfsSaml</c>, <c>GenericSaml</c>).
    /// </summary>
    string Key { get; }

    /// <summary>Human-friendly name shown in the "Add provider" picker (e.g. "Microsoft Entra ID (SAML)").</summary>
    string DisplayName { get; }

    /// <summary>Default icon name (Lucide or custom) suggested for new configs of this flavor.</summary>
    string DefaultIconName { get; }

    /// <summary>
    /// Default user-update-script body for this flavor. Same shape as the OIDC
    /// flavor's script — receives the SAML <c>rawClaims</c> dict (logical-claim
    /// name → list of attribute values), returns
    /// <c>({ firstname, lastname, email, acronym })</c> for patching the linked
    /// Modgud user. Vendor-specific quirks (e.g. EntraID's claim URIs) are
    /// already absorbed by <see cref="ApplyDefaults"/>'s pre-filled
    /// <c>AttributeMap</c>; the script sees the logical claim names only.
    /// </summary>
    string DefaultUserUpdateScript { get; }

    /// <summary>
    /// Default for <c>LoginProvider.StoreRawClaims</c>. Enterprise SAML
    /// flavors default to <c>true</c> because assertion-debugging is
    /// mission-critical during onboarding; generic flavor too.
    /// </summary>
    bool DefaultStoreRawClaims { get; }

    /// <summary>Flavor-specific fields shown in the admin "Connection" tab.</summary>
    IReadOnlyList<FlavorConfigField> ConfigSchema { get; }

    /// <summary>
    /// Apply flavor-specific defaults to (a copy of) <paramref name="data"/>.
    /// Called once at "add new provider" time so the admin starts with
    /// vendor-appropriate defaults (e.g. EntraID's <c>AttributeMap</c> already
    /// populated with Microsoft's claim URIs). The returned record never
    /// mutates the input.
    /// <para>
    /// Pass <c>null</c> to get a fresh record with only this flavor's
    /// defaults applied.
    /// </para>
    /// </summary>
    SamlFlavorData ApplyDefaults(SamlFlavorData? data);
}
