namespace Modgud.Authentication.Identity.LoginProviders.Flavors;

/// <summary>
/// Advanced OIDC settings shared by every OIDC flavor. Like the SAML side, the
/// set of knobs is identical across flavors (Generic / Entra ID) — flavors
/// differ only in connection defaults. These map 1:1 onto
/// <c>OpenIdConnectOptions</c> properties consumed by
/// <c>DynamicOidcSchemeManager</c>; defaults match the values that manager
/// previously hard-coded, so existing providers behave unchanged.
/// <para>Rendered on the admin "Advanced" tab via the shared section machinery.</para>
/// </summary>
public static class OidcAdvancedConfigFields
{
    public static IReadOnlyList<FlavorConfigField> All { get; } =
    [
        new FlavorConfigField(
            Key: "UsePkce",
            Type: FlavorConfigFieldType.Boolean,
            Label: "Use PKCE",
            HelpText: "Send a PKCE code challenge on the authorization-code flow. Strongly recommended — turn off only for a legacy IdP that rejects PKCE.",
            Default: true,
            Section: FlavorConfigSections.Advanced),
        new FlavorConfigField(
            Key: "GetClaimsFromUserInfoEndpoint",
            Type: FlavorConfigFieldType.Boolean,
            Label: "Fetch claims from UserInfo endpoint",
            HelpText: "After token exchange, call the IdP's UserInfo endpoint for the full claim set. Turn off if the IdP returns everything in the id_token or has no UserInfo endpoint.",
            Default: true,
            Section: FlavorConfigSections.Advanced),
        new FlavorConfigField(
            Key: "SaveTokens",
            Type: FlavorConfigFieldType.Boolean,
            Label: "Save tokens",
            HelpText: "Persist the IdP's access/refresh/id tokens in the external auth ticket. Off by default — only needed if something downstream calls the IdP on the user's behalf.",
            Default: false,
            Section: FlavorConfigSections.Advanced),
        new FlavorConfigField(
            Key: "Prompt",
            Type: FlavorConfigFieldType.Select,
            Label: "Prompt",
            HelpText: "OIDC 'prompt' parameter sent on every sign-in. Default sends none (the IdP decides). 'select_account' always shows the account picker; 'login' forces re-authentication.",
            Default: "",
            Section: FlavorConfigSections.Advanced,
            Options:
            [
                new FlavorConfigFieldOption("", "Default (none)"),
                new FlavorConfigFieldOption("login", "login — force re-authentication"),
                new FlavorConfigFieldOption("select_account", "select_account — show account picker"),
                new FlavorConfigFieldOption("consent", "consent — force consent screen"),
                new FlavorConfigFieldOption("none", "none — silent (fail if interaction needed)"),
            ]),
    ];
}
