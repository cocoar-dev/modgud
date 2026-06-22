using Microsoft.AspNetCore.Http;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Restricts JWT signature validation at the IdP boundary to the keys of
/// the active realm only. Without this, a token signed for realm A would
/// also validate against realm B's UserInfo endpoint as long as realm A's
/// key happened to be loaded somewhere in the global pool — defeating the
/// point of having per-realm keys in the first place.
///
/// <para>
/// The discriminator is the token TYPE, mirroring
/// <see cref="RealmSigningKeyHandler"/>: only <c>access_token</c> and
/// <c>id_token</c> are signed with the realm key, so only their signatures
/// must be validated against it. Authorization codes, refresh tokens and
/// device codes are signed with the global pool and validated by the IdP
/// itself — we leave their key set untouched.
/// </para>
///
/// <para>
/// This is NOT a reference-vs-JWT distinction. With
/// <c>UseReferenceAccessTokens()</c> an access token is delivered to the
/// client as an opaque reference, but its payload is still persisted as a
/// realm-signed JWT and re-validated on the way through
/// <c>/connect/userinfo</c> + <c>/connect/introspect</c>. An earlier version
/// keyed off <c>IsReferenceToken</c> and skipped exactly this case, leaving
/// the global keys in place → <c>invalid_token</c> (OpenIddict ID2090,
/// "signing key not found"). Reference REFRESH tokens, by contrast, stay
/// global-signed, so their <c>access_token</c>-free <c>ValidTokenTypes</c>
/// correctly falls through the guard.
/// </para>
///
/// <para>
/// The companion <see cref="RealmSigningKeyHandler"/> ensures issued tokens
/// carry the realm's key; this handler is the inverse — accept ONLY the
/// realm's key on incoming token validation. Together they are the
/// crypto isolation gate.
/// </para>
/// </summary>
public sealed class RealmTokenValidationHandler : IOpenIddictServerHandler<ValidateTokenContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
            .UseScopedHandler<RealmTokenValidationHandler>()
            // Run AFTER the stock handlers that build the default
            // TokenValidationParameters from Options.SigningCredentials, so
            // we can replace the IssuerSigningKeys list with realm-only keys.
            .SetOrder(Protection.ValidateIdentityModelToken.Descriptor.Order - 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IRealmKeyStore _keyStore;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public RealmTokenValidationHandler(IRealmKeyStore keyStore, IHttpContextAccessor httpContextAccessor)
    {
        _keyStore = keyStore;
        _httpContextAccessor = httpContextAccessor;
    }

    // RFC 8693 token-type URIs OpenIddict stamps on tokens it issues. Only
    // these two are signed with the realm key (see RealmSigningKeyHandler),
    // so only their validation may install realm-only verification keys.
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";
    private const string IdTokenType = "urn:ietf:params:oauth:token-type:id_token";

    public async ValueTask HandleAsync(ValidateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TokenValidationParameters is null) return;

        // Install realm-only verification keys ONLY when an access token or
        // id token is among the acceptable types for this validation:
        //  - /connect/userinfo accepts {access_token}
        //  - /connect/introspect, /connect/token, /connect/revoke accept the
        //    full generic set (which includes access_token)
        //  - identity-token validation accepts {id_token}
        // Endpoints that accept NEITHER (e.g. a {client_assertion}-only
        // validation, whose JWS is signed by the CLIENT's key — not the realm
        // key) are left with their stock key set.
        //
        // This is deliberately keyed on token TYPE, not on IsReferenceToken.
        // With UseReferenceAccessTokens the access token is delivered as an
        // opaque reference, but ValidateReferenceTokenIdentifier swaps in its
        // realm-signed JWT payload, which ValidateIdentityModelToken then
        // verifies against these keys. The previous IsReferenceToken guard
        // skipped that case → ID2090 at userinfo + introspect.
        if (!context.ValidTokenTypes.Contains(AccessTokenType) &&
            !context.ValidTokenTypes.Contains(IdTokenType))
        {
            return;
        }

        var slug = TenantContext.Current;
        var keys = await _keyStore.GetVerificationKeysAsync(slug);

        // Replace the trusted-keys list with realm-only keys.
        context.TokenValidationParameters.IssuerSigningKeys = keys;

        // Mirror the per-realm issuer story from C3c on the validation
        // side. Tokens issued through Modgud carry iss = request
        // BaseUri (e.g. https://realm-a.example.com); the stock validator
        // would otherwise compare against the global Options.Issuer
        // (https://auth.example.com) and reject with invalid_token. Set
        // ValidIssuer to the active request's BaseUri so the validator
        // accepts the realm-specific iss it just issued.
        //
        // ADR-0011: on an Application subdomain, tokens were minted with the
        // tenant CANONICAL issuer (see RealmSigningKeyHandler/CanonicalIssuer),
        // so the validator must accept that same canonical iss here — otherwise
        // a subdomain Bearer call (e.g. passkey enroll) rejects its own token.
        // Plain realm hosts are unchanged (Resolve returns BaseUri).
        if (context.BaseUri is not null)
        {
            var issuerUri = CanonicalIssuer.Resolve(context.BaseUri, _httpContextAccessor.HttpContext)
                            ?? context.BaseUri;
            var realmIssuer = issuerUri.AbsoluteUri.TrimEnd('/');
            context.TokenValidationParameters.ValidIssuer = realmIssuer;
            // Also keep the trailing-slash variant in the valid-issuers set
            // because OpenIddict's signing path emits with the slash and
            // some downstream handlers compare verbatim.
            context.TokenValidationParameters.ValidIssuers = new[]
            {
                realmIssuer,
                realmIssuer + "/",
            };
        }
    }
}
