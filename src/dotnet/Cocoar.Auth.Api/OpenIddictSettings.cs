using Cocoar.Auth.Infrastructure.OpenIddict;

namespace Cocoar.Auth.Api;

/// <summary>
/// OpenIddict OAuth 2.0 / OIDC server configuration. Loaded from
/// <c>data/configuration.json</c> (and optional <c>data/configuration.local.json</c>),
/// overridable via <c>OpenIddict__*</c> env vars.
/// </summary>
public class OpenIddictSettings : IOpenIddictSettings
{
    /// <summary>Path to a PFX file holding the production signing certificate. Ignored when <see cref="DevelopmentMode"/> is true.</summary>
    public string? SigningCertificatePath { get; set; }

    /// <summary>
    /// Issuer URL advertised in /.well-known/openid-configuration. Per-realm
    /// overrides are applied at request time by <c>RealmIssuerHandler</c>.
    /// CONFIG-01: defaults to empty so a Production deployment that forgot to
    /// set it fails closed at startup rather than silently advertising
    /// http://localhost:9099 to remote clients. Development sets the
    /// localhost default in <c>data/configuration.json</c>.
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

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
