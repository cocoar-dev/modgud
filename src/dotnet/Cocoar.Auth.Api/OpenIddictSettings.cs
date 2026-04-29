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

    /// <summary>Issuer URL advertised in /.well-known/openid-configuration. Per-realm overrides are applied at request time by <c>RealmIssuerHandler</c>.</summary>
    public string Issuer { get; set; } = "http://localhost:9099";

    public int AccessTokenLifetimeMinutes { get; set; } = 60;
    public int RefreshTokenLifetimeDays { get; set; } = 14;
    public int AuthorizationCodeLifetimeMinutes { get; set; } = 5;

    /// <summary>Use ephemeral signing/encryption keys. Dev only; tokens get invalidated on every restart.</summary>
    public bool DevelopmentMode { get; set; } = true;
}
