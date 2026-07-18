using Modgud.Infrastructure.OpenIddict;

namespace Modgud.Api;

/// <summary>
/// OpenIddict OAuth 2.0 / OIDC server configuration. Loaded from
/// <c>data/configuration.json</c> (and optional <c>data/configuration.local.json</c>),
/// overridable via <c>OpenIddict__*</c> env vars.
/// </summary>
public class OpenIddictSettings : IOpenIddictSettings
{
    /// <summary>
    /// Path to a passwordless PFX file holding the production signing
    /// certificate. When unset, a default of <c>data/keys/signing.pfx</c>
    /// is used. When the file at the resolved path is missing, the
    /// bootstrap auto-generates a self-signed cert there at first start
    /// (and persists it across restarts). Ignored when
    /// <see cref="DevelopmentMode"/> is true.
    ///
    /// <para>Convention: passwordless PFX, file-system permissions
    /// (<c>0600</c> on Linux) protect the private key. Mirrors the
    /// <c>cocoar-secrets</c> CLI tool's recommendation. To convert a
    /// password-protected PFX to passwordless, use
    /// <c>cocoar-secrets convert-cert --ipass &lt;old&gt; -i in.pfx -o out.pfx</c>.</para>
    /// </summary>
    public string? SigningCertificatePath { get; set; }

    /// <summary>
    /// Comma-separated list of paths to previously-active signing
    /// certificates. Loaded as validation-only keys for the rotation-overlap
    /// window (CERT-01). Cleared once the longest in-flight access token
    /// has expired and JWKS caches have refreshed. Passwordless PFX, same
    /// convention as <see cref="SigningCertificatePath"/>.
    /// </summary>
    public string[]? PreviousSigningCertificatePaths { get; set; }

    /// <summary>
    /// Path to a passwordless PFX for token encryption. Falls back to a
    /// default of <c>data/keys/encryption.pfx</c> when unset, then auto-
    /// generates if the file is missing. Separate keys for signing vs
    /// encryption are recommended for production (OAUTH-05).
    /// </summary>
    public string? EncryptionCertificatePath { get; set; }

    /// <summary>
    /// Comma-separated list of paths to previously-active encryption
    /// certificates. Loaded as decryption-only keys for the rotation-overlap
    /// window (issue #125) — mirrors <see cref="PreviousSigningCertificatePaths"/>.
    /// Cleared once the longest-lived in-flight authorization code, device
    /// code, or refresh token issued under the old cert has expired.
    /// Passwordless PFX, same convention as <see cref="EncryptionCertificatePath"/>.
    /// </summary>
    public string[]? PreviousEncryptionCertificatePaths { get; set; }

    // NOTE: there is deliberately no configurable Issuer. Modgud is multi-tenant
    // and the issuer is per-realm, derived from the request host (BaseUri) on
    // every path — discovery, the token `iss` claim, and token validation (see
    // RealmIssuerHandler / RealmSigningKeyHandler / RealmTokenValidationHandler).
    // A global issuer setting never took effect, so exposing one only invited
    // mis-tuning. OpenIddict's required base issuer is a fixed internal
    // placeholder (see OpenIddictExtensions).

    public int AccessTokenLifetimeMinutes { get; set; } = 60;
    public int RefreshTokenLifetimeDays { get; set; } = 14;
    public int AuthorizationCodeLifetimeMinutes { get; set; } = 5;

    /// <summary>
    /// Use ephemeral signing/encryption keys. Dev/test only — every restart
    /// rotates the keys and invalidates every previously issued token. The
    /// class default is <c>false</c> so a Production deployment that forgets
    /// to set it cannot accidentally end up in development mode (PROD-02);
    /// <c>data/configuration.json</c> sets it to <c>true</c> for the local
    /// dev experience, and the bootstrap throws when both
    /// <c>IsProduction()</c> and <c>DevelopmentMode</c> are true.
    /// </summary>
    public bool DevelopmentMode { get; set; } = false;
}
