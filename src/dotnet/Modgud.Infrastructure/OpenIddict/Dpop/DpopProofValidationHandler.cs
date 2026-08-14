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

    private readonly IDpopReplayStore _replayStore;
    private readonly IDpopNonceStore _nonceStore;
    private readonly IQuerySession _querySession;

    public DpopProofValidationHandler(
        IDpopReplayStore replayStore, IDpopNonceStore nonceStore, IQuerySession querySession)
    {
        _replayStore = replayStore;
        _nonceStore = nonceStore;
        _querySession = querySession;
    }

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

        var header = httpRequest.Headers[DpopConstants.HeaderName];
        if (header.Count == 0)
        {
            // No proof. Offered-not-required by default → ordinary bearer token.
            // But a client flagged RequireDpop must present one (RFC 9449 §5): a
            // tokenless request is rejected rather than silently downgraded.
            var noProofPolicy = await GetClientDpopPolicyAsync(context.ClientId, context.CancellationToken);
            if (noProofPolicy.RequireDpop)
                context.Reject(DpopConstants.InvalidProofError,
                    "This client requires a DPoP proof on the token request.");
            return;
        }

        if (header.Count > 1)
        {
            context.Reject(DpopConstants.InvalidProofError, "Multiple DPoP proofs are not allowed.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var htu = context.Transaction.RequestUri?.ToString()
            ?? $"{httpRequest.Scheme}://{httpRequest.Host}{httpRequest.Path}";

        var result = DpopProofValidator.Validate(header.ToString(), httpRequest.Method, htu, now);
        if (!result.IsValid)
        {
            context.Reject(DpopConstants.InvalidProofError, $"The DPoP proof is not valid ({result.Error}).");
            return;
        }

        // Server nonce (RFC 9449 §8-9): a client flagged RequireDpopNonce must carry
        // a valid, server-issued nonce in its proof. The first proof carries none —
        // answer with a fresh DPoP-Nonce header + use_dpop_nonce so the client
        // retries. Checked before the replay ledger so a nonce-less first proof
        // doesn't burn a jti. (Structurally invalid proofs are already rejected
        // above, so we never mint a nonce for garbage.)
        var proofPolicy = await GetClientDpopPolicyAsync(context.ClientId, context.CancellationToken);
        if (proofPolicy.RequireDpopNonce)
        {
            var presentedNonce = ReadProofNonce(header.ToString());
            if (presentedNonce is null ||
                !await _nonceStore.IsValidAsync(presentedNonce, now, context.CancellationToken))
            {
                var freshNonce = await _nonceStore.IssueAsync(now, context.CancellationToken);
                httpRequest.HttpContext.Response.Headers[DpopConstants.NonceHeaderName] = freshNonce;
                context.Reject(DpopConstants.UseNonceError,
                    "A DPoP nonce is required; retry the proof with the nonce from the DPoP-Nonce header.");
                return;
            }
        }

        // Replay: the jti must be first-seen within its acceptance window.
        var expiresAt = (result.IssuedAt ?? now)
            + DpopProofValidator.DefaultMaxAge + DpopProofValidator.DefaultClockSkew;
        var firstUse = await _replayStore.TryRecordAsync(result.Jti!, expiresAt, now, context.CancellationToken);
        if (!firstUse)
        {
            context.Reject(DpopConstants.InvalidProofError, "The DPoP proof has already been used.");
            return;
        }

        // Hand the binding to the downstream handlers via HttpContext.Items
        // (shared across every event for this one request). At the token
        // endpoint the claim-stamping/token-type handlers read it; at the
        // device-authorization endpoint DpopDeviceCodeBindingCaptureHandler
        // persists it against the minted device code.
        httpRequest.HttpContext.Items[DpopConstants.HttpContextJktKey] = result.Jkt;
    }

    /// <summary>
    /// Reads the presenting client's admin-set <see cref="OAuthApplicationPropertyKeys.RequireDpop"/>
    /// flag. Mirrors <c>AccessTokenTypeHandler</c>'s per-client query. CIMD clients
    /// are non-persisted and never carry the flag, so a query miss ⇒ not required.
    /// </summary>
    private async ValueTask<(bool RequireDpop, bool RequireDpopNonce)> GetClientDpopPolicyAsync(
        string? clientId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId)) return (false, false);

        var app = await _querySession.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(a => a.ClientId == clientId && !a.IsDeleted, ct);
        if (app is null) return (false, false);

        return (
            ReadBool(app.Properties, OAuthApplicationPropertyKeys.RequireDpop),
            ReadBool(app.Properties, OAuthApplicationPropertyKeys.RequireDpopNonce));
    }

    /// <summary>Reads the <c>nonce</c> claim out of a proof's payload without
    /// re-validating it (the proof's signature/claims were already checked).</summary>
    private static string? ReadProofNonce(string proof)
    {
        var parts = proof.Split('.');
        if (parts.Length != 3 || parts[1].Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("nonce", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    // Local decode of a persisted boolean property. Marten may hand the value back
    // as a boxed bool (Newtonsoft) or a JsonElement (STJ) depending on the
    // configured serializer, so accept both — mirrors OAuthAdminMapping.GetBoolProp
    // (kept local to avoid an Application-layer coupling for a 6-line read).
    private static bool ReadBool(IDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null) return false;
        return raw switch
        {
            bool b => b,
            JsonElement e when e.ValueKind is JsonValueKind.True => true,
            _ => false,
        };
    }
}
