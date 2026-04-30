namespace Cocoar.Auth.Authentication.Domain.LoginProviders;

/// <summary>
/// Discriminator on a <see cref="LoginProvider"/>. <c>Internal</c> is the
/// built-in password/passkey/magic-link path that ships with the IdP — it has
/// no Flavor, no ClientId/Secret, no external callback. Every other Type
/// represents an external authentication mechanism that goes through a
/// flavor-specific (per-protocol/per-vendor) configuration surface.
/// </summary>
public enum LoginProviderType
{
    /// <summary>Built-in password/passkey/magic-link authentication.</summary>
    Internal = 0,

    /// <summary>External OpenID Connect provider (Entra, Okta, Keycloak, …).</summary>
    Oidc = 1,

    /// <summary>SAML 2.0 IdP (not yet wired — Phase 2+).</summary>
    Saml = 2,

    /// <summary>LDAP directory bind (not yet wired — Phase 2+).</summary>
    Ldap = 3,

    /// <summary>Kerberos/SPNEGO (not yet wired — Phase 2+).</summary>
    Kerberos = 4,
}
