using Marten;
using Microsoft.AspNetCore.Http;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Domain.OAuth.Applications;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Identity;

/// <summary>
/// Resolves the effective WebAuthn RP ID for a passkey ceremony (ADR-0009 per-client
/// RP-ID). The single resolver shared by the native passkey LOGIN begin/redeem and
/// the native ENROLL begin/finish, so all of them agree on the RP ID for a given
/// client — the value a credential is enrolled under is byte-identical to what login
/// later demands.
///
/// <para>An OAuth client may carry an admin-set per-client RP ID
/// (<see cref="OAuthApplicationSettingKeys.WebAuthnRpId"/>). When present it is the
/// RP ID; otherwise — no <c>client_id</c>, an unknown client, or a CIMD/synthesized
/// client (which carries no <c>modgud:*</c> settings) — the realm's
/// <see cref="Modgud.Domain.Realms.Realm.PrimaryDomain"/> is used, i.e. today's
/// behaviour. A blank value is returned as-is and surfaces as the existing
/// <see cref="RelyingPartyUnavailableException"/> when the FIDO2 config is built.</para>
/// </summary>
public sealed class RpIdResolver(
    IHttpContextAccessor httpContextAccessor,
    IRealmProvisioningService realmSvc)
{
    /// <summary>
    /// The effective RP ID for <paramref name="clientId"/>: the client's admin-set
    /// per-client RP ID if set, else the realm's <c>PrimaryDomain</c>. Pass the
    /// caller's tenant-scoped <paramref name="session"/> (the OAuth client docs live
    /// in the per-realm DB).
    /// </summary>
    public async Task<string> ResolveAsync(IQuerySession session, string? clientId, CancellationToken ct = default)
    {
        var primaryDomain = await GetPrimaryDomainAsync(ct);
        if (string.IsNullOrWhiteSpace(clientId))
            return primaryDomain;

        var app = await session.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(a => a.ClientId == clientId && !a.IsDeleted, ct);
        if (app is not null
            && app.Settings.TryGetValue(OAuthApplicationSettingKeys.WebAuthnRpId, out var rpId)
            && !string.IsNullOrWhiteSpace(rpId))
            return rpId;

        return primaryDomain;
    }

    /// <summary>
    /// The current realm's <c>PrimaryDomain</c> — the legacy/realm-scoped RP ID and
    /// the fallback for clients without a per-client RP ID. Used as the verifier's
    /// fallback so a legacy <c>RpId == null</c> credential resolves to the realm RP ID.
    /// </summary>
    public async Task<string> GetPrimaryDomainAsync(CancellationToken ct = default)
    {
        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException(
                "RpIdResolver requires an active HttpContext — passkey ceremonies are request-bound.");

        var realm = await http.ResolveCurrentRealmAsync(realmSvc, ct)
            ?? throw new InvalidOperationException(
                "Could not resolve the current realm for the passkey ceremony — no relying party can be resolved.");

        return realm.PrimaryDomain ?? string.Empty;
    }
}
