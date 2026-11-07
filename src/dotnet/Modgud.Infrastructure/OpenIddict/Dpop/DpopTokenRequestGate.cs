using System.Buffers.Text;
using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Http;
using Modgud.Domain.OAuth.Applications;

namespace Modgud.Infrastructure.OpenIddict.Dpop;

/// <summary>The DPoP decision for one token request: refused (<see cref="Error"/> set) or accepted,
/// with the proof key's thumbprint when a proof was presented.</summary>
public sealed record DpopGateOutcome(string? Error, string? Description, string? Jkt)
{
    public bool Rejected => Error is not null;
}

/// <summary>
/// RFC 9449 §5 checks of a token request's DPoP proof — the client's RequireDpop / RequireDpopNonce
/// policy, the proof itself and the replay ledger — decided ONCE per request. G-B4-03: endpoints that
/// store state before they sign in (terminal enrollment, staffing redeem and step-up) ask first, so
/// a proof the pipeline would refuse is refused before anything is committed. The later
/// <see cref="DpopProofValidationHandler"/> gets the same answer from the request's cache instead of
/// recording the jti a second time (which would refuse every such request as a replay).
/// </summary>
public interface IDpopTokenRequestGate
{
    ValueTask<DpopGateOutcome> EvaluateAsync(HttpContext http, string? clientId, string htu, CancellationToken ct);
}

public sealed class DpopTokenRequestGate(
    IDpopReplayStore replayStore, IDpopNonceStore nonceStore, IQuerySession querySession) : IDpopTokenRequestGate
{
    private const string OutcomeKey = "modgud:dpop:gate-outcome";

    public async ValueTask<DpopGateOutcome> EvaluateAsync(HttpContext http, string? clientId, string htu, CancellationToken ct)
    {
        if (http.Items[OutcomeKey] is DpopGateOutcome decided) return decided;
        var outcome = await DecideAsync(http, clientId, htu, ct);
        http.Items[OutcomeKey] = outcome;
        // Hand the binding to the downstream handlers via HttpContext.Items (shared across every
        // event for this one request): the claim-stamping and token-type handlers read it at the
        // token endpoint, DpopDeviceCodeBindingCaptureHandler at the device-authorization endpoint.
        if (!outcome.Rejected && outcome.Jkt is not null)
            http.Items[DpopConstants.HttpContextJktKey] = outcome.Jkt;
        return outcome;
    }

    private async ValueTask<DpopGateOutcome> DecideAsync(HttpContext http, string? clientId, string htu, CancellationToken ct)
    {
        var header = http.Request.Headers[DpopConstants.HeaderName];
        if (header.Count == 0)
        {
            // No proof. Offered-not-required by default → ordinary bearer token. A client flagged
            // RequireDpop must present one (RFC 9449 §5): rejected rather than silently downgraded.
            var noProofPolicy = await GetClientDpopPolicyAsync(clientId, ct);
            return noProofPolicy.RequireDpop
                ? Refuse(DpopConstants.InvalidProofError, "This client requires a DPoP proof on the token request.")
                : new DpopGateOutcome(null, null, null);
        }

        if (header.Count > 1)
            return Refuse(DpopConstants.InvalidProofError, "Multiple DPoP proofs are not allowed.");

        var now = DateTimeOffset.UtcNow;
        var result = DpopProofValidator.Validate(header.ToString(), http.Request.Method, htu, now);
        if (!result.IsValid)
            return Refuse(DpopConstants.InvalidProofError, $"The DPoP proof is not valid ({result.Error}).");

        // Server nonce (RFC 9449 §8-9): a client flagged RequireDpopNonce must carry a valid,
        // server-issued nonce. The first proof carries none — answer with a fresh DPoP-Nonce header +
        // use_dpop_nonce so the client retries. Checked before the replay ledger so a nonce-less first
        // proof doesn't burn a jti. (Structurally invalid proofs are already rejected above.)
        var proofPolicy = await GetClientDpopPolicyAsync(clientId, ct);
        if (proofPolicy.RequireDpopNonce)
        {
            var presentedNonce = ReadProofNonce(header.ToString());
            if (presentedNonce is null || !await nonceStore.IsValidAsync(presentedNonce, now, ct))
            {
                http.Response.Headers[DpopConstants.NonceHeaderName] = await nonceStore.IssueAsync(now, ct);
                return Refuse(DpopConstants.UseNonceError,
                    "A DPoP nonce is required; retry the proof with the nonce from the DPoP-Nonce header.");
            }
        }

        // Replay: the jti must be first-seen within its acceptance window.
        var expiresAt = (result.IssuedAt ?? now) + DpopProofValidator.DefaultMaxAge + DpopProofValidator.DefaultClockSkew;
        if (!await replayStore.TryRecordAsync(result.Jti!, expiresAt, now, ct))
            return Refuse(DpopConstants.InvalidProofError, "The DPoP proof has already been used.");

        return new DpopGateOutcome(null, null, result.Jkt);
    }

    private static DpopGateOutcome Refuse(string error, string description) => new(error, description, null);

    /// <summary>
    /// Reads the presenting client's admin-set <see cref="OAuthApplicationPropertyKeys.RequireDpop"/> /
    /// <see cref="OAuthApplicationPropertyKeys.RequireDpopNonce"/> flags. CIMD clients are non-persisted
    /// and never carry them, so a query miss ⇒ not required.
    /// </summary>
    private async ValueTask<(bool RequireDpop, bool RequireDpopNonce)> GetClientDpopPolicyAsync(string? clientId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId)) return (false, false);
        var app = await querySession.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(a => a.ClientId == clientId && !a.IsDeleted, ct);
        if (app is null) return (false, false);
        return (ReadBool(app.Properties, OAuthApplicationPropertyKeys.RequireDpop),
                ReadBool(app.Properties, OAuthApplicationPropertyKeys.RequireDpopNonce));
    }

    /// <summary>Reads the <c>nonce</c> claim out of a proof's payload without re-validating it.</summary>
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

    // Marten may hand a persisted boolean back as a boxed bool or a JsonElement, depending on the
    // configured serializer, so accept both — mirrors OAuthAdminMapping.GetBoolProp.
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
