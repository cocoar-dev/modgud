namespace Cocoar.Auth.Authentication.Domain.ExternalAuth;

/// <summary>
/// String-based flavor identifiers for external identity providers. Open set —
/// new flavors (Okta, Keycloak, Google, GitHub, ...) are added by implementing
/// <c>IIdentityProviderFlavor</c> in Infrastructure and registering in DI. The
/// flavor key travels through events and documents as a plain string, so
/// renaming a flavor class does not invalidate stored configurations.
/// </summary>
public static class IdpFlavor
{
    public const string EntraId = "EntraId";
    public const string GenericOidc = "GenericOidc";
    // Future: Okta, Keycloak, Google, GitHub, Facebook, ...
}
