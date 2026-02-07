using Cocoar.Auth.Infrastructure.OpenIddict;
using Cocoar.Configuration.Secrets.SecretTypes;

namespace Cocoar.Auth.Api.Configuration;

/// <summary>
/// OpenIddict OAuth 2.0 / OIDC configuration settings.
/// </summary>
public class OpenIddictSettings : IOpenIddictSettings
{
	/// <summary>
	/// Path to the X509 signing certificate (PFX file).
	/// In production, this should be set to a valid certificate path.
	/// </summary>
	public string? SigningCertificatePath { get; init; }

	/// <summary>
	/// Password for the signing certificate.
	/// </summary>
	public ISecret<string>? SigningCertificatePassword { get; init; }

	/// <summary>
	/// The issuer URL for the OpenIddict server.
	/// This should match the public URL of the identity provider.
	/// </summary>
	public required string Issuer { get; init; }

	/// <summary>
	/// Access token lifetime in minutes.
	/// </summary>
	public int AccessTokenLifetimeMinutes { get; init; } = 60;

	/// <summary>
	/// Refresh token lifetime in days.
	/// </summary>
	public int RefreshTokenLifetimeDays { get; init; } = 14;

	/// <summary>
	/// Authorization code lifetime in minutes.
	/// </summary>
	public int AuthorizationCodeLifetimeMinutes { get; init; } = 5;

	/// <summary>
	/// Enable development mode with ephemeral signing keys.
	/// Should only be true in development environments.
	/// </summary>
	public bool DevelopmentMode { get; init; }
}
