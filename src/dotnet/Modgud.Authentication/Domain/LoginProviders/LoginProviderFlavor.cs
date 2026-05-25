namespace Modgud.Authentication.Domain.LoginProviders;

/// <summary>
/// String-based flavor identifiers for external login providers. Open set —
/// new flavors (Okta, Keycloak, Google, GitHub, ...) are added by implementing
/// <c>ILoginProviderFlavor</c> and registering in DI. The flavor key travels
/// through events and documents as a plain string, so renaming a flavor class
/// does not invalidate stored configurations.
/// <para>
/// <see cref="Internal"/> is the placeholder key used on built-in
/// <see cref="LoginProviderType.Internal"/> records. Internal providers do not
/// resolve to an <c>ILoginProviderFlavor</c> implementation — runtime code
/// must short-circuit on the <c>Type</c> field instead.
/// </para>
/// </summary>
public static class LoginProviderFlavor
{
    public const string Internal = "internal";
    public const string EntraId = "EntraId";
    public const string GenericOidc = "GenericOidc";
    // Future: Okta, Keycloak, Google, GitHub, Facebook, ...
}
