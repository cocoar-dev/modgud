using Modgud.Authentication.Domain.LoginProviders;

namespace Modgud.Authentication.Identity.LoginProviders.Saml.Flavors;

/// <summary>
/// On-prem Active Directory Federation Services (AD FS) configured for SAML
/// 2.0 SSO. Pre-fills the <see cref="SamlFlavorData.AttributeMap"/> with
/// ADFS's default claim URIs (the same <c>http://schemas.xmlsoap.org/...</c>
/// family Microsoft uses for AD claims) and defaults the NameID format to
/// UPN — the typical ADFS claim-rule produces UPN-style NameIDs.
/// <para>
/// ADFS often sits behind a customer firewall without a publicly reachable
/// federation-metadata URL; the admin will frequently use the Metadata XML
/// paste field instead.
/// </para>
/// </summary>
public class AdfsSamlFlavor : ISamlFlavor
{
    public string Key => LoginProviderFlavor.AdfsSaml;
    public string DisplayName => "Active Directory Federation Services (SAML)";
    public string DefaultIconName => "server";
    public bool DefaultStoreRawClaims => true;

    public string DefaultUserUpdateScript => """
        // ADFS SAML → Modgud user patch. AttributeMap pre-translates AD's
        // standard claim URIs (windowsaccountname, upn, givenname, surname,
        // emailaddress) to logical names. Customise the IdP-side claim
        // rule if your forest emits different attributes.
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
            Label: "AD FS Metadata URL",
            Required: false,
            HelpText: "Typically https://<adfs-host>/FederationMetadata/2007-06/FederationMetadata.xml. Leave empty if AD FS sits behind a firewall and use the XML paste instead.",
            Placeholder: "https://adfs.customer.local/FederationMetadata/2007-06/FederationMetadata.xml"),
        new FlavorConfigField(
            Key: "MetadataXml",
            Type: FlavorConfigFieldType.MultilineText,
            Label: "AD FS Metadata XML (alternative to URL)",
            Required: false,
            HelpText: "Paste the FederationMetadata.xml content from the AD FS host. Use when the URL isn't reachable from Modgud.",
            Placeholder: "<EntityDescriptor ..."),
        .. SamlAdvancedConfigFields.All,
    ];

    public SamlFlavorData ApplyDefaults(SamlFlavorData? data)
    {
        var basis = data ?? new SamlFlavorData();
        return basis with
        {
            // ADFS default claim rule emits UPN as the NameID — match.
            NameIdFormat = string.IsNullOrEmpty(basis.NameIdFormat)
                ? SamlNameIdFormats.EmailAddress
                : basis.NameIdFormat,

            // AD-flavoured claim URIs (the schemas.xmlsoap.org family).
            AttributeMap = basis.AttributeMap.Count > 0
                ? basis.AttributeMap
                : new Dictionary<string, IReadOnlyList<string>>
                {
                    ["email"] =
                    [
                        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
                    ],
                    ["given_name"] =
                    [
                        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname",
                    ],
                    ["family_name"] =
                    [
                        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname",
                    ],
                    ["name"] =
                    [
                        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name",
                    ],
                    ["upn"] =
                    [
                        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn",
                    ],
                    ["windowsaccountname"] =
                    [
                        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/windowsaccountname",
                    ],
                    ["groups"] =
                    [
                        "http://schemas.xmlsoap.org/claims/Group",
                        "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
                    ],
                },

            AmrMapping = basis.AmrMapping.Count > 0
                ? basis.AmrMapping
                : new Dictionary<string, IReadOnlyList<string>>
                {
                    ["urn:federation:authentication:windows"] = ["pwd"],
                    ["urn:oasis:names:tc:SAML:2.0:ac:classes:Password"] = ["pwd"],
                    ["urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport"] = ["pwd"],
                    ["urn:oasis:names:tc:SAML:2.0:ac:classes:TLSClient"] = ["mfa"],
                    ["urn:oasis:names:tc:SAML:2.0:ac:classes:X509"] = ["mfa"],
                    ["urn:oasis:names:tc:SAML:2.0:ac:classes:MultiFactor"] = ["mfa"],
                },
        };
    }
}
