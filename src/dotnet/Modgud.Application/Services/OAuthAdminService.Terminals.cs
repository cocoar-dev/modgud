using ErrorOr;
using Modgud.Application.Errors;
using Modgud.Domain.FunctionTerminals;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using static Modgud.Application.Services.OAuthAdminMapping;

namespace Modgud.Application.Services;

/// <summary>
/// MG-FT-03 — the terminal-managed client half of the OAuth admin service. All
/// methods STAGE into the injected session and never commit: a terminal slot
/// creates/mutates its enrollment stream and its client stream in one unit of
/// work owned by the endpoint (mirrors how the inline-SA create stages
/// principal + client + secret atomically).
/// </summary>
public partial class OAuthAdminService
{
    /// <summary>
    /// Stages the terminal-managed public client for one slot. The profile is
    /// FIXED (plan §6.4): public, secretless, DPoP-mandatory, reference tokens,
    /// per-client RP-ID, exactly the three terminal grants — validated against
    /// <see cref="OAuthAdminMapping.ValidateFunctionTerminalLinkInvariant"/> as
    /// defense in depth even though this method is the only producer.
    /// </summary>
    public Error? StageCreateTerminalClient(
        Guid applicationId,
        string clientId,
        string displayName,
        Guid functionPrincipalId,
        Guid terminalEnrollmentId,
        string webAuthnRpId)
    {
        var grants = TerminalGrantTypes.ToList();

        if (ValidateWebAuthnRpId(webAuthnRpId) is { } rpIdError)
            return rpIdError;

        if (ValidateFunctionTerminalLinkInvariant(
                grants, OAuthClientTypes.Public, requireClientSecret: false,
                AccessTokenType.Reference, requireDpop: true,
                linkedServiceAccountId: null, functionPrincipalId, terminalEnrollmentId,
                webAuthnRpId) is { } invariantError)
            return invariantError;

        var permissions = BuildClientPermissions(grants, scopes: [], OAuthClientTypes.Public);
        var (aggregate, createdEvent) = OAuthApplicationAggregate.Create(
            applicationId,
            clientId,
            displayName,
            OAuthClientTypes.Public,
            OAuthConsentTypes.Implicit,
            applicationType: null,
            redirectUris: [],
            postLogoutRedirectUris: [],
            permissions: permissions,
            requirements: []);
        _session.Events.StartStream<OAuthApplicationAggregate>(applicationId, createdEvent);

        _session.Events.Append(applicationId, aggregate.SetSettings(new Dictionary<string, string>
        {
            [OAuthApplicationSettingKeys.AccessTokenType] = AccessTokenType.Reference.ToString(),
            [OAuthApplicationSettingKeys.WebAuthnRpId] = webAuthnRpId.Trim().ToLowerInvariant(),
        }));

        _session.Events.Append(applicationId, aggregate.SetProperties(BuildClientProperties(
            enabled: true, allowBrowser: false, requireSecret: false, enableLocal: false,
            requireConsent: false, allowRemember: false, corsOrigins: [],
            alwaysSend: false, updateClaims: false, claims: [], roles: [],
            requireDpop: true, requireDpopNonce: false)));

        _session.Events.Append(applicationId,
            aggregate.SetFunctionTerminalLink(functionPrincipalId, terminalEnrollmentId));

        return null;
    }

    /// <summary>
    /// Stages the client's enabled flag (slot disable/reactivate). Bypasses the
    /// terminal-managed guard deliberately — this IS the function-terminal
    /// mutation path the guard points callers to.
    /// </summary>
    public async Task<Error?> StageSetTerminalClientEnabledAsync(
        Guid applicationId, bool enabled, CancellationToken ct)
    {
        var aggregate = await _session.Events.AggregateStreamAsync<OAuthApplicationAggregate>(applicationId, token: ct);
        if (aggregate is null || aggregate.IsDeleted)
            return OAuthErrors.ClientNotFound(applicationId.ToString());

        var properties = new Dictionary<string, object?>(aggregate.Properties)
        {
            [OAuthApplicationPropertyKeys.Enabled] = System.Text.Json.JsonSerializer.SerializeToElement(enabled),
        };
        _session.Events.Append(applicationId, aggregate.SetProperties(properties));
        return null;
    }

    /// <summary>Stages the client's soft delete (slot revoke — the client dies
    /// with its slot). Token revocation is the caller's follow-up.</summary>
    public async Task<Error?> StageDeleteTerminalClientAsync(Guid applicationId, CancellationToken ct)
    {
        var aggregate = await _session.Events.AggregateStreamAsync<OAuthApplicationAggregate>(applicationId, token: ct);
        if (aggregate is null || aggregate.IsDeleted)
            return null; // already gone — revoke is idempotent

        _session.Events.Append(applicationId, aggregate.Delete());
        return null;
    }
}
