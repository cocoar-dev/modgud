using System.Buffers.Text;
using System.Text.Json;
using Microsoft.AspNetCore; // OpenIddictServerAspNetCoreHelpers.GetHttpRequest(this transaction)
using Marten;
using Modgud.Domain.OAuth.Applications;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Infrastructure.OpenIddict.Dpop;

/// <summary>
/// Validates a DPoP proof presented at the token endpoint (RFC 9449 §5) and, on
/// success, stashes the proof key's thumbprint on the transaction so the
/// downstream handlers can bind the issued access token to it
/// (<see cref="DpopConfirmationClaimHandler"/>) and announce it as a DPoP token
/// (<see cref="DpopTokenTypeHandler"/>).
///
/// <para>
/// DPoP is <b>offered, not required</b> by default: a token request with no
/// <c>DPoP</c> header is untouched and yields an ordinary bearer token. A request
/// that DOES carry a proof must present a valid, non-replayed one — otherwise the
/// grant is rejected with <c>invalid_dpop_proof</c>. A client flagged
/// <see cref="OAuthApplicationPropertyKeys.RequireDpop"/> (#118) inverts the
/// default for itself: a tokenless request is then rejected with the same error.
/// </para>
///
/// <para>The checks themselves live in <see cref="IDpopTokenRequestGate"/>, decided once per
/// request: an endpoint that stores state before it signs in has already asked (G-B4-03), and this
/// handler then gets the same answer instead of recording the jti twice.</para>
///
/// <para>Scoped because it depends on the tenant-scoped replay store + query
/// session. Runs just before the access token is generated, mirroring
/// <c>ResourceIndicatorHandler</c>.</para>
/// </summary>
public sealed class DpopProofValidationHandler : IOpenIddictServerHandler<ProcessSignInContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<ProcessSignInContext>()
            .UseScopedHandler<DpopProofValidationHandler>()
            // Before RedeemTokenEntry consumes the incoming authorization code /
            // refresh token — so a rejection here (invalid proof, replay, or the
            // use_dpop_nonce challenge) does NOT burn the code, and the client can
            // retry the nonce handshake with the same code (RFC 9449 §8-9). Still
            // well before the access token is generated, so the cnf.jkt binding
            // stashed for the GenerateToken handlers is in place. (-2, not -1:
            // DpopDeviceCodeBindingHandler needs the stashed thumbprint and must
            // itself still run before the redeem.)
            .SetOrder(RedeemTokenEntry.Descriptor.Order - 2)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IDpopTokenRequestGate _gate;

    public DpopProofValidationHandler(IDpopTokenRequestGate gate) => _gate = gate;

    public async ValueTask HandleAsync(ProcessSignInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Token endpoint (authorization_code / refresh_token / client_credentials /
        // native grants all sign in here) PLUS the device-authorization endpoint:
        // a proof presented with the device request is validated here so
        // DpopDeviceCodeBindingStampHandler can bind the minted device code to the
        // proof key (MG-FT spike; RFC 9449 applied to RFC 8628).
        if (context.EndpointType is not (OpenIddictServerEndpointType.Token
            or OpenIddictServerEndpointType.DeviceAuthorization))
            return;

        var httpRequest = context.Transaction.GetHttpRequest();
        if (httpRequest is null)
            return;

        var htu = context.Transaction.RequestUri?.ToString()
            ?? $"{httpRequest.Scheme}://{httpRequest.Host}{httpRequest.Path}";
        var outcome = await _gate.EvaluateAsync(httpRequest.HttpContext, context.ClientId, htu, context.CancellationToken);
        if (outcome.Rejected)
            context.Reject(outcome.Error!, outcome.Description);
    }
}
