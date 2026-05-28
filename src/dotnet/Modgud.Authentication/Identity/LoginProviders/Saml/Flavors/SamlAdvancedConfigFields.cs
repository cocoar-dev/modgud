namespace Modgud.Authentication.Identity.LoginProviders.Saml.Flavors;

/// <summary>
/// The advanced SAML settings shared by EVERY SAML flavor. SAML is one protocol,
/// so the set of tunable knobs is identical across Generic / Entra ID / AD FS —
/// flavors differ only in default <em>values</em>, labels, and the seeded
/// claim/AMR maps, never in <em>which</em> knobs exist. Surfacing them from one
/// shared list means a flavor can't silently omit a setting (the bug that hit
/// the Entra flavor with <c>WantResponseSigned</c>) and a new SAML setting lands
/// in every flavor at once.
/// <para>
/// All fields live in the <see cref="FlavorConfigSections.Advanced"/> section so
/// the admin UI renders them on a dedicated "Advanced" tab. Defaults encode the
/// common real-world posture (assertion signed, response NOT required signed —
/// see <see cref="SamlFlavorData.WantResponseSigned"/>).
/// </para>
/// </summary>
public static class SamlAdvancedConfigFields
{
    public static IReadOnlyList<FlavorConfigField> All { get; } =
    [
        new FlavorConfigField(
            Key: "WantAssertionsSigned",
            Type: FlavorConfigFieldType.Boolean,
            Label: "Require signed assertions",
            HelpText: "Reject responses whose SAML assertion is not signed. This is the primary protection against XML-signature-wrapping — strongly recommended to keep on.",
            Default: true,
            Section: FlavorConfigSections.Advanced),
        new FlavorConfigField(
            Key: "WantResponseSigned",
            Type: FlavorConfigFieldType.Boolean,
            Label: "Require signed response",
            HelpText: "Reject responses whose outer <Response> wrapper is not signed. Most IdPs — including Microsoft Entra ID and AD FS — sign only the assertion by default, so leave this OFF unless your IdP is explicitly configured to sign the SAML Response.",
            Default: false,
            Section: FlavorConfigSections.Advanced),
        new FlavorConfigField(
            Key: "SignAuthnRequest",
            Type: FlavorConfigFieldType.Boolean,
            Label: "Sign AuthnRequest",
            HelpText: "Sign the outgoing SAML AuthnRequest with this realm's SP signing key.",
            Default: true,
            Section: FlavorConfigSections.Advanced),
        new FlavorConfigField(
            Key: "WantAssertionsEncrypted",
            Type: FlavorConfigFieldType.Boolean,
            Label: "Require encrypted assertions",
            HelpText: "Require the assertion to be XML-encrypted (in addition to being signed). Rare — most IdPs only encrypt over TLS. Leave off unless your IdP is configured for assertion encryption.",
            Default: false,
            Section: FlavorConfigSections.Advanced),
        new FlavorConfigField(
            Key: "NameIdFormat",
            Type: FlavorConfigFieldType.Select,
            Label: "NameID format",
            HelpText: "Requested NameID format in the outgoing AuthnRequest. emailAddress is the safe default for most IdPs.",
            Default: SamlNameIdFormats.EmailAddress,
            Section: FlavorConfigSections.Advanced,
            Options:
            [
                new FlavorConfigFieldOption(SamlNameIdFormats.EmailAddress, "Email address"),
                new FlavorConfigFieldOption(SamlNameIdFormats.Persistent, "Persistent"),
                new FlavorConfigFieldOption(SamlNameIdFormats.Transient, "Transient"),
                new FlavorConfigFieldOption(SamlNameIdFormats.Unspecified, "Unspecified"),
            ]),
        new FlavorConfigField(
            Key: "EntityId",
            Type: FlavorConfigFieldType.String,
            Label: "IdP Entity ID (optional)",
            HelpText: "SAML IdP Entity ID. Leave empty to auto-detect from the IdP metadata on first fetch; set explicitly only if the metadata omits it or you need to override.",
            Placeholder: "https://sts.windows.net/<tenant-id>/",
            Section: FlavorConfigSections.Advanced),
        new FlavorConfigField(
            Key: "MetadataRefreshIntervalSeconds",
            Type: FlavorConfigFieldType.Select,
            Label: "Metadata refresh interval",
            HelpText: "How often Modgud re-fetches the IdP metadata to pick up signing-certificate rotation.",
            Default: SamlFlavorData.DefaultMetadataRefreshIntervalSeconds.ToString(),
            Section: FlavorConfigSections.Advanced,
            Options:
            [
                new FlavorConfigFieldOption("3600", "1 hour"),
                new FlavorConfigFieldOption("21600", "6 hours"),
                new FlavorConfigFieldOption("86400", "24 hours"),
                new FlavorConfigFieldOption("604800", "7 days"),
            ]),
    ];
}
