namespace Modgud.Authentication.Identity.LoginProviders.Saml.Flavors;

/// <summary>
/// Signature-policy config fields shared by every SAML flavor. Surfaced in the
/// admin "Connection" tab as checkboxes so admins can match the toggles to what
/// their IdP actually signs.
/// <para>
/// Defaults encode the common real-world posture: require the <b>assertion</b>
/// to be signed (the XML-signature-wrapping defense) but NOT the <c>Response</c>
/// wrapper — most IdPs, including Microsoft Entra ID and AD FS, sign only the
/// assertion by default. Requiring response signing against such an IdP yields
/// <c>saml-response-unsigned</c>. Admins whose IdP signs the response can tick
/// the box.
/// </para>
/// </summary>
public static class SamlSigningConfigFields
{
    public static IReadOnlyList<FlavorConfigField> All { get; } =
    [
        new FlavorConfigField(
            Key: "WantAssertionsSigned",
            Type: FlavorConfigFieldType.Boolean,
            Label: "Require signed assertions",
            Required: false,
            HelpText: "Reject responses whose SAML assertion is not signed. This is the primary protection against XML-signature-wrapping — strongly recommended to keep on.",
            Placeholder: null,
            Default: true),
        new FlavorConfigField(
            Key: "WantResponseSigned",
            Type: FlavorConfigFieldType.Boolean,
            Label: "Require signed response",
            Required: false,
            HelpText: "Reject responses whose outer <Response> wrapper is not signed. Most IdPs — including Microsoft Entra ID and AD FS — sign only the assertion by default, so leave this OFF unless your IdP is explicitly configured to sign the SAML Response.",
            Placeholder: null,
            Default: false),
        new FlavorConfigField(
            Key: "SignAuthnRequest",
            Type: FlavorConfigFieldType.Boolean,
            Label: "Sign AuthnRequest",
            Required: false,
            HelpText: "Sign the outgoing SAML AuthnRequest with this realm's SP signing key.",
            Placeholder: null,
            Default: true),
    ];
}
