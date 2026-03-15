using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using Cocoar.Configuration.Reactive;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cocoar.Auth.Infrastructure.OpenIddict;

/// <summary>
/// Configuration settings interface for OpenIddict.
/// </summary>
public interface IOpenIddictSettings
{
	string? SigningCertificatePath { get; }
	string Issuer { get; }
	int AccessTokenLifetimeMinutes { get; }
	int RefreshTokenLifetimeDays { get; }
	int AuthorizationCodeLifetimeMinutes { get; }
	bool DevelopmentMode { get; }
}

/// <summary>
/// Extension methods for configuring OpenIddict with Marten document storage.
/// </summary>
public static class OpenIddictExtensions
{
	/// <summary>
	/// Adds OpenIddict services with Marten document storage.
	/// </summary>
	/// <param name="services">The service collection.</param>
	/// <param name="settings">The OpenIddict settings (required at configuration time for signing credentials).</param>
	public static IServiceCollection AddOpenIddictWithMarten<TSettings>(
		this IServiceCollection services,
		TSettings settings)
		where TSettings : class, IOpenIddictSettings
	{
		// Configure OpenIddict
		// Note: Store registrations are handled by OpenIddict's Add*Store methods
		services.AddOpenIddict()
			// Register the core services with custom stores
			.AddCore(options =>
			{
				// Applications and Scopes use inline projections from event sourcing
				// Authorizations and Tokens use document storage (ephemeral/sensitive data)
				options.SetDefaultApplicationEntity<OAuthApplicationState>()
					.SetDefaultAuthorizationEntity<OpenIddictAuthorizationDocument>()
					.SetDefaultScopeEntity<OAuthScopeState>()
					.SetDefaultTokenEntity<OpenIddictTokenDocument>();

				// Register custom stores
				options.AddApplicationStore<MartenApplicationStore>();
				options.AddAuthorizationStore<MartenAuthorizationStore>();
				options.AddScopeStore<MartenScopeStore>();
				options.AddTokenStore<MartenTokenStore>();
			})
			// Register the ASP.NET Core host and configure the authorization server
			.AddServer(options =>
			{
				// Enable the required endpoints
				options.SetAuthorizationEndpointUris("connect/authorize")
					.SetTokenEndpointUris("connect/token")
					.SetUserInfoEndpointUris("connect/userinfo")
					.SetEndSessionEndpointUris("connect/logout")
					.SetIntrospectionEndpointUris("connect/introspect")
					.SetRevocationEndpointUris("connect/revoke");

				// Enable the authorization code flow with PKCE
				options.AllowAuthorizationCodeFlow()
					.RequireProofKeyForCodeExchange();

				// Enable the refresh token flow
				options.AllowRefreshTokenFlow();

				// Enable the client credentials flow (for machine-to-machine)
				options.AllowClientCredentialsFlow();

				// Enable reference tokens - store token payloads server-side,
				// return opaque reference identifiers to clients.
				// This is the PRIMARY reason for building this custom identity server.
				// Per-client token type (Reference vs JWT) is controlled via application settings.
				options.UseReferenceAccessTokens()
					.UseReferenceRefreshTokens();

				// Register the standard scopes
				options.RegisterScopes(
					Scopes.OpenId,
					Scopes.Email,
					Scopes.Profile,
					Scopes.Roles,
					Scopes.OfflineAccess);

				// Configure signing credentials based on environment
				if (settings.DevelopmentMode)
				{
					// Use ephemeral signing keys for development
					options.AddDevelopmentEncryptionCertificate()
						.AddDevelopmentSigningCertificate();
				}
				else if (!string.IsNullOrEmpty(settings.SigningCertificatePath))
				{
					// Load the X509 certificate for production
					var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader
						.LoadCertificateFromFile(settings.SigningCertificatePath);
					options.AddEncryptionCertificate(certificate)
						.AddSigningCertificate(certificate);
				}

				// Register the ASP.NET Core host
				options.UseAspNetCore()
					.EnableAuthorizationEndpointPassthrough()
					.EnableTokenEndpointPassthrough()
					.EnableUserInfoEndpointPassthrough()
					.EnableEndSessionEndpointPassthrough()
					.EnableStatusCodePagesIntegration();
			})
			// Register the validation components
			.AddValidation(options =>
			{
				// Import the configuration from the local OpenIddict server instance
				options.UseLocalServer();

				// Register the ASP.NET Core host
				options.UseAspNetCore();
			});

		return services;
	}

	/// <summary>
	/// Configures OpenIddict signing credentials and token lifetimes.
	/// Call this after the service provider is built.
	/// </summary>
	public static void ConfigureOpenIddictServer<TSettings>(
		this IServiceProvider serviceProvider)
		where TSettings : class, IOpenIddictSettings
	{
		var settingsConfig = serviceProvider.GetRequiredService<IReactiveConfig<TSettings>>();
		var settings = settingsConfig.CurrentValue;

		// Note: Token lifetimes and signing credentials are configured at startup
		// and require application restart to change
	}

	/// <summary>
	/// Adds OpenIddict server options configuration based on settings.
	/// </summary>
	public static IServiceCollection ConfigureOpenIddictServerOptions<TSettings>(
		this IServiceCollection services)
		where TSettings : class, IOpenIddictSettings
	{
		services.AddOptions<global::OpenIddict.Server.OpenIddictServerOptions>()
			.Configure<IReactiveConfig<TSettings>>((options, settingsConfig) =>
			{
				var settings = settingsConfig.CurrentValue;

				// Set the issuer URI
				options.Issuer = new Uri(settings.Issuer);

				// Configure token lifetimes
				options.AccessTokenLifetime = TimeSpan.FromMinutes(settings.AccessTokenLifetimeMinutes);
				options.RefreshTokenLifetime = TimeSpan.FromDays(settings.RefreshTokenLifetimeDays);
				options.AuthorizationCodeLifetime = TimeSpan.FromMinutes(settings.AuthorizationCodeLifetimeMinutes);

				// Configure signing credentials based on environment
				// Development mode uses ephemeral keys, production requires certificates
				// Note: In OpenIddict 6.x, certificates are configured in the builder, not options
				// The DevelopmentMode flag is used in Program.cs to conditionally add certificates
			});

		return services;
	}

	/// <summary>
	/// Configures OpenIddict server with development or production signing credentials.
	/// </summary>
	public static OpenIddictServerBuilder ConfigureSigningCredentials<TSettings>(
		this OpenIddictServerBuilder builder,
		IServiceProvider serviceProvider)
		where TSettings : class, IOpenIddictSettings
	{
		var settingsConfig = serviceProvider.GetRequiredService<IReactiveConfig<TSettings>>();
		var settings = settingsConfig.CurrentValue;

		if (settings.DevelopmentMode)
		{
			// Use ephemeral signing keys for development
			builder.AddDevelopmentEncryptionCertificate()
				.AddDevelopmentSigningCertificate();
		}
		else if (!string.IsNullOrEmpty(settings.SigningCertificatePath))
		{
			// Load the X509 certificate for production
			var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader
				.LoadCertificateFromFile(settings.SigningCertificatePath);
			builder.AddEncryptionCertificate(certificate)
				.AddSigningCertificate(certificate);
		}

		return builder;
	}

	/// <summary>
	/// Seeds default scopes if they don't exist.
	/// </summary>
	public static async Task SeedOpenIddictScopesAsync(this IServiceProvider serviceProvider)
	{
		using var scope = serviceProvider.CreateScope();
		var scopeManager = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();

		// Standard scopes to seed
		var standardScopes = new[]
		{
			(Scopes.OpenId, "OpenID", "Required scope for OpenID Connect"),
			(Scopes.Email, "Email", "Access to email address"),
			(Scopes.Profile, "Profile", "Access to profile information"),
			(Scopes.Roles, "Roles", "Access to user roles"),
			(Scopes.OfflineAccess, "Offline Access", "Issue refresh tokens for offline access")
		};

		foreach (var (name, displayName, description) in standardScopes)
		{
			if (await scopeManager.FindByNameAsync(name) is null)
			{
				await scopeManager.CreateAsync(new OpenIddictScopeDescriptor
				{
					Name = name,
					DisplayName = displayName,
					Description = description
				});
			}
		}
	}
}
