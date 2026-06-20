using System.Collections.Immutable;
using System.Text.Json;
using BuildingBlocks.Helper;
using Modgud.Application.Dcr;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.ServiceAccount;
using Modgud.Application.Errors;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.OAuth.Scopes;
using ErrorOr;
using Marten;
using static Modgud.Application.Services.OAuthAdminMapping;

namespace Modgud.Application.Services;

/// <summary>
/// Admin orchestrator for OAuth clients, scopes, and APIs. Built on event-sourced
/// aggregates + inline state projections — no OpenIddict runtime dependency
/// (that lands in etappe 3b alongside the authorization/token endpoints).
///
/// <para>The injected <see cref="IDocumentSession"/> is tenant-scoped via
/// <c>TenantedSessionFactory</c>, so every CRUD call automatically targets the
/// active realm's database.</para>
/// </summary>
public class OAuthAdminService
{
    /// <summary>OpenIddict's well-known Settings key for per-application
    /// access-token lifetime. Mirrors
    /// <c>OpenIddictConstants.Settings.TokenLifetimes.AccessToken</c> —
    /// inlined here to avoid pulling OpenIddict.Abstractions into the
    /// Application layer. Drift against the OpenIddict value would
    /// silently disable the DCR per-realm lifetime override — pinned
    /// by a unit test that uses reflection to fetch the OpenIddict
    /// constant and asserts equality.</summary>
    internal const string OpenIddictAccessTokenLifetimeSettingKey = "tkn_lft:act";

    /// <summary>OpenIddict's well-known Settings key for per-application
    /// refresh-token lifetime. See
    /// <see cref="OpenIddictAccessTokenLifetimeSettingKey"/>.</summary>
    internal const string OpenIddictRefreshTokenLifetimeSettingKey = "tkn_lft:reft";

    private readonly IDocumentSession _session;

    public OAuthAdminService(IDocumentSession session)
    {
        _session = session;
    }


    // ───────────────────────────────────────────── Clients ─────────────────────

    public async Task<OAuthClientListDto> GetClientsAsync(
        PaginationRequest pagination, CancellationToken ct = default)
    {
        var query = _session.Query<OAuthApplicationState>().Where(x => !x.IsDeleted);
        var totalCount = await query.CountAsync(ct);

        var clients = await query
            .OrderBy(x => x.ClientId)
            .Skip((pagination.Page - 1) * pagination.PageSize)
            .Take(pagination.PageSize)
            .ToListAsync(ct);

        return new OAuthClientListDto
        {
            Items = clients.Select(MapClient).ToList(),
            TotalCount = totalCount,
        };
    }

    public async Task<OAuthClientDto?> GetClientByIdAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid)) return null;
        var state = await _session.LoadAsync<OAuthApplicationState>(guid, ct);
        if (state is null || state.IsDeleted) return null;
        return MapClient(state);
    }

    public Task<ErrorOr<OAuthClientCreatedDto>> CreateClientAsync(
        CreateOAuthClientDto dto, CancellationToken ct = default)
        => CreateClientAsync(dto, dcrMetadata: null, ct);

    /// <summary>
    /// Internal overload used by the DCR registration endpoint. When
    /// <paramref name="dcrMetadata"/> is non-null, the new client's
    /// Properties dict carries the DCR-marker keys
    /// (<c>cocoar:dcr:is_dynamically_registered</c> +
    /// timestamps + source IP) in the SAME transaction as the rest of
    /// the create flow — no second round-trip, no half-created
    /// "DCR client without DCR metadata" failure window.
    /// </summary>
    public async Task<ErrorOr<OAuthClientCreatedDto>> CreateClientAsync(
        CreateOAuthClientDto dto, DcrMetadataInput? dcrMetadata, CancellationToken ct = default)
    {
        if (dto.ClientType is not (OAuthClientTypes.Public or OAuthClientTypes.Confidential))
            return OAuthErrors.InvalidClientType(dto.ClientType);

        if (dto.ConsentType is not (OAuthConsentTypes.Explicit or OAuthConsentTypes.Implicit or OAuthConsentTypes.External))
            return OAuthErrors.InvalidConsentType(dto.ConsentType);

        if (ValidateWebAuthnRpId(dto.WebAuthnRpId) is { } createRpIdErr)
            return createRpIdErr;

        var existing = await _session.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(x => x.ClientId == dto.ClientId && !x.IsDeleted, ct);
        if (existing is not null)
            return OAuthErrors.ClientIdAlreadyExists(dto.ClientId);

        // Optional App-link validation (n:m). Each id must resolve to a
        // non-deleted App in the same realm. Duplicates are dropped.
        var appIds = new List<Guid>();
        if (dto.AppIds is { Count: > 0 })
        {
            var distinct = dto.AppIds.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).ToList();
            foreach (var raw in distinct)
            {
                if (!ShortGuid.TryParse(raw, out Guid parsed))
                    return Error.Validation("OAuthClient.InvalidAppId", $"AppId '{raw}' is not a valid Guid or ShortGuid.");
                var app = await _session.LoadAsync<App>(parsed, ct);
                if (app is null || app.IsDeleted)
                    return Error.Validation("OAuthClient.AppNotFound", $"App {raw} not found.");
                appIds.Add(parsed);
            }
        }

        // ServiceAccount-link validation. The endpoint accepts a raw
        // LinkedServiceAccountId on the create DTO so M2M setup is a single
        // round-trip; downstream mutations of the link (rotate, unlink, etc.)
        // go through the SA-scoped credentials endpoints instead. Parse, then
        // confirm the SA exists, then enforce the SA-link invariant
        // (R1/R2/R3) against the combination of grants + link the admin
        // submitted. DCR clients never come with a link — the DCR pipeline
        // doesn't surface it.
        Guid? linkedServiceAccountId = null;
        if (!string.IsNullOrWhiteSpace(dto.LinkedServiceAccountId))
        {
            if (!ShortGuid.TryParse(dto.LinkedServiceAccountId, out Guid parsedSa))
                return OAuthErrors.InvalidServiceAccountId(dto.LinkedServiceAccountId);
            var sa = await _session.LoadAsync<ServiceAccount>(parsedSa, ct);
            if (sa is null || sa.IsDeleted)
                return OAuthErrors.ServiceAccountNotFound(dto.LinkedServiceAccountId);
            linkedServiceAccountId = parsedSa;
        }
        if (ValidateServiceAccountLinkInvariant(dto.AllowedGrantTypes, linkedServiceAccountId) is { } createLinkErr)
            return createLinkErr;

        // Confidential clients must have a secret (generated if not supplied).
        string? clientSecret = null;
        if (dto.ClientType == OAuthClientTypes.Confidential)
        {
            clientSecret = dto.ClientSecret ?? GenerateSecret();
        }

        // Build permissions list (endpoints + grant types + scopes).
        var permissions = BuildClientPermissions(dto.AllowedGrantTypes, dto.Scopes, dto.ClientType);

        var id = Guid.NewGuid();
        var (aggregate, createdEvent) = OAuthApplicationAggregate.Create(
            id,
            dto.ClientId,
            dto.DisplayName,
            dto.ClientType,
            dto.ConsentType,
            applicationType: null,
            redirectUris: dto.RedirectUris,
            postLogoutRedirectUris: dto.PostLogoutRedirectUris,
            permissions: permissions,
            requirements: Array.Empty<string>());

        _session.Events.StartStream<OAuthApplicationAggregate>(id, createdEvent);

        // Settings (primitive lifetime + token-type values).
        var settings = BuildClientSettings(dto);
        if (dcrMetadata is not null)
        {
            // Per-realm DCR token-lifetime override. Written as OpenIddict's
            // own well-known settings keys (OpenIddictConstants.Settings.
            // TokenLifetimes.AccessToken / RefreshToken — string-literal
            // inlined to keep OpenIddict.Abstractions out of the Application
            // layer). Format matches what
            // OpenIddictApplicationDescriptor.SetAccessTokenLifetime
            // produces: TimeSpan.ToString("c") + invariant culture.
            // OpenIddict's EvaluateGeneratedTokens pipeline reads these
            // directly from the persisted Application.Settings — no
            // per-request handler gymnastics, no order-of-handlers race.
            // Pinned against drift by OpenIddictLifetimeSettingKeysTests
            // in the unit-test suite.
            settings[OpenIddictAccessTokenLifetimeSettingKey] =
                dcrMetadata.AccessTokenLifetime.ToString("c", System.Globalization.CultureInfo.InvariantCulture);
            settings[OpenIddictRefreshTokenLifetimeSettingKey] =
                dcrMetadata.RefreshTokenLifetime.ToString("c", System.Globalization.CultureInfo.InvariantCulture);
        }
        if (settings.Count > 0)
        {
            _session.Events.Append(id, aggregate.SetSettings(settings));
        }

        // Properties (booleans / lists / claims as JsonElement so the surface
        // matches what the OpenIddict-runtime in 3b will read back).
        var properties = BuildClientProperties(
            dto.Enabled, dto.AllowAccessTokensViaBrowser, dto.RequireClientSecret,
            dto.EnableLocalLogin, dto.RequireConsent, dto.AllowRememberConsent,
            dto.AllowedCorsOrigins, dto.AlwaysSendClientClaims,
            dto.UpdateAccessTokenClaimsOnRefresh, dto.Claims, dto.Roles);
        if (dcrMetadata is not null)
        {
            properties[OAuthApplicationPropertyKeys.DcrIsDynamicallyRegistered] = JsonSerializer.SerializeToElement(true);
            properties[OAuthApplicationPropertyKeys.DcrRegisteredAt] = JsonSerializer.SerializeToElement(
                dcrMetadata.RegisteredAt.ToString("O"));
            properties[OAuthApplicationPropertyKeys.DcrRegisteredFromIp] = JsonSerializer.SerializeToElement(
                dcrMetadata.SourceIp);
            properties[OAuthApplicationPropertyKeys.DcrLastUsedAt] = JsonSerializer.SerializeToElement(
                dcrMetadata.RegisteredAt.ToString("O"));
        }
        if (properties.Count > 0)
        {
            _session.Events.Append(id, aggregate.SetProperties(properties));
        }

        // App-link — only emit when at least one app was supplied so freshly
        // created realm-wide clients don't carry a redundant empty-list
        // event in their stream.
        if (appIds.Count > 0)
        {
            _session.Events.Append(id, aggregate.SetAppIds(appIds));
        }

        // ServiceAccount-link — same one-shot pattern as AppIds. Skipped when
        // the link is null so user-flow clients don't carry a redundant
        // unset-link event.
        if (linkedServiceAccountId.HasValue)
        {
            _session.Events.Append(id, aggregate.SetLinkedServiceAccountId(linkedServiceAccountId.Value));
        }

        // Persist the (hashed) secret separately from the event stream.
        if (clientSecret is not null)
        {
            var sec = OAuthApplicationSecurityData.Create(id);
            sec.ClientSecret = HashSecret(clientSecret);
            _session.Store(sec);
        }

        await _session.SaveChangesAsync(ct);

        // Reload projected state so the response reflects the persisted view.
        var state = await _session.LoadAsync<OAuthApplicationState>(id, ct);
        return new OAuthClientCreatedDto
        {
            Client = MapClient(state!),
            ClientSecret = clientSecret,
        };
    }

    public async Task<ErrorOr<OAuthClientDto>> UpdateClientAsync(
        string id, UpdateOAuthClientDto dto, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return OAuthErrors.ClientNotFound(id);

        var aggregate = await _session.Events.AggregateStreamAsync<OAuthApplicationAggregate>(guid, token: ct);
        if (aggregate is null || aggregate.IsDeleted)
            return OAuthErrors.ClientNotFound(id);

        // Phase 2C — SA-managed clients are read-only via the standard admin
        // PUT. The SA owns its credentials as child resources, so mutations
        // (rotate, scope edit, lifetime tweak, enable/disable) must go through
        // the SA-scoped endpoints — that path is the single place where the
        // SA-link invariant is preserved end-to-end. A standard PUT here
        // would silently drift the M2M client off its owning SA.
        if (aggregate.LinkedServiceAccountId.HasValue)
            return OAuthErrors.CannotMutateServiceAccountManagedClient(aggregate.ClientId);

        // Phase 2C — guard against the path "add client_credentials via PUT".
        // The UpdateDto can't carry a LinkedServiceAccountId (by design — the
        // SA-scoped endpoints own that mutation), so any attempt to add
        // client_credentials here would necessarily violate R1 (cc requires a
        // link). Fail fast with the same invariant error.
        if (dto.AllowedGrantTypes is not null &&
            ValidateServiceAccountLinkInvariant(dto.AllowedGrantTypes, linkedServiceAccountId: null) is { } updLinkErr)
            return updLinkErr;

        if (ValidateWebAuthnRpId(dto.WebAuthnRpId) is { } updRpIdErr)
            return updRpIdErr;

        if (dto.DisplayName is not null && dto.DisplayName != aggregate.DisplayName)
            _session.Events.Append(guid, aggregate.SetDisplayName(dto.DisplayName));

        if (dto.ConsentType is not null && dto.ConsentType != aggregate.ConsentType)
        {
            if (dto.ConsentType is not (OAuthConsentTypes.Explicit or OAuthConsentTypes.Implicit or OAuthConsentTypes.External))
                return OAuthErrors.InvalidConsentType(dto.ConsentType);
            _session.Events.Append(guid, aggregate.SetConsentType(dto.ConsentType));
        }

        if (dto.RedirectUris is not null && !dto.RedirectUris.SequenceEqual(aggregate.RedirectUris))
            _session.Events.Append(guid, aggregate.SetRedirectUris(dto.RedirectUris));

        if (dto.PostLogoutRedirectUris is not null && !dto.PostLogoutRedirectUris.SequenceEqual(aggregate.PostLogoutRedirectUris))
            _session.Events.Append(guid, aggregate.SetPostLogoutRedirectUris(dto.PostLogoutRedirectUris));

        // Recompute permissions if grants/scopes changed; preserves endpoint perms.
        if (dto.AllowedGrantTypes is not null || dto.Scopes is not null)
        {
            var grants = dto.AllowedGrantTypes ?? ExtractGrantTypes(aggregate.Permissions);
            var scopes = dto.Scopes ?? ExtractScopes(aggregate.Permissions);
            var permissions = BuildClientPermissions(grants, scopes, aggregate.ClientType ?? OAuthClientTypes.Public);
            _session.Events.Append(guid, aggregate.SetPermissions(permissions));
        }

        // Settings — partial-PATCH merge; only emit the event when the merge
        // actually produced a different dictionary.
        var newSettings = MergeClientSettings(aggregate.Settings, dto);
        if (!DictEquals(newSettings, aggregate.Settings))
            _session.Events.Append(guid, aggregate.SetSettings(newSettings));

        // Properties — partial-PATCH merge; always emit because every field is
        // re-serialised through BuildClientProperties (the `current` snapshot
        // may not match the canonical encoding of the same logical values).
        var newProps = MergeClientProperties(aggregate.Properties, dto);
        _session.Events.Append(guid, aggregate.SetProperties(newProps));

        // App-link patch. dto.AppIds == null → no change; an empty list →
        // explicit detach-all; non-empty list → replace (set semantics).
        if (dto.AppIds is not null)
        {
            var distinct = dto.AppIds.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).ToList();
            var parsed = new List<Guid>();
            foreach (var raw in distinct)
            {
                if (!ShortGuid.TryParse(raw, out Guid parsedId))
                    return Error.Validation("OAuthClient.InvalidAppId", $"AppId '{raw}' is not a valid Guid or ShortGuid.");
                var app = await _session.LoadAsync<App>(parsedId, ct);
                if (app is null || app.IsDeleted)
                    return Error.Validation("OAuthClient.AppNotFound", $"App {raw} not found.");
                parsed.Add(parsedId);
            }

            // Order-insensitive equality check — only emit when the set
            // actually changed, so an idempotent re-save doesn't pile up
            // events.
            var current = aggregate.AppIds.ToHashSet();
            var next = parsed.ToHashSet();
            if (!current.SetEquals(next))
            {
                _session.Events.Append(guid, aggregate.SetAppIds(parsed));
            }
        }

        await _session.SaveChangesAsync(ct);

        var state = await _session.LoadAsync<OAuthApplicationState>(guid, ct);
        return MapClient(state!);
    }

    public async Task<ErrorOr<bool>> DeleteClientAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return OAuthErrors.ClientNotFound(id);

        var aggregate = await _session.Events.AggregateStreamAsync<OAuthApplicationAggregate>(guid, token: ct);
        if (aggregate is null || aggregate.IsDeleted)
            return OAuthErrors.ClientNotFound(id);

        // Phase 2C — SA-managed clients can only be deleted via the SA-scoped
        // endpoints (or as part of cascade on SA delete). Symmetric with the
        // PUT guard in UpdateClientAsync.
        if (aggregate.LinkedServiceAccountId.HasValue)
            return OAuthErrors.CannotMutateServiceAccountManagedClient(aggregate.ClientId);

        return await DeleteClientInternalAsync(guid, aggregate, ct);
    }

    /// <summary>
    /// Guard-free deletion path. Used by the public API after the SA-link
    /// guard cleared and by the SA-scoped cascade on Service-Account delete.
    /// Caller must SaveChangesAsync.
    /// </summary>
    private async Task<ErrorOr<bool>> DeleteClientInternalAsync(
        Guid guid, OAuthApplicationAggregate aggregate, CancellationToken ct)
    {
        _session.Events.Append(guid, aggregate.Delete());
        // Hard-delete the secrets document — projection rebuild safety doesn't
        // apply to security data (we never replay secrets).
        _session.Delete<OAuthApplicationSecurityData>(guid);

        await _session.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ErrorOr<ClientSecretDto>> RegenerateClientSecretAsync(
        string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return OAuthErrors.ClientNotFound(id);

        var state = await _session.LoadAsync<OAuthApplicationState>(guid, ct);
        if (state is null || state.IsDeleted)
            return OAuthErrors.ClientNotFound(id);

        if (state.ClientType != OAuthClientTypes.Confidential)
            return OAuthErrors.CannotRegenerateSecretForPublicClient;

        // Phase 2C — SA-managed clients rotate via the SA-scoped /rotate
        // endpoint, not the generic admin path.
        if (state.LinkedServiceAccountId.HasValue)
            return OAuthErrors.CannotMutateServiceAccountManagedClient(state.ClientId);

        return await RegenerateClientSecretInternalAsync(guid, ct);
    }

    private async Task<ClientSecretDto> RegenerateClientSecretInternalAsync(
        Guid guid, CancellationToken ct)
    {
        var newSecret = GenerateSecret();
        var sec = await _session.LoadAsync<OAuthApplicationSecurityData>(guid, ct)
                  ?? OAuthApplicationSecurityData.Create(guid);
        sec.ClientSecret = HashSecret(newSecret);
        sec.UpdateConcurrencyToken();
        _session.Store(sec);

        await _session.SaveChangesAsync(ct);
        return new ClientSecretDto { ClientSecret = newSecret };
    }

    // ───────────────────────────────────────────── SA credentials ─────────────
    //
    // A "Service-Account credential" is a confidential OAuth client with
    // <see cref="OAuthApplicationState.LinkedServiceAccountId"/> set. The
    // SA-scoped endpoints below are the SINGLE source of mutations for these
    // clients — the standard /admin/oauth/clients endpoints reject them via
    // the SA-link guard. The DisplayName / Scopes / AppIds / lifetime /
    // enabled surface is intentionally narrower than the generic OAuth client
    // edit form: grant-types, client-type, secret-required, redirect URIs are
    // all system-pinned (client_credentials + confidential + always-secret +
    // no-redirect) so the SA admin can't accidentally produce a malformed
    // M2M client.

    public async Task<List<OAuthClientDto>> ListServiceAccountCredentialsAsync(
        Guid serviceAccountId, CancellationToken ct = default)
    {
        var rows = await _session.Query<OAuthApplicationState>()
            .Where(x => !x.IsDeleted && x.LinkedServiceAccountId == serviceAccountId)
            .OrderBy(x => x.ClientId)
            .ToListAsync(ct);
        return rows.Select(MapClient).ToList();
    }

    public async Task<ErrorOr<ServiceAccountCredentialIssuedDto>> IssueServiceAccountCredentialAsync(
        Guid serviceAccountId,
        IssueServiceAccountCredentialDto dto,
        CancellationToken ct = default)
    {
        var sa = await _session.LoadAsync<Modgud.Authorization.Principals.ServiceAccount>(serviceAccountId, ct);
        if (sa is null || sa.IsDeleted)
            return OAuthErrors.ServiceAccountNotFound(new ShortGuid(serviceAccountId).ToString());

        var clientId = (dto.ClientId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(clientId))
        {
            // {accountName}.{8-char suffix}. ShortGuid trims to 22 chars; we
            // slice 8 to keep the visible client_id short enough for log
            // grep, while still providing enough entropy to avoid collisions
            // between credentials of the same SA. Loops on collision (the
            // CreateClientAsync uniqueness check would reject otherwise).
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var suffix = new ShortGuid(Guid.NewGuid()).ToString()[..8];
                var candidate = $"{sa.AccountName}.{suffix}";
                var clash = await _session.Query<OAuthApplicationState>()
                    .AnyAsync(x => !x.IsDeleted && x.ClientId == candidate, ct);
                if (!clash) { clientId = candidate; break; }
            }
            if (string.IsNullOrEmpty(clientId))
                return Error.Conflict("OAuth.ClientIdAutoGenerationFailed",
                    "Could not generate a unique client_id for the new credential after 8 attempts.");
        }

        var createDto = new CreateOAuthClientDto
        {
            ClientId = clientId,
            DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? sa.AccountName : dto.DisplayName,
            ClientType = OAuthClientTypes.Confidential,
            ConsentType = OAuthConsentTypes.Implicit,
            AllowedGrantTypes = [OAuthAdminMapping.ClientCredentialsGrantType],
            Scopes = dto.Scopes,
            RequireClientSecret = true,
            RequireConsent = false,
            Enabled = true,
            // Audit #6/#7/#8 — default Reference (opaque + instantly revocable) so
            // SA deactivate/delete/rotate cuts off live M2M access immediately. JWT
            // is opt-in for resource servers that must self-validate (its already-
            // issued tokens then survive a revoke until expiry).
            AccessTokenType = dto.AccessTokenType,
            AccessTokenLifetime = dto.AccessTokenLifetime,
            AppIds = dto.AppIds,
            LinkedServiceAccountId = new ShortGuid(serviceAccountId).ToString(),
        };

        var result = await CreateClientAsync(createDto, ct);
        if (result.IsError) return result.Errors;
        return new ServiceAccountCredentialIssuedDto
        {
            Credential = result.Value.Client,
            ClientSecret = result.Value.ClientSecret!,
        };
    }

    public async Task<ErrorOr<OAuthClientDto>> UpdateServiceAccountCredentialAsync(
        Guid serviceAccountId,
        string credentialId,
        UpdateServiceAccountCredentialDto dto,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(credentialId, out var guid))
            return OAuthErrors.ClientNotFound(credentialId);

        var aggregate = await _session.Events.AggregateStreamAsync<OAuthApplicationAggregate>(guid, token: ct);
        if (aggregate is null || aggregate.IsDeleted)
            return OAuthErrors.ClientNotFound(credentialId);

        // Tenant-style ownership check: the credential MUST belong to the SA
        // named in the route, not just any SA. Prevents a request that
        // squeezes through /api/service-account/{otherSaId}/credentials/{credId}
        // from editing credentials it shouldn't see.
        if (aggregate.LinkedServiceAccountId != serviceAccountId)
            return OAuthErrors.ClientNotFound(credentialId);

        if (dto.DisplayName is not null && dto.DisplayName != aggregate.DisplayName)
            _session.Events.Append(guid, aggregate.SetDisplayName(dto.DisplayName));

        if (dto.Scopes is not null)
        {
            var grants = ExtractGrantTypes(aggregate.Permissions);
            var newPermissions = BuildClientPermissions(grants, dto.Scopes, aggregate.ClientType ?? OAuthClientTypes.Confidential);
            if (!newPermissions.SequenceEqual(aggregate.Permissions))
                _session.Events.Append(guid, aggregate.SetPermissions(newPermissions));
        }

        if (dto.AppIds is not null)
        {
            var distinct = dto.AppIds.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).ToList();
            var parsed = new List<Guid>();
            foreach (var raw in distinct)
            {
                if (!ShortGuid.TryParse(raw, out Guid parsedId))
                    return Error.Validation("OAuthClient.InvalidAppId", $"AppId '{raw}' is not a valid Guid or ShortGuid.");
                var app = await _session.LoadAsync<App>(parsedId, ct);
                if (app is null || app.IsDeleted)
                    return Error.Validation("OAuthClient.AppNotFound", $"App {raw} not found.");
                parsed.Add(parsedId);
            }
            var current = aggregate.AppIds.ToHashSet();
            var next = parsed.ToHashSet();
            if (!current.SetEquals(next))
                _session.Events.Append(guid, aggregate.SetAppIds(parsed));
        }

        if (dto.AccessTokenLifetime.HasValue || dto.AccessTokenType.HasValue)
        {
            // Merge both into ONE settings revision — two separate SetSettings events
            // built off the same base would clobber each other.
            var settings = new Dictionary<string, string>(aggregate.Settings);
            if (dto.AccessTokenLifetime.HasValue)
                settings[OAuthApplicationSettingKeys.AccessTokenLifetime] = dto.AccessTokenLifetime.Value.ToString();
            if (dto.AccessTokenType.HasValue)
                settings[OAuthApplicationSettingKeys.AccessTokenType] = dto.AccessTokenType.Value.ToString();
            if (!DictEquals(settings, aggregate.Settings))
                _session.Events.Append(guid, aggregate.SetSettings(settings));
        }

        if (dto.Enabled.HasValue)
        {
            var current = GetBoolProp(aggregate.Properties, OAuthApplicationPropertyKeys.Enabled, true);
            if (current != dto.Enabled.Value)
            {
                var newProps = new Dictionary<string, object?>(aggregate.Properties)
                {
                    [OAuthApplicationPropertyKeys.Enabled] = JsonSerializer.SerializeToElement(dto.Enabled.Value),
                };
                _session.Events.Append(guid, aggregate.SetProperties(newProps));
            }
        }

        await _session.SaveChangesAsync(ct);
        var state = await _session.LoadAsync<OAuthApplicationState>(guid, ct);
        return MapClient(state!);
    }

    public async Task<ErrorOr<ClientSecretDto>> RotateServiceAccountCredentialAsync(
        Guid serviceAccountId, string credentialId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(credentialId, out var guid))
            return OAuthErrors.ClientNotFound(credentialId);

        var state = await _session.LoadAsync<OAuthApplicationState>(guid, ct);
        if (state is null || state.IsDeleted)
            return OAuthErrors.ClientNotFound(credentialId);
        if (state.LinkedServiceAccountId != serviceAccountId)
            return OAuthErrors.ClientNotFound(credentialId);

        return await RegenerateClientSecretInternalAsync(guid, ct);
    }

    public async Task<ErrorOr<bool>> DeleteServiceAccountCredentialAsync(
        Guid serviceAccountId, string credentialId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(credentialId, out var guid))
            return OAuthErrors.ClientNotFound(credentialId);

        var aggregate = await _session.Events.AggregateStreamAsync<OAuthApplicationAggregate>(guid, token: ct);
        if (aggregate is null || aggregate.IsDeleted)
            return OAuthErrors.ClientNotFound(credentialId);
        if (aggregate.LinkedServiceAccountId != serviceAccountId)
            return OAuthErrors.ClientNotFound(credentialId);

        return await DeleteClientInternalAsync(guid, aggregate, ct);
    }

    /// <summary>
    /// Stage cascade-deletion of every credential owned by a Service Account.
    /// Queues delete events + secret-doc removals on the active session but
    /// does NOT call SaveChangesAsync — the caller commits the whole unit of
    /// work (cascade + SA soft-delete) together so Marten's optimistic
    /// concurrency check doesn't reject the second mutation against a
    /// version it already advanced internally.
    /// <para>
    /// Returns the credential count for the response toast.
    /// </para>
    /// </summary>
    public async Task<int> StageDeleteAllServiceAccountCredentialsAsync(
        Guid serviceAccountId, CancellationToken ct = default)
    {
        var states = await _session.Query<OAuthApplicationState>()
            .Where(x => !x.IsDeleted && x.LinkedServiceAccountId == serviceAccountId)
            .ToListAsync(ct);
        if (states.Count == 0) return 0;

        foreach (var state in states)
        {
            var aggregate = await _session.Events
                .AggregateStreamAsync<OAuthApplicationAggregate>(state.Id, token: ct);
            if (aggregate is null || aggregate.IsDeleted) continue;
            _session.Events.Append(state.Id, aggregate.Delete());
            _session.Delete<OAuthApplicationSecurityData>(state.Id);
        }
        return states.Count;
    }

    // ───────────────────────────────────────────── Scopes ──────────────────────

    public async Task<OAuthScopeListDto> GetScopesAsync(CancellationToken ct = default)
    {
        var scopes = await _session.Query<OAuthScopeState>()
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        return new OAuthScopeListDto
        {
            Items = scopes.Select(MapScope).ToList(),
            TotalCount = scopes.Count,
        };
    }

    public async Task<OAuthScopeDto?> GetScopeByIdAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid)) return null;
        var state = await _session.LoadAsync<OAuthScopeState>(guid, ct);
        if (state is null || state.IsDeleted) return null;
        return MapScope(state);
    }

    public async Task<ErrorOr<OAuthScopeDto>> CreateScopeAsync(
        CreateOAuthScopeDto dto, CancellationToken ct = default)
    {
        // RFC 8707 §2: each entry in Resources lands as `aud` once a
        // client requests this scope, so each must be an absolute URI.
        foreach (var resource in dto.Resources)
        {
            if (!AudienceUri.TryValidate(resource, out var resourceError))
                return Error.Validation("OAuthScope.InvalidResource", resourceError!);
        }

        var existing = await _session.Query<OAuthScopeState>()
            .FirstOrDefaultAsync(x => x.Name == dto.Name && !x.IsDeleted, ct);
        if (existing is not null)
            return OAuthErrors.ScopeNameAlreadyExists(dto.Name);

        // Optional App-link validation. Standard OIDC scopes (openid, email,
        // profile, …) stay global so existing /connect/authorize requests
        // for them keep working regardless of the client's app.
        Guid? appId = null;
        if (!string.IsNullOrEmpty(dto.AppId))
        {
            if (!ShortGuid.TryParse(dto.AppId, out Guid parsedAppId))
                return Error.Validation("OAuthScope.InvalidAppId", $"AppId '{dto.AppId}' is not a valid Guid or ShortGuid.");
            var app = await _session.LoadAsync<App>(parsedAppId, ct);
            if (app is null || app.IsDeleted)
                return Error.Validation("OAuthScope.AppNotFound", $"App {dto.AppId} not found.");
            appId = parsedAppId;
        }

        var id = Guid.NewGuid();
        var (aggregate, createdEvent) = OAuthScopeAggregate.Create(id, dto.Name, dto.DisplayName, dto.Description, dto.Resources);
        _session.Events.StartStream<OAuthScopeAggregate>(id, createdEvent);

        // Apply non-default flags as separate events (matching legacy behaviour).
        if (!dto.Enabled) _session.Events.Append(id, aggregate.SetEnabled(false));
        if (dto.Required) _session.Events.Append(id, aggregate.SetRequired(true));
        if (dto.Emphasize) _session.Events.Append(id, aggregate.SetEmphasize(true));
        if (!dto.ShowInDiscoveryDocument) _session.Events.Append(id, aggregate.SetShowInDiscoveryDocument(false));
        if (dto.UserClaims.Count > 0) _session.Events.Append(id, aggregate.SetUserClaims(dto.UserClaims));

        // Mirror identity-resource flags onto Properties — the runtime will read them from there.
        _session.Events.Append(id, aggregate.SetProperties(BuildScopeProperties(
            dto.Enabled, dto.Required, dto.Emphasize, dto.ShowInDiscoveryDocument, dto.UserClaims,
            dto.AllowDynamicRegistrationClients)));

        if (appId.HasValue)
            _session.Events.Append(id, aggregate.SetAppId(appId));

        await _session.SaveChangesAsync(ct);

        var state = await _session.LoadAsync<OAuthScopeState>(id, ct);
        return MapScope(state!);
    }

    public async Task<ErrorOr<OAuthScopeDto>> UpdateScopeAsync(
        string id, UpdateOAuthScopeDto dto, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return OAuthErrors.ScopeNotFound(id);

        var aggregate = await _session.Events.AggregateStreamAsync<OAuthScopeAggregate>(guid, token: ct);
        if (aggregate is null || aggregate.IsDeleted)
            return OAuthErrors.ScopeNotFound(id);

        if (StandardScopes.IsStandard(aggregate.Name))
            return OAuthErrors.CannotModifyStandardScope(aggregate.Name);

        if (dto.DisplayName is not null && dto.DisplayName != aggregate.DisplayName)
            _session.Events.Append(guid, aggregate.SetDisplayName(dto.DisplayName));

        if (dto.Description is not null && dto.Description != aggregate.Description)
            _session.Events.Append(guid, aggregate.SetDescription(dto.Description));

        if (dto.Resources is not null && !dto.Resources.SequenceEqual(aggregate.Resources))
        {
            foreach (var resource in dto.Resources)
            {
                if (!AudienceUri.TryValidate(resource, out var resourceError))
                    return Error.Validation("OAuthScope.InvalidResource", resourceError!);
            }
            _session.Events.Append(guid, aggregate.SetResources(dto.Resources));
        }

        if (dto.Enabled.HasValue && dto.Enabled.Value != aggregate.Enabled)
            _session.Events.Append(guid, aggregate.SetEnabled(dto.Enabled.Value));

        if (dto.Required.HasValue && dto.Required.Value != aggregate.Required)
            _session.Events.Append(guid, aggregate.SetRequired(dto.Required.Value));

        if (dto.Emphasize.HasValue && dto.Emphasize.Value != aggregate.Emphasize)
            _session.Events.Append(guid, aggregate.SetEmphasize(dto.Emphasize.Value));

        if (dto.ShowInDiscoveryDocument.HasValue && dto.ShowInDiscoveryDocument.Value != aggregate.ShowInDiscoveryDocument)
            _session.Events.Append(guid, aggregate.SetShowInDiscoveryDocument(dto.ShowInDiscoveryDocument.Value));

        if (dto.UserClaims is not null && !dto.UserClaims.SequenceEqual(aggregate.UserClaims))
            _session.Events.Append(guid, aggregate.SetUserClaims(dto.UserClaims));

        // Always write a fresh Properties snapshot reflecting the merged values.
        var currentAllowDcr = GetBoolProp(aggregate.Properties, ScopePropertyKeys.AllowDynamicRegistrationClients, false);
        _session.Events.Append(guid, aggregate.SetProperties(BuildScopeProperties(
            dto.Enabled ?? aggregate.Enabled,
            dto.Required ?? aggregate.Required,
            dto.Emphasize ?? aggregate.Emphasize,
            dto.ShowInDiscoveryDocument ?? aggregate.ShowInDiscoveryDocument,
            dto.UserClaims ?? aggregate.UserClaims,
            dto.AllowDynamicRegistrationClients ?? currentAllowDcr)));

        // App-link patch — null=no change, ""=make global, "guid"=assign.
        if (dto.AppId is not null)
        {
            Guid? newAppId = null;
            if (dto.AppId.Length > 0)
            {
                if (!ShortGuid.TryParse(dto.AppId, out Guid parsedAppId))
                    return Error.Validation("OAuthScope.InvalidAppId", $"AppId '{dto.AppId}' is not a valid Guid or ShortGuid.");
                var app = await _session.LoadAsync<App>(parsedAppId, ct);
                if (app is null || app.IsDeleted)
                    return Error.Validation("OAuthScope.AppNotFound", $"App {dto.AppId} not found.");
                newAppId = parsedAppId;
            }
            if (newAppId != aggregate.AppId)
                _session.Events.Append(guid, aggregate.SetAppId(newAppId));
        }

        await _session.SaveChangesAsync(ct);

        var state = await _session.LoadAsync<OAuthScopeState>(guid, ct);
        return MapScope(state!);
    }

    public async Task<ErrorOr<bool>> DeleteScopeAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return OAuthErrors.ScopeNotFound(id);

        var aggregate = await _session.Events.AggregateStreamAsync<OAuthScopeAggregate>(guid, token: ct);
        if (aggregate is null || aggregate.IsDeleted)
            return OAuthErrors.ScopeNotFound(id);

        if (StandardScopes.IsStandard(aggregate.Name))
            return OAuthErrors.CannotDeleteStandardScope(aggregate.Name);

        _session.Events.Append(guid, aggregate.Delete());
        await _session.SaveChangesAsync(ct);
        return true;
    }

    // ───────────────────────────────────────────── APIs ────────────────────────

    public async Task<OAuthApiListDto> GetApisAsync(
        PaginationRequest pagination, CancellationToken ct = default)
    {
        var query = _session.Query<OAuthApiState>().Where(x => !x.IsDeleted);
        var totalCount = await query.CountAsync(ct);

        var apis = await query
            .OrderBy(x => x.Name)
            .Skip((pagination.Page - 1) * pagination.PageSize)
            .Take(pagination.PageSize)
            .ToListAsync(ct);

        var items = new List<OAuthApiDto>();
        foreach (var api in apis)
        {
            items.Add(await MapApiAsync(api, ct));
        }
        return new OAuthApiListDto { Items = items, TotalCount = totalCount };
    }

    public async Task<OAuthApiDto?> GetApiByIdAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid)) return null;
        var state = await _session.LoadAsync<OAuthApiState>(guid, ct);
        if (state is null || state.IsDeleted) return null;
        return await MapApiAsync(state, ct);
    }

    public async Task<ErrorOr<OAuthApiCreatedDto>> CreateApiAsync(
        CreateOAuthApiDto dto, CancellationToken ct = default)
    {
        // RFC 8707 §2: an OAuthApi.Name doubles as the JWT `aud` claim
        // and as the value clients pass in `resource=`. Both MUST be
        // absolute URIs without fragment.
        if (!AudienceUri.TryValidate(dto.Name, out var audienceError))
            return Error.Validation("OAuthApi.InvalidName", audienceError!);

        var existing = await _session.Query<OAuthApiState>()
            .FirstOrDefaultAsync(x => x.Name == dto.Name && !x.IsDeleted, ct);
        if (existing is not null)
            return OAuthErrors.ApiNameAlreadyExists(dto.Name);

        // Optional App-link validation. dto.AppId is the App this RS will
        // belong to; without it the RS cannot authenticate against the
        // distribution API (no app context to derive).
        Guid? appId = null;
        App? linkedApp = null;
        if (!string.IsNullOrEmpty(dto.AppId))
        {
            if (!ShortGuid.TryParse(dto.AppId, out Guid parsed))
                return Error.Validation("OAuthApi.InvalidAppId", $"AppId '{dto.AppId}' is not a valid Guid or ShortGuid.");
            var app = await _session.LoadAsync<App>(parsed, ct);
            if (app is null || app.IsDeleted)
                return Error.Validation("OAuthApi.AppNotFound", $"App {dto.AppId} not found.");
            appId = parsed;
            linkedApp = app;
        }

        // PermissionIds must be a subset of the linked App's catalog.
        // Without an AppId there's nothing to be a subset of, so a non-
        // empty list is rejected.
        var permissionIdsResult = ValidatePermissionIds(dto.PermissionIds, linkedApp);
        if (permissionIdsResult.IsError) return permissionIdsResult.FirstError;

        var id = Guid.NewGuid();
        var (aggregate, createdEvent) = OAuthApiAggregate.Create(id, dto.Name, dto.DisplayName, dto.Description, dto.Enabled, dto.Scopes);
        _session.Events.StartStream<OAuthApiAggregate>(id, createdEvent);

        if (dto.UserClaims.Count > 0)
            _session.Events.Append(id, aggregate.SetUserClaims(dto.UserClaims));

        if (appId.HasValue)
            _session.Events.Append(id, aggregate.SetAppId(appId));

        if (permissionIdsResult.Value.Count > 0)
            _session.Events.Append(id, aggregate.SetPermissionIds(permissionIdsResult.Value));

        // Write the canonical Properties snapshot so the DCR opt-in flag is
        // queryable consistently regardless of whether it was set at create
        // time or via a later patch. Always emit — the runtime reads
        // AllowDynamicRegistration from Properties, and an absent key is a
        // no-DCR default we want to be explicit about.
        _session.Events.Append(id, aggregate.SetProperties(BuildApiProperties(dto.AllowDynamicRegistration)));

        await _session.SaveChangesAsync(ct);

        return new OAuthApiCreatedDto
        {
            Id = id.ToString(),
            Name = dto.Name,
            DisplayName = dto.DisplayName,
            Description = dto.Description,
            Enabled = dto.Enabled,
            Scopes = dto.Scopes,
            UserClaims = dto.UserClaims,
        };
    }

    public async Task<ErrorOr<OAuthApiDto>> UpdateApiAsync(
        string id, UpdateOAuthApiDto dto, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return OAuthErrors.ApiNotFound(id);

        var aggregate = await _session.Events.AggregateStreamAsync<OAuthApiAggregate>(guid, token: ct);
        if (aggregate is null || aggregate.IsDeleted)
            return OAuthErrors.ApiNotFound(id);

        if (dto.DisplayName is not null && dto.DisplayName != aggregate.DisplayName)
            _session.Events.Append(guid, aggregate.SetDisplayName(dto.DisplayName));
        if (dto.Description is not null && dto.Description != aggregate.Description)
            _session.Events.Append(guid, aggregate.SetDescription(dto.Description));
        if (dto.Enabled.HasValue && dto.Enabled.Value != aggregate.Enabled)
            _session.Events.Append(guid, dto.Enabled.Value ? aggregate.Enable() : aggregate.Disable());
        if (dto.Scopes is not null && !dto.Scopes.SequenceEqual(aggregate.Scopes))
            _session.Events.Append(guid, aggregate.SetScopes(dto.Scopes));
        if (dto.UserClaims is not null && !dto.UserClaims.SequenceEqual(aggregate.UserClaims))
            _session.Events.Append(guid, aggregate.SetUserClaims(dto.UserClaims));

        // App-link patch — null=no change, ""=detach, "guid"=assign.
        // Track the App we ended up linked to (post-patch) so PermissionIds
        // validation in this same call sees the new context — otherwise
        // detaching + setting a new subset in one round-trip would
        // contradict each other.
        var resolvedAppId = aggregate.AppId;
        App? resolvedApp = resolvedAppId.HasValue
            ? await _session.LoadAsync<App>(resolvedAppId.Value, ct)
            : null;

        if (dto.AppId is not null)
        {
            Guid? newAppId = null;
            App? newApp = null;
            if (dto.AppId.Length > 0)
            {
                if (!ShortGuid.TryParse(dto.AppId, out Guid parsed))
                    return Error.Validation("OAuthApi.InvalidAppId", $"AppId '{dto.AppId}' is not a valid Guid or ShortGuid.");
                newApp = await _session.LoadAsync<App>(parsed, ct);
                if (newApp is null || newApp.IsDeleted)
                    return Error.Validation("OAuthApi.AppNotFound", $"App {dto.AppId} not found.");
                newAppId = parsed;
            }
            if (newAppId != aggregate.AppId)
                _session.Events.Append(guid, aggregate.SetAppId(newAppId));

            resolvedAppId = newAppId;
            resolvedApp = newApp;
        }

        // PermissionIds patch — null=no change, [] = clear, [...]=replace.
        // Validated against resolvedApp so a payload that detaches the App
        // AND sets PermissionIds in one go is rejected unless the new list
        // is empty.
        if (dto.PermissionIds is not null)
        {
            // Detaching to no app while keeping non-empty subset is invalid
            // — the subset would point at a catalog that's no longer
            // referenced. Forbid it explicitly with a clear message.
            if (resolvedAppId is null && dto.PermissionIds.Count > 0)
            {
                return Error.Validation(
                    "OAuthApi.PermissionIdsRequireAppLink",
                    "PermissionIds cannot be set on an RS without an AppId — link the RS to an App first or send an empty list.");
            }

            var permissionIdsResult = ValidatePermissionIds(dto.PermissionIds, resolvedApp);
            if (permissionIdsResult.IsError) return permissionIdsResult.FirstError;

            if (!permissionIdsResult.Value.SequenceEqual(aggregate.PermissionIds))
                _session.Events.Append(guid, aggregate.SetPermissionIds(permissionIdsResult.Value));
        }

        // AllowDynamicRegistration patch — only emit a Properties event if
        // the value actually changes. Reading the current value from the
        // aggregate's Properties dict keeps the comparison honest if the
        // flag was set by a parallel admin path.
        if (dto.AllowDynamicRegistration.HasValue)
        {
            var currentAllowDcr = GetBoolProp(aggregate.Properties, OAuthApiPropertyKeys.AllowDynamicRegistration, false);
            if (dto.AllowDynamicRegistration.Value != currentAllowDcr)
                _session.Events.Append(guid, aggregate.SetProperties(BuildApiProperties(dto.AllowDynamicRegistration.Value)));
        }

        await _session.SaveChangesAsync(ct);

        var state = await _session.LoadAsync<OAuthApiState>(guid, ct);
        return await MapApiAsync(state!, ct);
    }

    public async Task<ErrorOr<bool>> DeleteApiAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return OAuthErrors.ApiNotFound(id);

        var aggregate = await _session.Events.AggregateStreamAsync<OAuthApiAggregate>(guid, token: ct);
        if (aggregate is null || aggregate.IsDeleted)
            return OAuthErrors.ApiNotFound(id);

        _session.Events.Append(guid, aggregate.Delete());

        await _session.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Creates the 1:1 companion <c>OAuthScope</c> for an existing
    /// <c>OAuthApi</c> — eliminating the manual two-step "create API +
    /// create matching scope" flow that 100% of single-RS integrations end
    /// up doing.
    ///
    /// <para>The created scope is shaped to mirror the API:</para>
    /// <list type="bullet">
    ///   <item><c>Name</c> = <c>api.Name</c> (so <c>scope=<api></c> in a
    ///   client request maps 1:1 to this RS).</item>
    ///   <item><c>Resources</c> = <c>[api.Name]</c> (the token request
    ///   adds the API's name as <c>aud</c>).</item>
    ///   <item><c>ShowInDiscoveryDocument</c> = <c>false</c> (privacy by
    ///   default — the RS shouldn't be enumerable via
    ///   <c>.well-known/openid-configuration</c>).</item>
    ///   <item><c>AppId</c> inherited from the API (or null if
    ///   unassigned).</item>
    /// </list>
    ///
    /// <para>The API's <c>Scopes</c> metadata gets the new name appended
    /// so the reverse relation stays consistent.</para>
    ///
    /// <para>Rejects if a scope (live or anything else) with the API's
    /// name already exists — the admin should manage that scope directly
    /// rather than going through this convenience endpoint.</para>
    /// </summary>
    public async Task<ErrorOr<OAuthScopeDto>> CreateImplicitScopeForApiAsync(
        string apiId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(apiId, out var guid))
            return OAuthErrors.ApiNotFound(apiId);

        var apiAggregate = await _session.Events.AggregateStreamAsync<OAuthApiAggregate>(guid, token: ct);
        if (apiAggregate is null || apiAggregate.IsDeleted)
            return OAuthErrors.ApiNotFound(apiId);

        var existing = await _session.Query<OAuthScopeState>()
            .FirstOrDefaultAsync(x => x.Name == apiAggregate.Name && !x.IsDeleted, ct);
        if (existing is not null)
            return OAuthErrors.ScopeNameAlreadyExists(apiAggregate.Name);

        var scopeId = Guid.NewGuid();
        var scopeName = apiAggregate.Name;
        var displayName = string.IsNullOrWhiteSpace(apiAggregate.DisplayName)
            ? apiAggregate.Name
            : apiAggregate.DisplayName;
        var description = $"Implicit scope granting access to the {apiAggregate.Name} resource server.";

        var (scopeAggregate, createdEvent) = OAuthScopeAggregate.Create(
            scopeId, scopeName, displayName, description, new[] { apiAggregate.Name });
        _session.Events.StartStream<OAuthScopeAggregate>(scopeId, createdEvent);

        // Hidden from `.well-known/openid-configuration` by default — the
        // implicit-scope convention is "1 RS = 1 scope", and exposing every
        // RS name in Discovery is a Multi-Tenant info-disclosure with no
        // upside (clients learn their scopes from RS docs anyway). Admins
        // can flip ShowInDiscoveryDocument later if they explicitly want it.
        _session.Events.Append(scopeId, scopeAggregate.SetShowInDiscoveryDocument(false));

        // Mirror identity-resource flags onto Properties — matches the path
        // taken by manual scope-create so the runtime reads consistent
        // metadata regardless of how the scope was minted.
        _session.Events.Append(scopeId, scopeAggregate.SetProperties(BuildScopeProperties(
            enabled: true, required: false, emphasize: false,
            showInDiscovery: false, userClaims: Array.Empty<string>())));

        if (apiAggregate.AppId.HasValue)
            _session.Events.Append(scopeId, scopeAggregate.SetAppId(apiAggregate.AppId));

        // Reverse relation: surface the new scope name on the API so the
        // admin grid + DTO show that the link is in place even if the UI
        // re-checks via HasImplicitScope.
        if (!apiAggregate.Scopes.Contains(scopeName))
        {
            var nextScopes = apiAggregate.Scopes.Append(scopeName).ToList();
            _session.Events.Append(guid, apiAggregate.SetScopes(nextScopes));
        }

        await _session.SaveChangesAsync(ct);

        var state = await _session.LoadAsync<OAuthScopeState>(scopeId, ct);
        return MapScope(state!);
    }

    // ───────────────────────────────────────────── Helpers ─────────────────────
    // Pure helpers (BuildClientPermissions, MapClient, etc.) live in
    // OAuthAdminMapping and are imported via `using static` above. The only
    // remaining instance helper is MapApiAsync, which probes for the
    // implicit-scope companion alongside the projection state.

    private async Task<OAuthApiDto> MapApiAsync(OAuthApiState s, CancellationToken ct)
    {
        // The implicit-scope-per-API convention pairs `scope.Name == api.Name`.
        // Probe for a live scope row with that name so the UI knows whether to
        // surface the "Create implicit scope" affordance.
        var hasImplicit = await _session.Query<OAuthScopeState>()
            .AnyAsync(x => x.Name == s.Name && !x.IsDeleted, ct);
        return MapApiState(s, hasImplicit);
    }

    /// <summary>
    /// Validates that every supplied PermissionId parses and resolves to an
    /// entry in the linked App's catalog. A null/empty input list is
    /// always valid. Returns the parsed Guid list on success.
    ///
    /// <para>When <paramref name="linkedApp"/> is null and the input list
    /// is non-empty, the result is an error — there's no catalog to be a
    /// subset of.</para>
    /// </summary>
    private static ErrorOr<List<Guid>> ValidatePermissionIds(
        IReadOnlyList<string>? raw, App? linkedApp)
    {
        if (raw is null || raw.Count == 0) return new List<Guid>();

        if (linkedApp is null)
        {
            return Error.Validation(
                "OAuthApi.PermissionIdsRequireAppLink",
                "PermissionIds cannot be set on an RS without an AppId.");
        }

        var catalogIds = linkedApp.Permissions.Select(p => p.Id).ToHashSet();
        var parsed = new List<Guid>(raw.Count);
        var seen = new HashSet<Guid>();

        foreach (var entry in raw)
        {
            if (!ShortGuid.TryParse(entry, out Guid id))
            {
                return Error.Validation(
                    "OAuthApi.InvalidPermissionId",
                    $"PermissionId '{entry}' is not a valid Guid or ShortGuid.");
            }

            if (!catalogIds.Contains(id))
            {
                return Error.Validation(
                    "OAuthApi.PermissionIdNotInAppCatalog",
                    $"PermissionId '{entry}' does not exist in App '{linkedApp.Slug}'s catalog.");
            }

            // Silent dedup on exact-id repeats — admin UIs may submit a
            // ticked-then-unticked-then-ticked entry as a duplicate.
            if (seen.Add(id)) parsed.Add(id);
        }

        return parsed;
    }
}
