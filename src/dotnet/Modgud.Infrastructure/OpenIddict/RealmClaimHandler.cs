using System.Security.Claims;
using Modgud.Infrastructure.Persistence.Tenancy;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Adds a <c>realm</c> claim to every access-token and id-token before
/// OpenIddict serialises them. Lets resource servers verify that an
/// incoming token was minted for THEIR realm — defence in depth on top
/// of the per-realm issuer (C3c) and per-realm signing key (C3b).
///
/// <para>
/// This is the parallel-claim flavour of OAUTH-11 rather than changing
/// the standard <c>sub</c> claim format. The <c>sub</c> stays a stable,
/// realm-local user identifier (matches OIDC core §5.4 and what consumer
/// libraries expect). The <c>realm</c> claim is the explicit qualifier
/// that resource servers can read when their identity-cache key needs to
/// be tenant-aware: <c>(realm, sub)</c> instead of just <c>sub</c>.
/// </para>
///
/// <para>
/// Hooks <see cref="GenerateTokenContext"/> rather than
/// <see cref="ProcessSignInContext"/> because OpenIddict 7's serialization
/// path actually reads the principal off the GenerateTokenContext.Principal
/// property — additions made to ProcessSignInContext.AccessTokenPrincipal
/// don't reliably propagate through the per-token regeneration. Filtering
/// on TokenType keeps us out of the IdP-internal codes / refresh-tokens
/// the same way <see cref="RealmSigningKeyHandler"/> does.
/// </para>
/// </summary>
public sealed class RealmClaimHandler : IOpenIddictServerHandler<GenerateTokenContext>
{
    public const string ClaimType = "realm";

    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<GenerateTokenContext>()
            .UseSingletonHandler<RealmClaimHandler>()
            // Same slot as RealmSigningKeyHandler — late enough that the
            // Principal is fully populated, early enough that the token
            // serializer hasn't run yet.
            .SetOrder(Protection.AttachSecurityCredentials.Descriptor.Order + 110)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public ValueTask HandleAsync(GenerateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Only add the claim to tokens that LEAVE the IdP boundary. Auth
        // codes and refresh tokens are IdP-internal — no consumer ever
        // reads claims off them — and adding the claim there just bloats
        // the persisted state.
        if (context.TokenType is not (
            "urn:ietf:params:oauth:token-type:access_token" or
            "urn:ietf:params:oauth:token-type:id_token"))
        {
            return default;
        }

        var slug = TenantContext.Current;
        var destination = context.TokenType is "urn:ietf:params:oauth:token-type:access_token"
            ? OpenIddictConstants.Destinations.AccessToken
            : OpenIddictConstants.Destinations.IdentityToken;

        if (context.Principal?.Identity is ClaimsIdentity identity)
        {
            // Idempotent — if a claim for the same realm is already present
            // (refresh-token round-trip, principal reuse across regen
            // passes), skip so we don't duplicate.
            if (!identity.HasClaim(c => c.Type == ClaimType && c.Value == slug))
            {
                var claim = new Claim(ClaimType, slug);
                claim.SetDestinations(destination);
                identity.AddClaim(claim);
            }
        }

        return default;
    }
}
