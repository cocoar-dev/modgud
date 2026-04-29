using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Domain.OAuth.Apis;
using Cocoar.Auth.Domain.OAuth.Applications;
using Cocoar.Auth.Domain.OAuth.Common;
using Cocoar.Auth.Domain.OAuth.Scopes;
using ErrorOr;
using Marten;

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

        // Settings — merge updates over current settings.
        var newSettings = new Dictionary<string, string>(aggregate.Settings);
        if (dto.AccessTokenType.HasValue) newSettings[OAuthApplicationSettingKeys.AccessTokenType] = dto.AccessTokenType.Value.ToString();
        if (dto.RefreshTokenUsage.HasValue) newSettings[OAuthApplicationSettingKeys.RefreshTokenUsage] = dto.RefreshTokenUsage.Value.ToString();
        if (dto.IdentityTokenLifetime.HasValue) newSettings[OAuthApplicationSettingKeys.IdentityTokenLifetime] = dto.IdentityTokenLifetime.Value.ToString();
        if (dto.AccessTokenLifetime.HasValue) newSettings[OAuthApplicationSettingKeys.AccessTokenLifetime] = dto.AccessTokenLifetime.Value.ToString();
        if (dto.AuthorizationCodeLifetime.HasValue) newSettings[OAuthApplicationSettingKeys.AuthorizationCodeLifetime] = dto.AuthorizationCodeLifetime.Value.ToString();
        if (dto.AbsoluteRefreshTokenLifetime.HasValue) newSettings[OAuthApplicationSettingKeys.AbsoluteRefreshTokenLifetime] = dto.AbsoluteRefreshTokenLifetime.Value.ToString();
        if (dto.SlidingRefreshTokenLifetime.HasValue) newSettings[OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime] = dto.SlidingRefreshTokenLifetime.Value.ToString();
        if (dto.ClientClaimsPrefix is not null) newSettings[OAuthApplicationSettingKeys.ClientClaimsPrefix] = dto.ClientClaimsPrefix;
        if (!DictEquals(newSettings, aggregate.Settings))
            _session.Events.Append(guid, aggregate.SetSettings(newSettings));

        // Properties — merge over current properties.
        var current = aggregate.Properties;
        var enabled = dto.Enabled ?? GetBoolProp(current, OAuthApplicationPropertyKeys.Enabled, true);
        var allowBrowser = dto.AllowAccessTokensViaBrowser ?? GetBoolProp(current, OAuthApplicationPropertyKeys.AllowAccessTokensViaBrowser, false);
        var requireSecret = dto.RequireClientSecret ?? GetBoolProp(current, OAuthApplicationPropertyKeys.RequireClientSecret, true);
        var enableLocal = dto.EnableLocalLogin ?? GetBoolProp(current, OAuthApplicationPropertyKeys.EnableLocalLogin, true);
        var requireConsent = dto.RequireConsent ?? GetBoolProp(current, OAuthApplicationPropertyKeys.RequireConsent, false);
        var allowRemember = dto.AllowRememberConsent ?? GetBoolProp(current, OAuthApplicationPropertyKeys.AllowRememberConsent, true);
        var corsOrigins = dto.AllowedCorsOrigins ?? GetStringListProp(current, OAuthApplicationPropertyKeys.AllowedCorsOrigins);
        var alwaysSend = dto.AlwaysSendClientClaims ?? GetBoolProp(current, OAuthApplicationPropertyKeys.AlwaysSendClientClaims, false);
        var updateClaims = dto.UpdateAccessTokenClaimsOnRefresh ?? GetBoolProp(current, OAuthApplicationPropertyKeys.UpdateAccessTokenClaimsOnRefresh, false);
        var claims = dto.Claims ?? GetClaimsProp(current);
        var roles = dto.Roles ?? GetStringListProp(current, OAuthApplicationPropertyKeys.Roles);

        var newProps = BuildClientProperties(
            enabled, allowBrowser, requireSecret, enableLocal, requireConsent, allowRemember,
            corsOrigins, alwaysSend, updateClaims, claims, roles);
        _session.Events.Append(guid, aggregate.SetProperties(newProps));

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
        sec.Secrets.Add(new ApiSecretEntry
        {
            Type = "SharedSecret",
            HashedValue = sec.ApiSecret,
            Description = "Initial secret",
            CreatedAt = DateTimeOffset.UtcNow,
        });
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
        var entry = new ApiSecretEntry
        {
            Type = dto.Type,
            HashedValue = hashed,
            Description = dto.Description,
            Expiration = dto.Expiration,
            CreatedAt = DateTimeOffset.UtcNow,
        };

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

    private static OAuthClientDto MapClient(OAuthApplicationState s)
    {
        var props = s.Properties;
        var settings = s.Settings;

        var accessTokenType = AccessTokenType.Reference;
        if (settings.TryGetValue(OAuthApplicationSettingKeys.AccessTokenType, out var v1) &&
            Enum.TryParse<AccessTokenType>(v1, out var parsed1))
            accessTokenType = parsed1;

        var refreshTokenUsage = RefreshTokenUsage.OneTimeOnly;
        if (settings.TryGetValue(OAuthApplicationSettingKeys.RefreshTokenUsage, out var v2) &&
            Enum.TryParse<RefreshTokenUsage>(v2, out var parsed2))
            refreshTokenUsage = parsed2;

        int? GetIntSetting(string key) =>
            settings.TryGetValue(key, out var sv) && int.TryParse(sv, out var iv) ? iv : null;

        settings.TryGetValue(OAuthApplicationSettingKeys.ClientClaimsPrefix, out var prefix);

        return new OAuthClientDto
        {
            Id = s.Id.ToString(),
            ClientId = s.ClientId,
            DisplayName = s.DisplayName,
            ClientType = s.ClientType ?? OAuthClientTypes.Public,
            ConsentType = s.ConsentType ?? OAuthConsentTypes.Explicit,
            Permissions = s.Permissions.ToList(),
            RedirectUris = s.RedirectUris.ToList(),
            PostLogoutRedirectUris = s.PostLogoutRedirectUris.ToList(),
            AccessTokenType = accessTokenType,
            Enabled = GetBoolProp(props, OAuthApplicationPropertyKeys.Enabled, true),
            RefreshTokenUsage = refreshTokenUsage,
            AllowAccessTokensViaBrowser = GetBoolProp(props, OAuthApplicationPropertyKeys.AllowAccessTokensViaBrowser, false),
            RequireClientSecret = GetBoolProp(props, OAuthApplicationPropertyKeys.RequireClientSecret, true),
            EnableLocalLogin = GetBoolProp(props, OAuthApplicationPropertyKeys.EnableLocalLogin, true),
            RequireConsent = GetBoolProp(props, OAuthApplicationPropertyKeys.RequireConsent, false),
            AllowRememberConsent = GetBoolProp(props, OAuthApplicationPropertyKeys.AllowRememberConsent, true),
            AllowedGrantTypes = ExtractGrantTypes(s.Permissions),
            AllowedCorsOrigins = GetStringListProp(props, OAuthApplicationPropertyKeys.AllowedCorsOrigins),
            IdentityTokenLifetime = GetIntSetting(OAuthApplicationSettingKeys.IdentityTokenLifetime),
            AccessTokenLifetime = GetIntSetting(OAuthApplicationSettingKeys.AccessTokenLifetime),
            AuthorizationCodeLifetime = GetIntSetting(OAuthApplicationSettingKeys.AuthorizationCodeLifetime),
            AbsoluteRefreshTokenLifetime = GetIntSetting(OAuthApplicationSettingKeys.AbsoluteRefreshTokenLifetime),
            SlidingRefreshTokenLifetime = GetIntSetting(OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime),
            AlwaysSendClientClaims = GetBoolProp(props, OAuthApplicationPropertyKeys.AlwaysSendClientClaims, false),
            UpdateAccessTokenClaimsOnRefresh = GetBoolProp(props, OAuthApplicationPropertyKeys.UpdateAccessTokenClaimsOnRefresh, false),
            ClientClaimsPrefix = prefix,
            Claims = GetClaimsProp(props),
            Roles = GetStringListProp(props, OAuthApplicationPropertyKeys.Roles),
        };
    }

    private static OAuthScopeDto MapScope(OAuthScopeState s) => new()
    {
        Id = s.Id.ToString(),
        Name = s.Name,
        DisplayName = s.DisplayName,
        Description = s.Description,
        Resources = s.Resources.ToList(),
        Enabled = s.Enabled,
        Required = s.Required,
        Emphasize = s.Emphasize,
        ShowInDiscoveryDocument = s.ShowInDiscoveryDocument,
        UserClaims = s.UserClaims.ToList(),
    };

    private async Task<OAuthApiDto> MapApiAsync(OAuthApiState s, CancellationToken ct)
    {
        var sec = await _session.LoadAsync<OAuthApiSecurityData>(s.Id, ct);
        var secrets = sec?.Secrets.Select(x => new ApiSecretEntryDto
        {
            SecretId = x.SecretId.ToString(),
            Type = x.Type,
            Description = x.Description,
            Expiration = x.Expiration,
            CreatedAt = x.CreatedAt,
        }).ToList() ?? new List<ApiSecretEntryDto>();

        return new OAuthApiDto
        {
            Id = s.Id.ToString(),
            Name = s.Name,
            DisplayName = s.DisplayName,
            Description = s.Description,
            Enabled = s.Enabled,
            Scopes = s.Scopes.ToList(),
            UserClaims = s.UserClaims.ToList(),
            Secrets = secrets,
        };
    }

    private static List<string> BuildClientPermissions(
        IReadOnlyList<string> grantTypes, IReadOnlyList<string> scopes, string clientType)
    {
        var permissions = new List<string>
        {
            OAuthPermissions.Endpoints.Authorization,
            OAuthPermissions.Endpoints.Token,
            OAuthPermissions.Endpoints.EndSession,
            OAuthPermissions.Endpoints.Introspection,
            OAuthPermissions.Endpoints.Revocation,
            OAuthPermissions.Endpoints.DeviceAuthorization,
        };

        if (grantTypes.Count > 0)
        {
            foreach (var g in grantTypes)
            {
                var p = MapGrantTypeToPermission(g);
                if (p is not null) permissions.Add(p);
            }
            if (grantTypes.Contains("authorization_code"))
                permissions.Add(OAuthPermissions.ResponseTypes.Code);
        }
        else
        {
            permissions.Add(OAuthPermissions.GrantTypes.AuthorizationCode);
            permissions.Add(OAuthPermissions.GrantTypes.RefreshToken);
            permissions.Add(OAuthPermissions.ResponseTypes.Code);
            if (clientType == OAuthClientTypes.Confidential)
                permissions.Add(OAuthPermissions.GrantTypes.ClientCredentials);
        }

        foreach (var scope in scopes)
            permissions.Add(OAuthPermissions.Prefixes.Scope + scope);

        return permissions;
    }

    private static Dictionary<string, string> BuildClientSettings(CreateOAuthClientDto dto)
    {
        var settings = new Dictionary<string, string>
        {
            [OAuthApplicationSettingKeys.AccessTokenType] = dto.AccessTokenType.ToString(),
            [OAuthApplicationSettingKeys.RefreshTokenUsage] = dto.RefreshTokenUsage.ToString(),
        };
        if (dto.IdentityTokenLifetime.HasValue) settings[OAuthApplicationSettingKeys.IdentityTokenLifetime] = dto.IdentityTokenLifetime.Value.ToString();
        if (dto.AccessTokenLifetime.HasValue) settings[OAuthApplicationSettingKeys.AccessTokenLifetime] = dto.AccessTokenLifetime.Value.ToString();
        if (dto.AuthorizationCodeLifetime.HasValue) settings[OAuthApplicationSettingKeys.AuthorizationCodeLifetime] = dto.AuthorizationCodeLifetime.Value.ToString();
        if (dto.AbsoluteRefreshTokenLifetime.HasValue) settings[OAuthApplicationSettingKeys.AbsoluteRefreshTokenLifetime] = dto.AbsoluteRefreshTokenLifetime.Value.ToString();
        if (dto.SlidingRefreshTokenLifetime.HasValue) settings[OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime] = dto.SlidingRefreshTokenLifetime.Value.ToString();
        if (dto.ClientClaimsPrefix is not null) settings[OAuthApplicationSettingKeys.ClientClaimsPrefix] = dto.ClientClaimsPrefix;
        return settings;
    }

    private static Dictionary<string, object?> BuildClientProperties(
        bool enabled, bool allowBrowser, bool requireSecret, bool enableLocal,
        bool requireConsent, bool allowRemember, IReadOnlyList<string> corsOrigins,
        bool alwaysSend, bool updateClaims,
        IReadOnlyList<OAuthClientClaimDto> claims, IReadOnlyList<string> roles)
        => new()
        {
            [OAuthApplicationPropertyKeys.Enabled] = JsonSerializer.SerializeToElement(enabled),
            [OAuthApplicationPropertyKeys.AllowAccessTokensViaBrowser] = JsonSerializer.SerializeToElement(allowBrowser),
            [OAuthApplicationPropertyKeys.RequireClientSecret] = JsonSerializer.SerializeToElement(requireSecret),
            [OAuthApplicationPropertyKeys.EnableLocalLogin] = JsonSerializer.SerializeToElement(enableLocal),
            [OAuthApplicationPropertyKeys.RequireConsent] = JsonSerializer.SerializeToElement(requireConsent),
            [OAuthApplicationPropertyKeys.AllowRememberConsent] = JsonSerializer.SerializeToElement(allowRemember),
            [OAuthApplicationPropertyKeys.AllowedCorsOrigins] = JsonSerializer.SerializeToElement(corsOrigins),
            [OAuthApplicationPropertyKeys.AlwaysSendClientClaims] = JsonSerializer.SerializeToElement(alwaysSend),
            [OAuthApplicationPropertyKeys.UpdateAccessTokenClaimsOnRefresh] = JsonSerializer.SerializeToElement(updateClaims),
            [OAuthApplicationPropertyKeys.ClientClaims] = JsonSerializer.SerializeToElement(claims),
            [OAuthApplicationPropertyKeys.Roles] = JsonSerializer.SerializeToElement(roles),
        };

    private static Dictionary<string, object?> BuildScopeProperties(
        bool enabled, bool required, bool emphasize, bool showInDiscovery, IReadOnlyList<string> userClaims)
        => new()
        {
            [ScopePropertyKeys.Enabled] = JsonSerializer.SerializeToElement(enabled),
            [ScopePropertyKeys.Required] = JsonSerializer.SerializeToElement(required),
            [ScopePropertyKeys.Emphasize] = JsonSerializer.SerializeToElement(emphasize),
            [ScopePropertyKeys.ShowInDiscoveryDocument] = JsonSerializer.SerializeToElement(showInDiscovery),
            [ScopePropertyKeys.UserClaims] = JsonSerializer.SerializeToElement(userClaims),
        };

    private static List<string> ExtractGrantTypes(IReadOnlyList<string> permissions) =>
        permissions
            .Where(p => p.StartsWith(OAuthPermissions.Prefixes.GrantType))
            .Select(MapPermissionToGrantType)
            .Where(g => g is not null)
            .Select(g => g!)
            .ToList();

    private static List<string> ExtractScopes(IReadOnlyList<string> permissions) =>
        permissions
            .Where(p => p.StartsWith(OAuthPermissions.Prefixes.Scope))
            .Select(p => p.Substring(OAuthPermissions.Prefixes.Scope.Length))
            .ToList();

    private static string? MapGrantTypeToPermission(string grantType) => grantType switch
    {
        "authorization_code" => OAuthPermissions.GrantTypes.AuthorizationCode,
        "client_credentials" => OAuthPermissions.GrantTypes.ClientCredentials,
        "refresh_token" => OAuthPermissions.GrantTypes.RefreshToken,
        "implicit" => OAuthPermissions.GrantTypes.Implicit,
        "password" => OAuthPermissions.GrantTypes.Password,
        "urn:ietf:params:oauth:grant-type:device_code" => OAuthPermissions.GrantTypes.DeviceCode,
        _ => null,
    };

    private static string? MapPermissionToGrantType(string permission) => permission switch
    {
        OAuthPermissions.GrantTypes.AuthorizationCode => "authorization_code",
        OAuthPermissions.GrantTypes.ClientCredentials => "client_credentials",
        OAuthPermissions.GrantTypes.RefreshToken => "refresh_token",
        OAuthPermissions.GrantTypes.Implicit => "implicit",
        OAuthPermissions.GrantTypes.Password => "password",
        OAuthPermissions.GrantTypes.DeviceCode => "urn:ietf:params:oauth:grant-type:device_code",
        _ => null,
    };

    private static bool GetBoolProp(IDictionary<string, object?> props, string key, bool defaultValue)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null) return defaultValue;
        return raw switch
        {
            bool b => b,
            JsonElement e when e.ValueKind is JsonValueKind.True => true,
            JsonElement e when e.ValueKind is JsonValueKind.False => false,
            _ => defaultValue,
        };
    }

    private static List<string> GetStringListProp(IDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null) return new List<string>();
        return raw switch
        {
            JsonElement e when e.ValueKind == JsonValueKind.Array => e.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToList(),
            IEnumerable<string> list => list.ToList(),
            _ => new List<string>(),
        };
    }

    private static List<OAuthClientClaimDto> GetClaimsProp(IDictionary<string, object?> props)
    {
        if (!props.TryGetValue(OAuthApplicationPropertyKeys.ClientClaims, out var raw) || raw is null)
            return new();
        if (raw is not JsonElement element || element.ValueKind != JsonValueKind.Array)
            return new();

        var claims = new List<OAuthClientClaimDto>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String &&
                item.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.String)
            {
                claims.Add(new OAuthClientClaimDto { Type = t.GetString()!, Value = v.GetString()! });
            }
        }
        return claims;
    }

    private static bool DictEquals(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
        }
        return true;
    }

    private static string GenerateSecret()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static string HashSecret(string secret) =>
        BCrypt.Net.BCrypt.HashPassword(secret, workFactor: 12);

    private static bool VerifySecret(string secret, string hash)
    {
        try { return BCrypt.Net.BCrypt.Verify(secret, hash); }
        catch { return false; }
    }
}
