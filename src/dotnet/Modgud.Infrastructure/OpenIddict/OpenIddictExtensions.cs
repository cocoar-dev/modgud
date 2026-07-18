using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.OAuth.Scopes;
using Modgud.Domain.OAuth.Storage;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Configuration settings for OpenIddict. Implementations live in the API layer
/// (<c>OpenIddictSettings</c>) so the Infrastructure layer stays unaware of how
/// configuration is loaded.
/// </summary>
public interface IOpenIddictSettings
{
    /// <summary>
    /// Path to the active production signing certificate (passwordless PFX).
    /// The first key added to the OpenIddict server is the active signing
    /// key — every newly issued JWT is signed with it.
    /// </summary>
    string? SigningCertificatePath { get; }

    /// <summary>
    /// Optional list of additional signing certificates for rotation overlap
    /// (CERT-01). Loaded as validation-only keys after the active one — a
    /// resource server validating an in-flight token issued just before the
    /// rotation still finds its kid in the JWKS document. Typical rotation
    /// procedure: deploy with both old + new paths set, wait out the longest
    /// access-token lifetime + JWKS cache TTL, then redeploy with only the
    /// new path. Passwordless PFX, same convention as the active key.
    /// </summary>
    string[]? PreviousSigningCertificatePaths { get; }

    /// <summary>
    /// Optional path to a separate encryption certificate (passwordless PFX).
    /// When null, <see cref="SigningCertificatePath"/> is reused for
    /// encryption (legacy behaviour) — operators are expected to provide a
    /// separate key for production-grade deployments so a key compromise on
    /// one axis doesn't carry through to the other (OAUTH-05).
    /// </summary>
    string? EncryptionCertificatePath { get; }

    /// <summary>
    /// Optional list of additional encryption certificates for rotation
    /// overlap (issue #125, the encryption-side follow-up to CERT-01).
    /// Loaded as decryption-only keys after the active one — an
    /// authorization code, device code, or refresh token that was
    /// JWE-encrypted just before the rotation still decrypts, because
    /// OpenIddict tries every registered encryption credential against an
    /// incoming token rather than only the active one. Unlike
    /// <see cref="PreviousSigningCertificatePaths"/>, none of this is ever
    /// published externally — encryption certs never appear in a JWKS —
    /// so this purely extends the server's own decrypt-attempt list. Same
    /// rotation procedure and passwordless-PFX convention as the active key.
    /// </summary>
    string[]? PreviousEncryptionCertificatePaths { get; }

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
        // validation/obfuscation matches the BCrypt format Modgud uses everywhere
        // else (admin REST, demo-seed, secret rotation). See CocoarOpenIddictApplicationManager
        // for the rationale.
        services.AddScoped<global::OpenIddict.Core.OpenIddictApplicationManager<OAuthApplicationState>, CocoarOpenIddictApplicationManager>();

        // CIMD — resolves https client_id URLs into synthesized,
        // non-persisted public clients. Scoped (reads tenant-scoped
        // RealmSettings); the metadata fetch goes through a named HttpClient
        // whose primary handler carries the SSRF guard (block private/
        // loopback/link-local/ULA/CGNAT/multicast at connect time, no
        // redirects). 5s overall timeout; the handler adds a 5s connect
        // timeout + per-resolve 5 KB body cap.
        services.AddScoped<Cimd.CimdClientResolver>();
        services.AddHttpClient(Cimd.CimdClientResolver.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(5);
            })
            .ConfigurePrimaryHttpMessageHandler(() => Cimd.CimdHttpMessageHandlerFactory.Create());

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

                // OAuth-2.1 / MCP-spec compliance — the `plain` PKCE
                // method is forbidden by OAuth 2.1 and the MCP
                // authorization spec mandates S256. OpenIddict 7
                // advertises both `plain` and `S256` in
                // code_challenge_methods_supported by default; remove
                // `plain` so the discovery doc is honest about what
                // we actually want clients to use, and so a
                // misconfigured client can't downgrade.
                options.Configure(opts =>
                {
                    opts.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain);
                });

                // OAUTH-10 — make refresh-token reuse detection strict. With a
                // non-zero leeway window, a refresh token's redeemed-then-
                // presented event in that window doesn't reject. Zero means
                // any second presentation after the first redemption fires
                // invalid_grant via OpenIddict's own stock
                // Protection.ValidateTokenEntry handler, which also revokes
                // the whole token family; RefreshTokenReuseAuditHandler
                // records the security-audit event just before that runs.
                options.SetRefreshTokenReuseLeeway(TimeSpan.Zero);
                options.AllowClientCredentialsFlow();
                options.AllowDeviceAuthorizationFlow();

                // ADR-0010 — native (cookieless) passwordless token grants. The
                // factor (email-OTP / magic-link) is verified server-side in
                // AuthorizationEndpoints.ExchangeAsync and tokens are minted
                // directly, with no browser, no cookie and no hosted login page.
                // Two independent gates beyond AllowCustomFlow: a per-realm enable
                // flag (RealmSettings.NativeGrants, default OFF) checked in the
                // dispatch branch, and the per-client gt:urn:cocoar:* application
                // permission — IgnoreGrantTypePermissions is NOT set, so OpenIddict
                // rejects a client that lacks the permission with unauthorized_client.
                options.AllowCustomFlow(CocoarGrantTypes.Otp);
                options.AllowCustomFlow(CocoarGrantTypes.Magic);
                options.AllowCustomFlow(CocoarGrantTypes.Passkey);

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

                // The OIDC standards plus the Cocoar-specific `permissions`
                // scope — both static-registered so they appear in Discovery
                // (via OpenIddict's stock AttachScopes handler) and so the
                // consent screen offers them as opt-ins. `permissions`
                // mirrors the `roles` pattern: per-scope-per-claim gate in
                // the UserInfo emission pipeline (AuthorizationEndpoints).
                options.RegisterScopes(
                    Scopes.OpenId, Scopes.Email, Scopes.Profile, Scopes.Roles, Scopes.OfflineAccess,
                    "permissions");

                // Multi-tenant IdP: audiences (OAuthApi names) live in tenant
                // databases, so the built-in resource validators would reject
                // every per-tenant audience.
                //  - DisableResourceValidation: turns off the global check
                //    against the static options.Resources list set at startup.
                //  - IgnoreResourcePermissions: turns off the per-application
                //    resource-permission check (oi_rsrc_… permission entries),
                //    which we don't model — clients are linked to Apps, and the
                //    audience set is determined by the OAuthScopes a client may
                //    request, not by a flat per-client allow-list.
                // Our custom ResourceIndicatorHandler runs at sign-in time and
                // validates each requested resource against principal.GetResources()
                // (populated from the active tenant's OAuthScope.Resources via
                // scopeManager.ListResourcesAsync), preserving the per-tenant
                // gating.
                options.DisableResourceValidation();
                options.IgnoreResourcePermissions();

                // OpenIddict requires a base issuer at config time, but Modgud
                // NEVER emits it: the effective issuer is per-realm, derived from
                // the request host (BaseUri) on every path — discovery
                // (RealmIssuerHandler), the token `iss` claim (RealmSigningKeyHandler)
                // and token validation (RealmTokenValidationHandler). This is a
                // deliberately-unroutable placeholder (RFC 2606 `.invalid`); if it
                // ever surfaces in a token, BaseUri resolution failed upstream. It is
                // intentionally NOT operator-configurable — a knob that never takes
                // effect is worse than none.
                options.SetIssuer(new Uri("https://issuer.invalid/"));
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
                    // CERT-01 / OAUTH-05 — production cert loading with
                    // password support, rotation-overlap, and separate
                    // signing/encryption certs.
                    //
                    // The first AddSigningCertificate/AddEncryptionCertificate
                    // call on each axis sets the active key (used for new
                    // tokens); every subsequent call on the same axis adds a
                    // validation/decryption-only key for artifacts issued
                    // under a previous cert during the rotation overlap
                    // window. (OpenIddict actually re-sorts each credential
                    // list by X.509 expiration once configuration is
                    // finalized and picks the furthest-expiring cert as
                    // active, rather than literally the first call — but
                    // that always agrees with "first call" here in practice,
                    // because a freshly rotated cert is generated with a
                    // later expiration than the one it's replacing. See
                    // OpenIddictServerConfiguration's Compare/Sort of
                    // EncryptionCredentials / SigningCredentials.)
                    var signingCert = LoadCertificate(settings.SigningCertificatePath);
                    options.AddSigningCertificate(signingCert);

                    if (settings.PreviousSigningCertificatePaths is { Length: > 0 } previousSigningPaths)
                    {
                        foreach (var previousPath in previousSigningPaths)
                        {
                            if (string.IsNullOrWhiteSpace(previousPath)) continue;
                            var previousCert = LoadCertificate(previousPath);
                            options.AddSigningCertificate(previousCert);
                        }
                    }

                    // Separate encryption cert when configured; falls back
                    // to the signing cert (legacy behaviour) otherwise. A
                    // production-grade deployment SHOULD provide a distinct
                    // path so a key compromise on one axis doesn't compromise
                    // the other.
                    var encryptionCert = !string.IsNullOrEmpty(settings.EncryptionCertificatePath)
                        ? LoadCertificate(settings.EncryptionCertificatePath)
                        : signingCert;
                    options.AddEncryptionCertificate(encryptionCert);

                    // Rotation overlap for the encryption axis (issue #125),
                    // symmetric to PreviousSigningCertificatePaths above.
                    // Without this, replacing encryption.pfx instantly fails
                    // every live JWE-wrapped authorization code, device code,
                    // and refresh token instance-wide with no grace window.
                    if (settings.PreviousEncryptionCertificatePaths is { Length: > 0 } previousEncryptionPaths)
                    {
                        foreach (var previousPath in previousEncryptionPaths)
                        {
                            if (string.IsNullOrWhiteSpace(previousPath)) continue;
                            var previousCert = LoadCertificate(previousPath);
                            options.AddEncryptionCertificate(previousCert);
                        }
                    }
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
                options.AddEventHandler(RealmAuthorizationResponseIssuerHandler.Descriptor);
                options.AddEventHandler(RealmScopesSupportedHandler.Descriptor);
                options.AddEventHandler(TokenEndpointAuthMethodsSupportedHandler.Descriptor);
                options.AddEventHandler(DcrRegistrationEndpointHandler.Descriptor);
                options.AddEventHandler(CimdMetadataDocumentSupportedHandler.Descriptor);
                options.AddEventHandler(AccessTokenTypeHandler.Descriptor);
                options.AddEventHandler(TokenMintMetricHandler.Descriptor);
                options.AddEventHandler(ResourceIndicatorHandler.Descriptor);
                options.AddEventHandler(DcrAudienceContainmentHandler.Descriptor);
                options.AddEventHandler(DcrLastUsedTrackerHandler.Descriptor);
                options.AddEventHandler(RealmSigningKeyHandler.Descriptor);
                options.AddEventHandler(RealmJwksHandler.Descriptor);
                options.AddEventHandler(RealmTokenValidationHandler.Descriptor);
                options.AddEventHandler(RefreshTokenReuseAuditHandler.Descriptor);
                options.AddEventHandler(RealmClaimHandler.Descriptor);
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
                // Multi-realm: accept the active realm's keys + issuer when a custom
                // resource endpoint (ADR-0009 native passkey enroll) is protected by
                // the validation/Bearer scheme — the mirror of RealmTokenValidationHandler
                // on the server pipeline. Without it a realm-signed access token is
                // rejected as invalid_token ("issuer not valid", ID2088).
                options.AddEventHandler(RealmValidationTokenHandler.Descriptor);
            });

        return services;
    }

    /// <summary>
    /// Loads an X509 certificate from a passwordless PFX file. Modgud
    /// follows the <c>cocoar-secrets</c> CLI convention: the private key
    /// is protected by file-system permissions (<c>0600</c> on Linux), not
    /// by a PFX password. To convert a password-protected PFX received
    /// from elsewhere, use
    /// <c>cocoar-secrets convert-cert -i in.pfx --ipass &lt;old&gt; -o out.pfx</c>.
    /// </summary>
    private static System.Security.Cryptography.X509Certificates.X509Certificate2 LoadCertificate(string path)
        => System.Security.Cryptography.X509Certificates.X509CertificateLoader
            .LoadPkcs12FromFile(path, password: ReadOnlySpan<char>.Empty);
}
