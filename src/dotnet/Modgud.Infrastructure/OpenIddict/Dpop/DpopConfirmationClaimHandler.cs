using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Infrastructure.OpenIddict.Dpop;

/// <summary>
/// Stamps the <c>cnf</c> (confirmation) claim carrying the DPoP proof key's
/// thumbprint onto the access token (RFC 9449 §6), when
/// <see cref="DpopProofValidationHandler"/> validated a proof earlier in the same
/// transaction. The claim serialises as a nested object <c>{"jkt":"…"}</c>; a
/// resource server compares that <c>jkt</c> against the thumbprint of the proof
/// presented with the token to confirm possession.
///
/// <para>
/// Hooks <see cref="GenerateTokenContext"/> for the same reason as
/// <c>RealmClaimHandler</c> (OpenIddict 7 reads the principal off
/// <c>GenerateTokenContext.Principal</c> at serialisation), and stamps ONLY the
/// access token — <c>cnf</c> has no meaning on an id token. For reference tokens
/// the claim lands in the persisted payload JWT, surfaced to resource servers via
/// introspection.
/// </para>
/// </summary>
public sealed class DpopConfirmationClaimHandler : IOpenIddictServerHandler<GenerateTokenContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<GenerateTokenContext>()
            .UseScopedHandler<DpopConfirmationClaimHandler>()
            // Same window as RealmClaimHandler (+110): principal fully populated,
            // token not yet serialised.
            .SetOrder(Protection.AttachSecurityCredentials.Descriptor.Order + 120)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IHttpContextAccessor _httpContextAccessor;

    public DpopConfirmationClaimHandler(IHttpContextAccessor httpContextAccessor) =>
        _httpContextAccessor = httpContextAccessor;

    public ValueTask HandleAsync(GenerateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // cnf.jkt binds the access token only (RFC 9449 §6).
        if (context.TokenType is not "urn:ietf:params:oauth:token-type:access_token")
            return default;

        var items = _httpContextAccessor.HttpContext?.Items;
        if (items is null ||
            !items.TryGetValue(DpopConstants.HttpContextJktKey, out var raw) ||
            raw is not string jkt || jkt.Length == 0)
            return default;

        if (context.Principal?.Identity is ClaimsIdentity identity &&
            !identity.HasClaim(c => c.Type == DpopConstants.ConfirmationClaim))
        {
            // jkt is a base64url thumbprint (URL-safe alphabet only), so direct
            // interpolation into the JSON object is injection-safe.
            var json = $"{{\"{DpopConstants.JwkThumbprintMember}\":\"{jkt}\"}}";
            var claim = new Claim(
                DpopConstants.ConfirmationClaim, json, DpopConstants.JsonClaimValueType);
            claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken);
            identity.AddClaim(claim);
        }

        return default;
    }
}
