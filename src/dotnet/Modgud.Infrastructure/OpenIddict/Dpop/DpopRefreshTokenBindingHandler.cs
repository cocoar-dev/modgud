using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Infrastructure.OpenIddict.Dpop;

/// <summary>
/// Enforces the DPoP binding of a refresh token when it is redeemed (RFC 9449
/// §5) — the half that stops a stolen bound refresh token from being replayed
/// without the private key.
///
/// <para>Runs on the refresh grant only. The incoming refresh token's bound-key
/// thumbprint is carried on <see cref="ProcessSignInContext.Principal"/> as
/// <see cref="DpopConstants.RefreshBindingClaimType"/> — <c>CreateClaimsPrincipalAsync</c>
/// re-copies it from the rehydrated reference-token principal, mirroring the
/// session-group carrier. If the token is bound, the proof presented at this
/// refresh (validated earlier by <see cref="DpopProofValidationHandler"/>, its
/// thumbprint stashed on <c>HttpContext.Items</c>) MUST match it; a missing or
/// mismatched proof is rejected with <c>invalid_dpop_proof</c>. An unbound refresh
/// token carries no thumbprint and is unaffected.</para>
///
/// <para>The <b>persistence</b> half — stamping the thumbprint onto each minted
/// refresh token so it survives rotation — lives in
/// <see cref="DpopRefreshTokenBindingStampHandler"/> (a <see cref="GenerateTokenContext"/>
/// handler, because OpenIddict serialises each token from its own
/// <c>GenerateTokenContext.Principal</c>, not from the sign-in principal).</para>
/// </summary>
public sealed class DpopRefreshTokenBindingHandler : IOpenIddictServerHandler<ProcessSignInContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<ProcessSignInContext>()
            .UseScopedHandler<DpopRefreshTokenBindingHandler>()
            // After DpopProofValidationHandler (GenerateAccessToken.Order - 10) has
            // stashed the proof thumbprint, still before any token is generated.
            .SetOrder(GenerateAccessToken.Descriptor.Order - 5)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IHttpContextAccessor _httpContextAccessor;

    public DpopRefreshTokenBindingHandler(IHttpContextAccessor httpContextAccessor) =>
        _httpContextAccessor = httpContextAccessor;

    public ValueTask HandleAsync(ProcessSignInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.EndpointType != OpenIddictServerEndpointType.Token)
            return default;
        if (context.Request?.IsRefreshTokenGrantType() != true)
            return default;

        // Bound key the incoming refresh token was issued against (re-copied onto
        // the principal by CreateClaimsPrincipalAsync). Absent ⇒ unbound token ⇒
        // nothing to enforce.
        var boundJkt = context.Principal?.GetClaim(DpopConstants.RefreshBindingClaimType);
        if (string.IsNullOrEmpty(boundJkt))
            return default;

        var presentedJkt = ReadPresentedJkt();
        if (string.IsNullOrEmpty(presentedJkt))
        {
            context.Reject(DpopConstants.InvalidProofError,
                "This refresh token is DPoP-bound; a DPoP proof is required to redeem it.");
            return default;
        }

        if (!string.Equals(presentedJkt, boundJkt, StringComparison.Ordinal))
        {
            context.Reject(DpopConstants.InvalidProofError,
                "The DPoP proof key does not match the key this refresh token is bound to.");
        }

        return default;
    }

    private string? ReadPresentedJkt()
    {
        var items = _httpContextAccessor.HttpContext?.Items;
        return items is not null &&
            items.TryGetValue(DpopConstants.HttpContextJktKey, out var raw) && raw is string s && s.Length > 0
            ? s
            : null;
    }
}

/// <summary>
/// Binds each minted refresh token to the DPoP proof key (RFC 9449 §5) by
/// stamping the validated proof's thumbprint onto the refresh token as the
/// internal <see cref="DpopConstants.RefreshBindingClaimType"/> carrier.
///
/// <para>Hooks <see cref="GenerateTokenContext"/> (not
/// <see cref="ProcessSignInContext"/>) because OpenIddict 7 serialises each token
/// from its own <c>GenerateTokenContext.Principal</c> — a claim added on the
/// sign-in principal never reaches the persisted refresh token, mirroring
/// <see cref="DpopConfirmationClaimHandler"/>. The carrier has no token
/// destination, so it lives only in the server-side refresh token; on redemption
/// it is restored and re-copied so the rotated token stays bound and the whole
/// chain remains sender-constrained. When no proof was presented (no thumbprint
/// on <c>HttpContext.Items</c>) the refresh token is left unbound.</para>
/// </summary>
public sealed class DpopRefreshTokenBindingStampHandler : IOpenIddictServerHandler<GenerateTokenContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<GenerateTokenContext>()
            .UseScopedHandler<DpopRefreshTokenBindingStampHandler>()
            // Same window as DpopConfirmationClaimHandler: principal populated,
            // token not yet serialised.
            .SetOrder(Protection.AttachSecurityCredentials.Descriptor.Order + 130)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IHttpContextAccessor _httpContextAccessor;

    public DpopRefreshTokenBindingStampHandler(IHttpContextAccessor httpContextAccessor) =>
        _httpContextAccessor = httpContextAccessor;

    public ValueTask HandleAsync(GenerateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The binding lives on the refresh token only (RFC 9449 §5). The access
        // token's own binding is cnf.jkt, stamped by DpopConfirmationClaimHandler.
        if (context.TokenType is not "urn:ietf:params:oauth:token-type:refresh_token")
            return default;

        var items = _httpContextAccessor.HttpContext?.Items;
        if (items is null ||
            !items.TryGetValue(DpopConstants.HttpContextJktKey, out var raw) ||
            raw is not string jkt || jkt.Length == 0)
        {
            return default; // no proof → an ordinary, unbound refresh token
        }

        if (context.Principal?.Identity is ClaimsIdentity identity &&
            !identity.HasClaim(c => c.Type == DpopConstants.RefreshBindingClaimType))
        {
            // No destinations: excluded from access/id tokens, persisted in the
            // refresh token so the binding survives redemption + rotation.
            identity.AddClaim(new Claim(DpopConstants.RefreshBindingClaimType, jkt));
        }

        return default;
    }
}
