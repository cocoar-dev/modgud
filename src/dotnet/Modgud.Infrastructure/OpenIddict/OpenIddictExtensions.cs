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
        services.AddScoped<ITokenMintClientTypeResolver, TokenMintClientTypeResolver>();
        services.AddHttpClient(Cimd.CimdClientResolver.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(5);
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
                Modgud.Infrastructure.Http.SsrfSafeHttpHandlerFactory.Create("CIMD metadata fetch"));

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
                    .SetEndUserVerificationEndpointUris("connect/verify")
                    // RFC 9126 (#118) — Pushed Authorization Requests. The client
                    // POSTs the full authorization request to this back-channel
                    // endpoint (authenticating if confidential) and receives a
                    // one-time `request_uri` to hand to /connect/authorize, so
                    // request parameters never traverse the browser/redirect
                    // chain. OpenIddict handles the endpoint natively — it stores
                    // each pushed request in the tenant token store and resolves
                    // the request_uri at the authorize endpoint — and advertises
                    // `pushed_authorization_request_endpoint` in discovery. PAR is
                    // offered, not mandated: RequirePushedAuthorizationRequests()
                    // is deliberately NOT set, so ordinary browser + device flows
                    // keep working. The RFC 8707 `resource=` a client pushes is
                    // stored with the request and validated at token time by the
                    // existing ResourceIndicatorHandler, exactly as for a direct
                    // authorize request.
                    .SetPushedAuthorizationEndpointUris("connect/par");

                // RFC 8414 (#136) — also serve the authorization-server
                // metadata at the bare `/.well-known/oauth-authorization-server`
                // alias, not only the OpenID Connect discovery document. A
                // spec-strict MCP client may probe only the RFC 8414 path and
                // never fall back to `openid-configuration`; without the alias
                // it can't discover the realm. Same document either way — the
                // per-realm handlers (RealmIssuerHandler, RealmScopesSupportedHandler,
                // DcrRegistrationEndpointHandler, CimdMetadataDocumentSupportedHandler)
                // hook HandleConfigurationRequest, which is path-agnostic. This
                // call REPLACES the endpoint's URI set rather than appending, so
                // the OIDC default must be listed explicitly to keep it live.
                options.SetConfigurationEndpointUris(
                    "/.well-known/openid-configuration",
                    "/.well-known/oauth-authorization-server");

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

                // OAUTH-10 — preserve replay detection while tolerating the
                // unavoidable response-loss window of rolling refresh tokens.
                // OpenIddict marks the old token Redeemed before the response is
                // written; a 500, disconnect, or process loss after that point
                // leaves a legitimate client with only the old token. During
                // this short window a retry may mint another replacement. Once
                // it expires, the stock ValidateTokenEntry handler rejects the
                // redeemed token and revokes its authorization's token family;
                // RefreshTokenReuseAuditHandler records that real replay first.
                options.SetRefreshTokenReuseLeeway(TimeSpan.FromSeconds(30));
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

                // MG-FT-05 — the staffing grant (passkey tap on an
                // enrolled terminal opens a StaffingSession). Same double gate:
                // the Features.PositionTerminals flag in the dispatch branch,
                // and the gt: permission only terminal-managed clients carry.
                options.AllowCustomFlow(Modgud.Domain.PositionTerminals.PositionGrantTypes.StaffingSession);

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
                options.AddEventHandler(BackChannelLogoutMetadataHandler.Descriptor);
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

                // DPoP (RFC 9449, #118) — validate a proof at the token endpoint,
                // bind the access token to the proof key (cnf.jkt), and announce
                // token_type=DPoP. Offered, not required; clients that send no
                // proof are unaffected.
                options.AddEventHandler(Dpop.DpopProofValidationHandler.Descriptor);
                options.AddEventHandler(Dpop.DpopDeviceCodeBindingHandler.Descriptor);
                options.AddEventHandler(Dpop.DpopDeviceCodeBindingCaptureHandler.Descriptor);
                options.AddEventHandler(Dpop.DpopRefreshTokenBindingHandler.Descriptor);
                options.AddEventHandler(Dpop.DpopConfirmationClaimHandler.Descriptor);
                options.AddEventHandler(Dpop.DpopRefreshTokenBindingStampHandler.Descriptor);
                options.AddEventHandler(Dpop.DpopTokenTypeHandler.Descriptor);
                options.AddEventHandler(Dpop.DpopDiscoveryMetadataHandler.Descriptor);
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

                // MG-FT-05 — see DpopValidationProofOfPossessionBypassHandler:
                // OpenIddict 7.6's stock PoP validation only understands
                // mTLS-bound tokens and hard-rejects our DPoP cnf.jkt tokens.
                options.AddEventHandler(Dpop.DpopValidationProofOfPossessionBypassHandler.Descriptor);
            });

        // DPoP proof replay store — tenant-scoped Marten session, so the jti
        // uniqueness check is shared across every instance on the same realm DB.
        services.AddScoped<Dpop.IDpopReplayStore, Dpop.MartenDpopReplayStore>();

        // DPoP server-nonce store (RFC 9449 §8-9) — same tenant-scoped Marten
        // backing as the replay store, so an issued nonce is honoured across every
        // instance on the realm DB.
        services.AddScoped<Dpop.IDpopNonceStore, Dpop.MartenDpopNonceStore>();

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
