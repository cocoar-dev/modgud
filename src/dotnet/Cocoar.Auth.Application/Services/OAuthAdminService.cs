using System.Collections.Immutable;
using System.Text.Json;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Domain.OAuth.Apis;
using Cocoar.Auth.Domain.OAuth.Applications;
using Cocoar.Auth.Domain.OAuth.Common;
using Cocoar.Auth.Domain.OAuth.Scopes;
using ErrorOr;
using Marten;
using static Cocoar.Auth.Application.Services.OAuthAdminMapping;

namespace Cocoar.Auth.Application.Services;

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

    public async Task<ErrorOr<OAuthClientCreatedDto>> CreateClientAsync(
        CreateOAuthClientDto dto, CancellationToken ct = default)
    {
        if (dto.ClientType is not (OAuthClientTypes.Public or OAuthClientTypes.Confidential))
            return OAuthErrors.InvalidClientType(dto.ClientType);

        if (dto.ConsentType is not (OAuthConsentTypes.Explicit or OAuthConsentTypes.Implicit or OAuthConsentTypes.External))
            return OAuthErrors.InvalidConsentType(dto.ConsentType);

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
                if (!Guid.TryParse(raw, out var parsed))
                    return Error.Validation("OAuthClient.InvalidAppId", $"AppId '{raw}' is not a valid Guid.");
                var app = await _session.LoadAsync<App>(parsed, ct);
                if (app is null || app.IsDeleted)
                    return Error.Validation("OAuthClient.AppNotFound", $"App {raw} not found.");
                appIds.Add(parsed);
            }
        }

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
                if (!Guid.TryParse(raw, out var parsedId))
                    return Error.Validation("OAuthClient.InvalidAppId", $"AppId '{raw}' is not a valid Guid.");
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

        var newSecret = GenerateSecret();
        var sec = await _session.LoadAsync<OAuthApplicationSecurityData>(guid, ct)
                  ?? OAuthApplicationSecurityData.Create(guid);
        sec.ClientSecret = HashSecret(newSecret);
        sec.UpdateConcurrencyToken();
        _session.Store(sec);

        await _session.SaveChangesAsync(ct);
        return new ClientSecretDto { ClientSecret = newSecret };
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
            if (!Guid.TryParse(dto.AppId, out var parsedAppId))
                return Error.Validation("OAuthScope.InvalidAppId", $"AppId '{dto.AppId}' is not a valid Guid.");
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
            dto.Enabled, dto.Required, dto.Emphasize, dto.ShowInDiscoveryDocument, dto.UserClaims)));

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
            _session.Events.Append(guid, aggregate.SetResources(dto.Resources));

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
        _session.Events.Append(guid, aggregate.SetProperties(BuildScopeProperties(
            dto.Enabled ?? aggregate.Enabled,
            dto.Required ?? aggregate.Required,
            dto.Emphasize ?? aggregate.Emphasize,
            dto.ShowInDiscoveryDocument ?? aggregate.ShowInDiscoveryDocument,
            dto.UserClaims ?? aggregate.UserClaims)));

        // App-link patch — null=no change, ""=make global, "guid"=assign.
        if (dto.AppId is not null)
        {
            Guid? newAppId = null;
            if (dto.AppId.Length > 0)
            {
                if (!Guid.TryParse(dto.AppId, out var parsedAppId))
                    return Error.Validation("OAuthScope.InvalidAppId", $"AppId '{dto.AppId}' is not a valid Guid.");
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
        var existing = await _session.Query<OAuthApiState>()
            .FirstOrDefaultAsync(x => x.Name == dto.Name && !x.IsDeleted, ct);
        if (existing is not null)
            return OAuthErrors.ApiNameAlreadyExists(dto.Name);

        var id = Guid.NewGuid();
        var (aggregate, createdEvent) = OAuthApiAggregate.Create(id, dto.Name, dto.DisplayName, dto.Description, dto.Enabled, dto.Scopes);
        _session.Events.StartStream<OAuthApiAggregate>(id, createdEvent);

        if (dto.UserClaims.Count > 0)
            _session.Events.Append(id, aggregate.SetUserClaims(dto.UserClaims));

        // Initial API secret — stored in OAuthApiSecurityData (BCrypt-hashed).
        var apiSecret = GenerateSecret();
        var sec = OAuthApiSecurityData.Create(id);
        sec.ApiSecret = HashSecret(apiSecret);
        sec.Secrets.Add(BuildApiSecretEntry(
            secretId: Guid.NewGuid(),
            type: "SharedSecret",
            hashedValue: sec.ApiSecret,
            description: "Initial secret",
            expiration: null,
            createdAt: DateTimeOffset.UtcNow));
        _session.Store(sec);

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
            ApiSecret = apiSecret,
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
        _session.Delete<OAuthApiSecurityData>(guid);

        await _session.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ErrorOr<ApiSecretDto>> RegenerateApiSecretAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return OAuthErrors.ApiNotFound(id);

        var state = await _session.LoadAsync<OAuthApiState>(guid, ct);
        if (state is null || state.IsDeleted)
            return OAuthErrors.ApiNotFound(id);

        var newSecret = GenerateSecret();
        var sec = await _session.LoadAsync<OAuthApiSecurityData>(guid, ct)
                  ?? OAuthApiSecurityData.Create(guid);
        sec.ApiSecret = HashSecret(newSecret);
        sec.UpdateConcurrencyToken();
        _session.Store(sec);

        await _session.SaveChangesAsync(ct);
        return new ApiSecretDto { ApiSecret = newSecret };
    }

    public async Task<ErrorOr<ApiSecretCreatedDto>> CreateApiSecretAsync(
        string id, CreateApiSecretDto dto, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return OAuthErrors.ApiNotFound(id);

        var state = await _session.LoadAsync<OAuthApiState>(guid, ct);
        if (state is null || state.IsDeleted)
            return OAuthErrors.ApiNotFound(id);

        var sec = await _session.LoadAsync<OAuthApiSecurityData>(guid, ct)
                  ?? OAuthApiSecurityData.Create(guid);

        var newSecret = GenerateSecret();
        var hashed = HashSecret(newSecret);
        var entry = BuildApiSecretEntry(
            secretId: Guid.NewGuid(),
            type: dto.Type,
            hashedValue: hashed,
            description: dto.Description,
            expiration: dto.Expiration,
            createdAt: DateTimeOffset.UtcNow);

        sec.Secrets.Add(entry);
        // Sync legacy single-secret slot to the latest entry for back-compat.
        sec.ApiSecret = hashed;
        sec.UpdateConcurrencyToken();
        _session.Store(sec);

        await _session.SaveChangesAsync(ct);

        return new ApiSecretCreatedDto
        {
            SecretId = entry.SecretId.ToString(),
            ApiSecret = newSecret,
        };
    }

    public async Task<ErrorOr<bool>> DeleteApiSecretAsync(
        string id, string secretId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return OAuthErrors.ApiNotFound(id);
        if (!Guid.TryParse(secretId, out var secretGuid))
            return OAuthErrors.ApiSecretNotFound(secretId);

        var state = await _session.LoadAsync<OAuthApiState>(guid, ct);
        if (state is null || state.IsDeleted)
            return OAuthErrors.ApiNotFound(id);

        var sec = await _session.LoadAsync<OAuthApiSecurityData>(guid, ct);
        if (sec is null) return OAuthErrors.ApiSecretNotFound(secretId);

        var entry = sec.Secrets.FirstOrDefault(s => s.SecretId == secretGuid);
        if (entry is null) return OAuthErrors.ApiSecretNotFound(secretId);

        sec.Secrets.Remove(entry);
        sec.ApiSecret = sec.Secrets.LastOrDefault()?.HashedValue;
        sec.UpdateConcurrencyToken();
        _session.Store(sec);

        await _session.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ValidateApiCredentialsAsync(
        string name, string secret, CancellationToken ct = default)
    {
        var api = await _session.Query<OAuthApiState>()
            .FirstOrDefaultAsync(x => x.Name == name && !x.IsDeleted && x.Enabled, ct);
        if (api is null) return false;

        var sec = await _session.LoadAsync<OAuthApiSecurityData>(api.Id, ct);
        if (sec is null) return false;

        foreach (var entry in sec.Secrets)
        {
            if (entry.Expiration.HasValue && entry.Expiration.Value < DateTimeOffset.UtcNow) continue;
            if (VerifySecret(secret, entry.HashedValue)) return true;
        }

        return sec.ApiSecret is not null && VerifySecret(secret, sec.ApiSecret);
    }

    // ───────────────────────────────────────────── Helpers ─────────────────────
    // Pure helpers (BuildClientPermissions, MapClient, etc.) live in
    // OAuthAdminMapping and are imported via `using static` above. The only
    // remaining instance helper is MapApiAsync, which loads secrets from session.

    private async Task<OAuthApiDto> MapApiAsync(OAuthApiState s, CancellationToken ct)
    {
        var sec = await _session.LoadAsync<OAuthApiSecurityData>(s.Id, ct);
        return MapApiState(s, sec?.Secrets);
    }
}
