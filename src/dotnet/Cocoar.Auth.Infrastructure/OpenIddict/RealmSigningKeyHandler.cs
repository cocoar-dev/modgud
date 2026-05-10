using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Cocoar.Auth.Infrastructure.Realms;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Cocoar.Auth.Infrastructure.OpenIddict;

/// <summary>
/// Replaces OpenIddict's global signing-credential pool with the realm-specific
/// key on token generation. Runs AFTER <see cref="Protection.AttachSecurityCredentials"/>
/// — that handler unconditionally assigns from the global pool, so the only
/// way to win is to overwrite afterwards.
///
/// <para>
/// We only override for tokens that LEAVE the IdP boundary — access tokens
/// (consumed by resource servers) and id tokens (consumed by RPs). Authorization
/// codes, device codes, refresh tokens and request tokens are issued AND
/// validated entirely by the IdP itself, so signing them with a per-realm key
/// would require the validation path at /connect/token redemption to also
/// resolve per-realm keys — extra surface area for no isolation gain (those
/// tokens never reach an outside party).
/// </para>
/// </summary>
public sealed class RealmSigningKeyHandler : IOpenIddictServerHandler<GenerateTokenContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<GenerateTokenContext>()
            .UseScopedHandler<RealmSigningKeyHandler>()
            // Run AFTER the stock AttachSecurityCredentials handler. That
            // handler unconditionally assigns SigningCredentials from the
            // global pool — running before it would just have our value
            // overwritten. Adding 100 leaves room for any other custom
            // handler that wants to slot between us and the default.
            .SetOrder(Protection.AttachSecurityCredentials.Descriptor.Order + 100)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IRealmKeyStore _keyStore;
    private readonly ILogger<RealmSigningKeyHandler> _logger;

    public RealmSigningKeyHandler(IRealmKeyStore keyStore, ILogger<RealmSigningKeyHandler> logger)
    {
        _keyStore = keyStore;
        _logger = logger;
    }

    public async ValueTask HandleAsync(GenerateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // OpenIddict 7 sets context.TokenType to the RFC 8693 token-type
        // identifier URI ("urn:ietf:params:oauth:token-type:access_token" etc.),
        // NOT the short hint form. We match against the URIs that correspond
        // to tokens that LEAVE the IdP boundary — access tokens (consumed by
        // resource servers) and id tokens (consumed by RPs). Authorization
        // codes, device codes, refresh tokens etc. are validated by the IdP
        // itself at /connect/token redemption, so we leave their signing
        // material to the global pool.
        if (context.TokenType is not (
            "urn:ietf:params:oauth:token-type:access_token" or
            "urn:ietf:params:oauth:token-type:id_token"))
        {
            return;
        }

        var slug = TenantContext.Current;
        var creds = await _keyStore.GetActiveSigningCredentialsAsync(slug);
        context.SigningCredentials = creds;

        // C3c — per-realm issuer in tokens (OAUTH-01). The discovery document
        // already serves a realm-aware issuer (see RealmIssuerHandler); this
        // mirrors the same behaviour in the iss claim of issued JWTs so
        // resource servers fetching realm A's discovery + JWKS will only
        // accept JWTs whose iss matches realm A's URL. The stock pipeline
        // would otherwise stamp the global Options.Issuer onto every token,
        // which would happily round-trip across realms.
        //
        // Updating the principal's Claims.Private.Issuer claim is the
        // load-bearing step: OpenIddict's AttachTokenMetadata handler runs
        // after this one and copies that claim onto SecurityTokenDescriptor.Issuer
        // (overwriting anything we set there directly). We update the
        // descriptor too as a belt-and-braces guard against future ordering
        // changes.
        if (context.BaseUri is not null)
        {
            // Use the .NET Uri serialization verbatim (AbsoluteUri keeps
            // the trailing slash for host-only URIs) so the iss claim we
            // stamp onto tokens matches the issuer value Discovery
            // publishes. Trimming the slash here breaks OpenIdConnect
            // clients with "IDX10205: Issuer validation failed" because
            // Discovery's Uri-typed issuer field always serialises with
            // the trailing slash and we have no clean way to override
            // that on the discovery side.
            var realmIssuer = context.BaseUri.AbsoluteUri;
            context.Principal?.SetClaim(OpenIddictConstants.Claims.Private.Issuer, realmIssuer);
            if (context.SecurityTokenDescriptor is not null)
            {
                context.SecurityTokenDescriptor.Issuer = realmIssuer;
            }
        }

        _logger.LogDebug("Auth: signed {TokenType} for realm '{Slug}' with kid '{Kid}', issuer '{Issuer}'",
            context.TokenType, slug, creds.Key.KeyId,
            context.SecurityTokenDescriptor?.Issuer);
    }
}
