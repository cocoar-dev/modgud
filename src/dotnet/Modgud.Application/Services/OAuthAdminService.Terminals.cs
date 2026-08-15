using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.Positions;
using Modgud.Application.Errors;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Domain.PositionTerminals;
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
    /// True when a create request means a terminal-managed client: it names the
    /// staffing grant or any of the position/terminal link fields. Mirrors the
    /// client_credentials ⇔ ServiceAccount coupling — a staffing client must be
    /// backed by a position, and position fields without the staffing grant are
    /// a contradiction the terminal path rejects loudly instead of dropping.
    /// </summary>
    public static bool HasTerminalClientIntent(CreateOAuthClientDto dto) =>
        dto.AllowedGrantTypes.Contains(PositionGrantTypes.StaffingSession, StringComparer.Ordinal)
        || !string.IsNullOrWhiteSpace(dto.LinkedPositionPrincipalId)
        || dto.NewPosition is not null
        || !string.IsNullOrWhiteSpace(dto.TerminalDisplayName)
        || !string.IsNullOrWhiteSpace(dto.TerminalLocation);

    /// <summary>
    /// Client-side terminal create ("wie in Service Accounts"): the generic
    /// admin create diverted here because the request carries terminal intent.
    /// Resolves or inline-creates the position, then delegates the client build
    /// to <see cref="StageCreateTerminalClient"/> — the single producer of the
    /// fixed terminal profile — and starts the enrollment stream, all committed
    /// in ONE SaveChanges: position, grants, slot, and client exist together or
    /// not at all.
    /// </summary>
    private async Task<ErrorOr<OAuthClientCreatedDto>> CreateTerminalClientAsync(
        CreateOAuthClientDto dto, bool isDcr, Guid? actorId, CancellationToken ct)
    {
        if (isDcr)
            return OAuthErrors.InvalidPositionTerminalClient(
                "a terminal client cannot be created via dynamic client registration.");

        if (actorId is null)
            return OAuthErrors.InvalidPositionTerminalClient(
                "creating a terminal client requires an authenticated admin actor.");

        var hasLinked = !string.IsNullOrWhiteSpace(dto.LinkedPositionPrincipalId);
        if (hasLinked && dto.NewPosition is not null)
            return OAuthErrors.PositionLinkModesAreMutuallyExclusive;

        if (!dto.AllowedGrantTypes.Contains(PositionGrantTypes.StaffingSession, StringComparer.Ordinal))
            return OAuthErrors.PositionLinkRequiresStaffingGrant;

        if (!hasLinked && dto.NewPosition is null)
            return OAuthErrors.StaffingGrantRequiresPositionLink;

        // The profile is fixed; requested grants may only be (a subset of) it.
        // Anything else — client_credentials, the web code flow — is a
        // different kind of client and gets rejected, not silently dropped.
        if (dto.AllowedGrantTypes.Any(g => !TerminalGrantTypes.Contains(g)))
            return OAuthErrors.InvalidPositionTerminalClient(
                "allowed grants are exactly device_code, refresh_token, and the position staffing grant.");

        if (!string.Equals(dto.ClientType, OAuthClientTypes.Public, StringComparison.Ordinal))
            return OAuthErrors.InvalidPositionTerminalClient("the client must be public.");

        var terminalDisplayName = (dto.TerminalDisplayName ?? string.Empty).Trim();
        if (terminalDisplayName.Length == 0)
            return OAuthErrors.TerminalDisplayNameRequired;

        // ── Resolve or inline-create the position ─────────────────────────
        PositionPrincipal position;
        PositionPrincipalDto? createdPosition = null;
        var stagedGrantUserIds = new List<Guid>();

        if (hasLinked)
        {
            if (!ShortGuid.TryParse(dto.LinkedPositionPrincipalId!, out Guid parsedPosition))
                return OAuthErrors.InvalidPositionId(dto.LinkedPositionPrincipalId!);
            var existing = await _session.LoadAsync<PositionPrincipal>(parsedPosition, ct);
            if (existing is null || existing.IsDeleted)
                return OAuthErrors.PositionNotFound(dto.LinkedPositionPrincipalId!);
            position = existing;
        }
        else
        {
            var newPosition = dto.NewPosition!;
            var accountName = (newPosition.AccountName ?? string.Empty).Trim().ToLowerInvariant();
            if (!ServiceAccountNamePattern.IsMatch(accountName))
                return OAuthErrors.InvalidNewPositionName;

            // Positions share the account-name space with persons AND service
            // accounts (mirrors the position endpoint's create).
            var personTaken = await _session.Query<Person>()
                .AnyAsync(p => !p.IsDeleted && p.AccountName == accountName, ct);
            var serviceAccountTaken = await _session.Query<ServiceAccount>()
                .AnyAsync(sa => !sa.IsDeleted && sa.AccountName == accountName, ct);
            var positionTaken = await _session.Query<PositionPrincipal>()
                .AnyAsync(f => !f.IsDeleted && f.AccountName == accountName, ct);
            if (personTaken || serviceAccountTaken || positionTaken)
                return OAuthErrors.PositionNameAlreadyExists(accountName);

            // This client IS the position's first slot — staged slots inside the
            // draft would race the same save with a second producer.
            if (newPosition.Terminals is { Count: > 0 })
                return OAuthErrors.InvalidPositionTerminalClient(
                    "NewPosition cannot stage terminal slots — this client is the slot; add further slots via the position modal.");

            var policy = PositionTerminalPolicy.Disabled;
            if (newPosition.TerminalPolicy is { } policyUpdate)
            {
                policy = policy with
                {
                    Enabled = policyUpdate.Enabled ?? policy.Enabled,
                    StaffingSessionLifetime = policyUpdate.StaffingSessionLifetimeMinutes is { } sessionMinutes
                        ? TimeSpan.FromMinutes(sessionMinutes)
                        : policy.StaffingSessionLifetime,
                    MaximumStaffingSessionLifetime = policyUpdate.MaximumStaffingSessionLifetimeMinutes is { } maximumMinutes
                        ? TimeSpan.FromMinutes(maximumMinutes)
                        : policy.MaximumStaffingSessionLifetime,
                };
                if (policy.StaffingSessionLifetime <= TimeSpan.Zero || policy.MaximumStaffingSessionLifetime <= TimeSpan.Zero)
                    return Error.Validation("Position.InvalidTerminalPolicy",
                        "Staffing session lifetimes must be positive.");
                if (policy.StaffingSessionLifetime > policy.MaximumStaffingSessionLifetime)
                    return Error.Validation("Position.InvalidTerminalPolicy",
                        "The staffing session lifetime must not exceed the absolute maximum lifetime.");
            }

            // Staged grants — resolve and validate EVERY user before creating
            // anything (mirrors the position endpoint's all-or-nothing rule).
            foreach (var rawUserId in newPosition.GrantUserIds?.Distinct() ?? [])
            {
                if (!ShortGuid.TryParse(rawUserId, out Guid grantUserId))
                    return Error.Validation("PositionGrant.InvalidUserId",
                        $"Grant user id '{rawUserId}' is invalid.");
                var person = await _session.LoadAsync<Person>(grantUserId, ct);
                if (person is null || person.IsDeleted)
                    return Error.Validation("PositionGrant.UserNotFound",
                        $"Grant user '{rawUserId}' does not exist.");
                if (!person.IsActive)
                    return Error.Validation("PositionGrant.UserInactive",
                        $"Grant user '{rawUserId}' is inactive.");
                stagedGrantUserIds.Add(grantUserId);
            }

            position = new PositionPrincipal
            {
                Id = Guid.NewGuid(),
                AccountName = accountName,
                Purpose = string.IsNullOrWhiteSpace(newPosition.Purpose) ? null : newPosition.Purpose.Trim(),
                IsActive = newPosition.IsActive,
                TerminalPolicy = policy,
            };
            _session.Events.StartStream<PositionPrincipal>(position.Id, new PositionPrincipalCreatedEvent(
                position.Id, position.AccountName, position.Purpose, position.IsActive, position.TerminalPolicy));

            createdPosition = new PositionPrincipalDto
            {
                Id = new ShortGuid(position.Id).ToString(),
                AccountName = position.AccountName,
                Purpose = position.Purpose,
                IsActive = position.IsActive,
                TerminalPolicy = new PositionTerminalPolicyDto
                {
                    Enabled = position.TerminalPolicy.Enabled,
                    StaffingSessionLifetimeMinutes = (int)position.TerminalPolicy.StaffingSessionLifetime.TotalMinutes,
                    MaximumStaffingSessionLifetimeMinutes = (int)position.TerminalPolicy.MaximumStaffingSessionLifetime.TotalMinutes,
                },
            };
        }

        if (!position.TerminalPolicy.Enabled)
            return OAuthErrors.PositionTerminalsDisabled(position.AccountName);

        // ── ClientId per convention (dto.ClientId is deliberately ignored —
        // the audit log reads the owning position off the generated id) ──────
        var clientId = string.Empty;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = $"{position.AccountName}.terminal.{new ShortGuid(Guid.NewGuid()).ToString()[..8]}";
            var clash = await _session.Query<OAuthApplicationState>()
                .AnyAsync(x => !x.IsDeleted && x.ClientId == candidate, ct);
            if (!clash) { clientId = candidate; break; }
        }
        if (clientId.Length == 0)
            return Error.Conflict("OAuth.ClientIdAutoGenerationFailed",
                "Could not generate a unique client_id for the terminal client after 8 attempts.");

        // ── Stage client + enrollment, commit once ─────────────────────────
        var enrollmentId = Guid.NewGuid();
        var applicationId = Guid.NewGuid();
        var clientError = StageCreateTerminalClient(
            applicationId, clientId, $"{position.DisplayName} — {terminalDisplayName}",
            position.Id, enrollmentId, dto.WebAuthnRpId ?? string.Empty);
        if (clientError is not null)
            return clientError.Value;

        var now = DateTimeOffset.UtcNow;
        foreach (var grantUserId in stagedGrantUserIds)
        {
            var grantId = Guid.NewGuid();
            _session.Events.StartStream<PositionGrant>(grantId, new PositionGrantIssued(
                grantId, position.Id, grantUserId, actorId.Value, now));
        }

        _session.Events.StartStream<TerminalEnrollment>(enrollmentId, new TerminalEnrollmentCreated(
            enrollmentId, position.Id, terminalDisplayName,
            string.IsNullOrWhiteSpace(dto.TerminalLocation) ? null : dto.TerminalLocation.Trim(),
            applicationId, clientId, dto.WebAuthnRpId!.Trim().ToLowerInvariant(),
            actorId.Value, now));

        await _session.SaveChangesAsync(ct);

        var state = await _session.LoadAsync<OAuthApplicationState>(applicationId, ct);
        return new OAuthClientCreatedDto
        {
            Client = MapClient(state!),
            ClientSecret = null,
            CreatedPosition = createdPosition,
            CreatedTerminalId = new ShortGuid(enrollmentId).ToString(),
        };
    }

    /// <summary>
    /// Stages the terminal-managed public client for one slot. The profile is
    /// FIXED (plan §6.4): public, secretless, DPoP-mandatory, reference tokens,
    /// per-client RP-ID, exactly the three terminal grants — validated against
    /// <see cref="OAuthAdminMapping.ValidatePositionTerminalLinkInvariant"/> as
    /// defense in depth even though this method is the only producer.
    /// </summary>
    public Error? StageCreateTerminalClient(
        Guid applicationId,
        string clientId,
        string displayName,
        Guid positionPrincipalId,
        Guid terminalEnrollmentId,
        string webAuthnRpId)
    {
        var grants = TerminalGrantTypes.ToList();

        if (ValidateWebAuthnRpId(webAuthnRpId) is { } rpIdError)
            return rpIdError;

        if (ValidatePositionTerminalLinkInvariant(
                grants, OAuthClientTypes.Public, requireClientSecret: false,
                AccessTokenType.Reference, requireDpop: true,
                linkedServiceAccountId: null, positionPrincipalId, terminalEnrollmentId,
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
            aggregate.SetPositionTerminalLink(positionPrincipalId, terminalEnrollmentId));

        return null;
    }

    /// <summary>
    /// Stages the client's enabled flag (slot disable/reactivate). Bypasses the
    /// terminal-managed guard deliberately — this IS the position-terminal
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
