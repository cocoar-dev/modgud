using Cocoar.Auth.Domain.OAuth.Applications;
using Cocoar.Auth.Domain.OAuth.Scopes;
using Cocoar.Auth.Domain.OAuth.Storage;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cocoar.Auth.Infrastructure.OpenIddict;

/// <summary>
/// Configuration settings for OpenIddict. Implementations live in the API layer
/// (<c>OpenIddictSettings</c>) so the Infrastructure layer stays unaware of how
/// configuration is loaded.
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
/// Default storage strategy:
/// <list type="bullet">
/// <item>Applications + Scopes — event-sourced (<c>OAuthApplicationState</c> / <c>OAuthScopeState</c>)</item>
/// <item>Authorizations + Tokens — plain documents (security-sensitive, ephemeral)</item>
/// </list>
/// All stores are tenant-scoped via <see cref="Persistence.Tenancy.ITenantSessionFactory"/>.
/// </summary>
public static class OpenIddictExtensions
{
    /// <summary>
    /// Adds OpenIddict services with Marten document storage. Settings are injected
    /// at configuration time so signing credentials can be set up before the host
    /// is built.
    /// </summary>
    public static IServiceCollection AddOpenIddictWithMarten<TSettings>(
        this IServiceCollection services,
        TSettings settings)
        where TSettings : class, IOpenIddictSettings
    {
        // BCrypt-aware application manager — replaces the default one so client_secret
        // validation/obfuscation matches the BCrypt format Cocoar.Auth uses everywhere
        // else (admin REST, demo-seed, secret rotation). See CocoarOpenIddictApplicationManager
        // for the rationale.
        services.AddScoped<global::OpenIddict.Core.OpenIddictApplicationManager<OAuthApplicationState>, CocoarOpenIddictApplicationManager>();

        // Custom Marten stores (OpenIddict 7 pattern: register the store directly,
        // not the entity → store mapping)
        services.AddScoped<IOpenIddictApplicationStore<OAuthApplicationState>, MartenApplicationStore>();
        services.AddScoped<IOpenIddictAuthorizationStore<OpenIddictAuthorizationDocument>, MartenAuthorizationStore>();
        services.AddScoped<IOpenIddictScopeStore<OAuthScopeState>, MartenScopeStore>();
        services.AddScoped<IOpenIddictTokenStore<OpenIddictTokenDocument>, MartenTokenStore>();

        services.AddOpenIddict()
            .AddCore(options =>
            {
                options.SetDefaultApplicationEntity<OAuthApplicationState>()
                    .SetDefaultAuthorizationEntity<OpenIddictAuthorizationDocument>()
                    .SetDefaultScopeEntity<OAuthScopeState>()
                    .SetDefaultTokenEntity<OpenIddictTokenDocument>();
            })
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris("connect/authorize")
                    .SetTokenEndpointUris("connect/token")
                    .SetUserInfoEndpointUris("connect/userinfo")
                    .SetEndSessionEndpointUris("connect/logout")
                    .SetIntrospectionEndpointUris("connect/introspect")
                    .SetRevocationEndpointUris("connect/revoke")
                    .SetDeviceAuthorizationEndpointUris("connect/device")
                    .SetEndUserVerificationEndpointUris("connect/verify");

                options.AllowAuthorizationCodeFlow()
                    .RequireProofKeyForCodeExchange();
                options.AllowRefreshTokenFlow();
                options.AllowClientCredentialsFlow();
                options.AllowDeviceAuthorizationFlow();

                // Reference tokens by default; per-client opt-in to JWT via AccessTokenTypeHandler.
                options.UseReferenceAccessTokens()
                    .UseReferenceRefreshTokens();

                // Don't encrypt access tokens — JWT clients (those with
                // AccessTokenType.Jwt) need consumer-readable tokens that
                // any standard JwtBearer + discovery JWKS can validate
                // without sharing an encryption key. Reference-token clients
                // are unaffected (the opaque token is only ever introspected
                // through OpenIddict itself). Tokens remain signed.
                options.DisableAccessTokenEncryption();

                options.RegisterScopes(
                    Scopes.OpenId, Scopes.Email, Scopes.Profile, Scopes.Roles, Scopes.OfflineAccess);

                options.SetIssuer(new Uri(settings.Issuer));
                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(settings.AccessTokenLifetimeMinutes));
                options.SetRefreshTokenLifetime(TimeSpan.FromDays(settings.RefreshTokenLifetimeDays));
                options.SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(settings.AuthorizationCodeLifetimeMinutes));

                if (settings.DevelopmentMode)
                {
                    // Ephemeral keys — fine for dev, NOT for prod.
                    options.AddDevelopmentEncryptionCertificate()
                        .AddDevelopmentSigningCertificate();
                }
                else if (!string.IsNullOrEmpty(settings.SigningCertificatePath))
                {
                    var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader
                        .LoadCertificateFromFile(settings.SigningCertificatePath);
                    options.AddEncryptionCertificate(certificate)
                        .AddSigningCertificate(certificate);
                }

                var aspNetCore = options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough()
                    .EnableEndUserVerificationEndpointPassthrough()
                    .EnableStatusCodePagesIntegration();

                if (settings.DevelopmentMode)
                {
                    // Allow HTTP for dev — production runs behind TLS-terminating proxy.
                    aspNetCore.DisableTransportSecurityRequirement();
                }

                options.AddEventHandler(RealmIssuerHandler.Descriptor);
                options.AddEventHandler(AccessTokenTypeHandler.Descriptor);
                options.AddEventHandler(RealmSigningKeyHandler.Descriptor);
                options.AddEventHandler(RealmJwksHandler.Descriptor);
                options.AddEventHandler(RealmTokenValidationHandler.Descriptor);
                options.AddEventHandler(RealmClaimHandler.Descriptor);
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        return services;
    }
}
