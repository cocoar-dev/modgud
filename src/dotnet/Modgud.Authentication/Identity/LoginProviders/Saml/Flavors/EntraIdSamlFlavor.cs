using Modgud.Authentication.Domain.LoginProviders;

namespace Modgud.Authentication.Identity.LoginProviders.Saml.Flavors;

/// <summary>
/// Microsoft Entra ID Enterprise Application configured for SAML 2.0 SSO.
/// Pre-fills the <see cref="SamlFlavorData.AttributeMap"/> with Microsoft's
/// claim URIs (the <c>http://schemas.microsoft.com/...</c> family plus the
/// SAML 2.0 standard short names) so the admin only has to enter the
/// Federation Metadata URL from the Enterprise App's SSO page.
/// </summary>
public class EntraIdSamlFlavor : ISamlFlavor
{
    public string Key => LoginProviderFlavor.EntraIdSaml;
    public string DisplayName => "Microsoft Entra ID (SAML)";
    public string DefaultIconName => "microsoft";
    public bool DefaultStoreRawClaims => true;

    public string DefaultUserUpdateScript => """
        // EntraID SAML → Modgud user patch. AttributeMap pre-translates
        // Microsoft's claim URIs to logical names, so the script reads
        // `claims.given_name` etc. directly. Entra emits `groups` as
        // object-IDs (GUIDs) — leave Modgud-Group mapping to the
        // membership script (see Group-Mapping tab).
        (claims) => ({
          firstname: claims.given_name?.trim(),
          lastname: claims.family_name?.trim(),
          email: claims.email ?? claims.upn,
          acronym: (claims.given_name?.[0] ?? '') + (claims.family_name?.[0] ?? '')
        })
        """;

    public IReadOnlyList<FlavorConfigField> ConfigSchema { get; } =
    [
        new FlavorConfigField(
            Key: "MetadataUrl",
            Type: FlavorConfigFieldType.Url,
            Label: "Federation Metadata URL",
            Required: true,
            HelpText: "Copy from the Entra Enterprise App → Single sign-on → 'App Federation Metadata Url' field.",
            Placeholder: "https://login.microsoftonline.com/<tenant-id>/federationmetadata/2007-06/federationmetadata.xml"),
        .. SamlAdvancedConfigFields.All,
    ];

    public SamlFlavorData ApplyDefaults(SamlFlavorData? data)
    {
        var basis = data ?? new SamlFlavorData();
        return basis with
        {
            // EntraID's default NameID is emailAddress when configured as
            // "User.mail" or "User.userprincipalname" — keep our default.
            NameIdFormat = string.IsNullOrEmpty(basis.NameIdFormat)
                ? SamlNameIdFormats.EmailAddress
                : basis.NameIdFormat,

            // Microsoft's standard SAML claim URIs. The list per logical name
            // includes both Microsoft's URI form and the short-form fallbacks
            // some IdP-side custom-mappings produce.
            AttributeMap = basis.AttributeMap.Count > 0
                ? basis.AttributeMap
                : new Dictionary<string, IReadOnlyList<string>>
                {
                    ["email"] =
                    [
                        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
                        "email",
                    ],
                    ["given_name"] =
                    [
                        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname",
                        "given_name",
                    ],
                    ["family_name"] =
                    [
                        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname",
                        "family_name",
                    ],
                    ["name"] =
                    [
                        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name",
                        "name",
                    ],
                    ["upn"] =
                    [
                        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn",
                    ],
                    ["groups"] =
                    [
                        "http://schemas.microsoft.com/ws/2008/06/identity/claims/groups",
                    ],
                },

            // EntraID supports both AuthnContextClass URIs; both map to
            // MFA when satisfied.
            AmrMapping = basis.AmrMapping.Count > 0
                ? basis.AmrMapping
                : new Dictionary<string, IReadOnlyList<string>>
                {
                    ["urn:oasis:names:tc:SAML:2.0:ac:classes:Password"] = ["pwd"],
                    ["urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport"] = ["pwd"],
                    ["urn:oasis:names:tc:SAML:2.0:ac:classes:MultiFactor"] = ["mfa"],
                    ["http://schemas.microsoft.com/claims/multipleauthn"] = ["mfa"],
                },
        };
    }
}
