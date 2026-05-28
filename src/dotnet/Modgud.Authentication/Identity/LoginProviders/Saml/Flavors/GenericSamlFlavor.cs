using Modgud.Authentication.Domain.LoginProviders;

namespace Modgud.Authentication.Identity.LoginProviders.Saml.Flavors;

/// <summary>
/// Vendor-neutral SAML 2.0 flavor. No claim-URI presets — the admin enters
/// metadata URL and (optionally) an attribute map for their IdP's specific
/// claim shape. Use this when the customer's IdP isn't EntraID or ADFS and
/// the admin knows the per-attribute URIs from their IdP-side setup screen.
/// </summary>
public class GenericSamlFlavor : ISamlFlavor
{
    public string Key => LoginProviderFlavor.GenericSaml;
    public string DisplayName => "Generic SAML 2.0";
    public string DefaultIconName => "key-round";
    public bool DefaultStoreRawClaims => true;

    public string DefaultUserUpdateScript => """
        // SAML → Modgud user patch. The `claims` object contains logical
        // claim names (driven by the provider's AttributeMap) → first
        // value. Use claims._all['name'] to get the full array if the
        // attribute is multi-valued. Returned object updates the linked
        // user's profile properties.
        (claims) => ({
          firstname: claims.given_name?.trim() ?? claims.first_name?.trim(),
          lastname: claims.family_name?.trim() ?? claims.last_name?.trim(),
          email: claims.email,
          acronym:
            ((claims.given_name?.[0] ?? claims.first_name?.[0]) ?? '') +
            ((claims.family_name?.[0] ?? claims.last_name?.[0]) ?? '')
        })
        """;

    public IReadOnlyList<FlavorConfigField> ConfigSchema { get; } =
    [
        new FlavorConfigField(
            Key: "MetadataUrl",
            Type: FlavorConfigFieldType.Url,
            Label: "IdP Federation Metadata URL",
            Required: false,
            HelpText: "Public URL where the IdP publishes its federation metadata XML. Either this or MetadataXml must be set.",
            Placeholder: "https://idp.example.com/saml/metadata"),
        new FlavorConfigField(
            Key: "MetadataXml",
            Type: FlavorConfigFieldType.MultilineText,
            Label: "IdP Metadata XML (alternative to URL)",
            Required: false,
            HelpText: "Paste the IdP metadata XML directly. Use when the IdP doesn't publish a reachable metadata URL.",
            Placeholder: "<EntityDescriptor xmlns=\"urn:oasis:names:tc:SAML:2.0:metadata\" ..."),
        .. SamlAdvancedConfigFields.All,
    ];

    public SamlFlavorData ApplyDefaults(SamlFlavorData? data) =>
        // Generic has no vendor-specific defaults beyond the record's own.
        // Caller picks NameIdFormat / toggles via the Connection tab.
        data ?? new SamlFlavorData();
}
