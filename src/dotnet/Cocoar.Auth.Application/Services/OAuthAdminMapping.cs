using System.Security.Cryptography;
using System.Text.Json;
using BuildingBlocks.Helper;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Domain.OAuth.Apis;
using Cocoar.Auth.Domain.OAuth.Applications;
using Cocoar.Auth.Domain.OAuth.Common;
using Cocoar.Auth.Domain.OAuth.Scopes;

namespace Cocoar.Auth.Application.Services;

/// <summary>
/// Pure helper functions mirroring the private helpers inside
/// <see cref="OAuthAdminService"/> — permission list construction,
/// property/setting (de)serialization, BCrypt secret hashing, and
/// projection-state → DTO mapping. Extracted into an <c>internal static</c>
/// class so they can be pinned by sub-second unit tests without spinning up
/// Marten.
///
/// <para>NOTE: this class currently duplicates the bodies of the equivalent
/// private static methods in <see cref="OAuthAdminService"/>. If those
/// internals change, mirror the change here (the unit tests will fail loudly
/// if behaviour drifts from the encoded contract). A follow-up refactor can
/// have the service delegate here directly to remove the duplication.</para>
///
/// <para>Everything in here is stateless and side-effect-free except
/// <see cref="GenerateSecret"/> (uses CSPRNG) and <see cref="HashSecret"/> /
/// <see cref="VerifySecret"/> (BCrypt — slow by design).</para>
/// </summary>
internal static class OAuthAdminMapping
{
    // ───────────────────────────────────────────── Permissions ────────────────

    internal static List<string> BuildClientPermissions(
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

        // Empty input stays empty: a client created without explicit
        // grant types ends up with no token-flow permissions and won't
        // be able to mint tokens. This is intentional — silently
        // granting "authorization_code + refresh_token (+ client_credentials
        // for confidential)" as a fallback was over-privileging clients
        // whose admin simply hadn't typed anything yet. Force every
        // create / update to be explicit. The UI exposes a multi-select
        // so the user has to pick.
        foreach (var g in grantTypes)
        {
            var p = MapGrantTypeToPermission(g);
            if (p is not null) permissions.Add(p);
        }
        if (grantTypes.Contains("authorization_code"))
            permissions.Add(OAuthPermissions.ResponseTypes.Code);
        // <c>clientType</c> is intentionally unused now — the only place
        // it had effect was the removed Confidential→ClientCredentials
        // fallback above. Kept on the signature for callsite compatibility.
        _ = clientType;

        foreach (var scope in scopes)
            permissions.Add(OAuthPermissions.Prefixes.Scope + scope);

        return permissions;
    }

    internal static List<string> ExtractGrantTypes(IReadOnlyList<string> permissions) =>
        permissions
            .Where(p => p.StartsWith(OAuthPermissions.Prefixes.GrantType))
            .Select(MapPermissionToGrantType)
            .Where(g => g is not null)
            .Select(g => g!)
            .ToList();

    internal static List<string> ExtractScopes(IReadOnlyList<string> permissions) =>
        permissions
            .Where(p => p.StartsWith(OAuthPermissions.Prefixes.Scope))
            .Select(p => p.Substring(OAuthPermissions.Prefixes.Scope.Length))
            .ToList();

    internal static string? MapGrantTypeToPermission(string grantType) => grantType switch
    {
        "authorization_code" => OAuthPermissions.GrantTypes.AuthorizationCode,
        "client_credentials" => OAuthPermissions.GrantTypes.ClientCredentials,
        "refresh_token" => OAuthPermissions.GrantTypes.RefreshToken,
        "implicit" => OAuthPermissions.GrantTypes.Implicit,
        "password" => OAuthPermissions.GrantTypes.Password,
        "urn:ietf:params:oauth:grant-type:device_code" => OAuthPermissions.GrantTypes.DeviceCode,
        _ => null,
    };

    internal static string? MapPermissionToGrantType(string permission) => permission switch
    {
        OAuthPermissions.GrantTypes.AuthorizationCode => "authorization_code",
        OAuthPermissions.GrantTypes.ClientCredentials => "client_credentials",
        OAuthPermissions.GrantTypes.RefreshToken => "refresh_token",
        OAuthPermissions.GrantTypes.Implicit => "implicit",
        OAuthPermissions.GrantTypes.Password => "password",
        OAuthPermissions.GrantTypes.DeviceCode => "urn:ietf:params:oauth:grant-type:device_code",
        _ => null,
    };

    // ───────────────────────────────────────────── Settings / Properties ──────

    internal static Dictionary<string, string> BuildClientSettings(CreateOAuthClientDto dto)
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

    internal static Dictionary<string, object?> BuildClientProperties(
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

    internal static Dictionary<string, object?> BuildScopeProperties(
        bool enabled, bool required, bool emphasize, bool showInDiscovery,
        IReadOnlyList<string> userClaims, bool allowDcrClients = false)
        => new()
        {
            [ScopePropertyKeys.Enabled] = JsonSerializer.SerializeToElement(enabled),
            [ScopePropertyKeys.Required] = JsonSerializer.SerializeToElement(required),
            [ScopePropertyKeys.Emphasize] = JsonSerializer.SerializeToElement(emphasize),
            [ScopePropertyKeys.ShowInDiscoveryDocument] = JsonSerializer.SerializeToElement(showInDiscovery),
            [ScopePropertyKeys.UserClaims] = JsonSerializer.SerializeToElement(userClaims),
            [ScopePropertyKeys.AllowDynamicRegistrationClients] = JsonSerializer.SerializeToElement(allowDcrClients),
        };

    internal static Dictionary<string, object?> BuildApiProperties(bool allowDcr)
        => new()
        {
            [OAuthApiPropertyKeys.AllowDynamicRegistration] = JsonSerializer.SerializeToElement(allowDcr),
        };

    // ───────────────────────────────────────────── Property decoding ──────────

    internal static bool GetBoolProp(IDictionary<string, object?> props, string key, bool defaultValue)
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

    internal static List<string> GetStringListProp(IDictionary<string, object?> props, string key)
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

    internal static List<OAuthClientClaimDto> GetClaimsProp(IDictionary<string, object?> props)
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

    internal static bool DictEquals(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
        }
        return true;
    }

    // ───────────────────────────────────────────── PATCH merges ───────────────

    /// <summary>
    /// Merges an <see cref="UpdateOAuthClientDto"/> over an existing client's
    /// <c>Settings</c> dictionary using partial-PATCH semantics:
    /// <list type="bullet">
    ///   <item>Field absent on the DTO (Optional&lt;T&gt; without value, or
    ///     <c>null</c> for the <see cref="UpdateOAuthClientDto.ClientClaimsPrefix"/>
    ///     reference type) → preserved from <paramref name="current"/>.</item>
    ///   <item>Field present on the DTO → overwrites the corresponding setting
    ///     key. Numeric and enum values stringify via the invariant
    ///     <see cref="object.ToString"/>.</item>
    /// </list>
    /// Pure: returns a fresh dictionary; never mutates <paramref name="current"/>.
    /// </summary>
    internal static Dictionary<string, string> MergeClientSettings(
        IReadOnlyDictionary<string, string> current, UpdateOAuthClientDto dto)
    {
        var settings = new Dictionary<string, string>(current);
        if (dto.AccessTokenType.HasValue) settings[OAuthApplicationSettingKeys.AccessTokenType] = dto.AccessTokenType.Value.ToString();
        if (dto.RefreshTokenUsage.HasValue) settings[OAuthApplicationSettingKeys.RefreshTokenUsage] = dto.RefreshTokenUsage.Value.ToString();
        if (dto.IdentityTokenLifetime.HasValue) settings[OAuthApplicationSettingKeys.IdentityTokenLifetime] = dto.IdentityTokenLifetime.Value.ToString();
        if (dto.AccessTokenLifetime.HasValue) settings[OAuthApplicationSettingKeys.AccessTokenLifetime] = dto.AccessTokenLifetime.Value.ToString();
        if (dto.AuthorizationCodeLifetime.HasValue) settings[OAuthApplicationSettingKeys.AuthorizationCodeLifetime] = dto.AuthorizationCodeLifetime.Value.ToString();
        if (dto.AbsoluteRefreshTokenLifetime.HasValue) settings[OAuthApplicationSettingKeys.AbsoluteRefreshTokenLifetime] = dto.AbsoluteRefreshTokenLifetime.Value.ToString();
        if (dto.SlidingRefreshTokenLifetime.HasValue) settings[OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime] = dto.SlidingRefreshTokenLifetime.Value.ToString();
        if (dto.ClientClaimsPrefix is not null) settings[OAuthApplicationSettingKeys.ClientClaimsPrefix] = dto.ClientClaimsPrefix;
        return settings;
    }

    /// <summary>
    /// Merges an <see cref="UpdateOAuthClientDto"/> over the client's
    /// <c>Properties</c> dictionary. Each property field on the DTO is
    /// nullable; <c>null</c> means "not in the patch — preserve current"
    /// while a non-null value overwrites.
    /// <para>
    /// Defaults applied when a key is absent from <paramref name="current"/>
    /// follow the legacy IdP semantics (<c>Enabled=true</c>,
    /// <c>RequireClientSecret=true</c>, <c>EnableLocalLogin=true</c>,
    /// <c>AllowRememberConsent=true</c>; the rest default to <c>false</c> /
    /// empty list).
    /// </para>
    /// Pure: returns a fresh dictionary; never mutates <paramref name="current"/>.
    /// </summary>
    internal static Dictionary<string, object?> MergeClientProperties(
        IDictionary<string, object?> current, UpdateOAuthClientDto dto)
        => BuildClientProperties(
            enabled: dto.Enabled ?? GetBoolProp(current, OAuthApplicationPropertyKeys.Enabled, true),
            allowBrowser: dto.AllowAccessTokensViaBrowser ?? GetBoolProp(current, OAuthApplicationPropertyKeys.AllowAccessTokensViaBrowser, false),
            requireSecret: dto.RequireClientSecret ?? GetBoolProp(current, OAuthApplicationPropertyKeys.RequireClientSecret, true),
            enableLocal: dto.EnableLocalLogin ?? GetBoolProp(current, OAuthApplicationPropertyKeys.EnableLocalLogin, true),
            requireConsent: dto.RequireConsent ?? GetBoolProp(current, OAuthApplicationPropertyKeys.RequireConsent, false),
            allowRemember: dto.AllowRememberConsent ?? GetBoolProp(current, OAuthApplicationPropertyKeys.AllowRememberConsent, true),
            corsOrigins: dto.AllowedCorsOrigins ?? GetStringListProp(current, OAuthApplicationPropertyKeys.AllowedCorsOrigins),
            alwaysSend: dto.AlwaysSendClientClaims ?? GetBoolProp(current, OAuthApplicationPropertyKeys.AlwaysSendClientClaims, false),
            updateClaims: dto.UpdateAccessTokenClaimsOnRefresh ?? GetBoolProp(current, OAuthApplicationPropertyKeys.UpdateAccessTokenClaimsOnRefresh, false),
            claims: dto.Claims ?? GetClaimsProp(current),
            roles: dto.Roles ?? GetStringListProp(current, OAuthApplicationPropertyKeys.Roles));

    // ───────────────────────────────────────────── State → DTO ────────────────

    internal static OAuthClientDto MapClient(OAuthApplicationState s)
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
            AppIds = s.AppIds.Select(g => new ShortGuid(g).ToString()).ToList(),
        };
    }

    internal static OAuthScopeDto MapScope(OAuthScopeState s) => new()
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
        // Match the ShortGuid format the App-list endpoint already uses,
        // so the admin UI can equality-compare these values 1:1 with the
        // applications-store entries when applying the App-context filter.
        AppId = s.AppId is null ? null : new ShortGuid(s.AppId.Value).ToString(),
        IsStandard = StandardScopes.IsStandard(s.Name),
        AllowDynamicRegistrationClients = GetBoolProp(s.Properties, ScopePropertyKeys.AllowDynamicRegistrationClients, false),
    };

    /// <summary>
    /// Maps an <see cref="OAuthApiState"/> projection plus the secret entries
    /// from the matching <see cref="OAuthApiSecurityData"/> document into the
    /// API DTO. Secret hashes are intentionally never copied into the DTO —
    /// only the metadata (id, type, description, dates) carries through, so
    /// the response stays safe to serialise back to a UI / log.
    /// </summary>
    internal static OAuthApiDto MapApiState(OAuthApiState s, IEnumerable<ApiSecretEntry>? secrets, bool hasImplicitScope = false)
    {
        var secretDtos = secrets?.Select(x => new ApiSecretEntryDto
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
            AppId = s.AppId is null ? null : new ShortGuid(s.AppId.Value).ToString(),
            PermissionIds = s.PermissionIds.Select(id => new ShortGuid(id).ToString()).ToList(),
            Secrets = secretDtos,
            HasImplicitScope = hasImplicitScope,
            AllowDynamicRegistration = GetBoolProp(s.Properties, OAuthApiPropertyKeys.AllowDynamicRegistration, false),
        };
    }

    // ───────────────────────────────────────────── Secrets ────────────────────

    internal static string GenerateSecret()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        // OAUTH-16: URL-safe Base64. Standard Base64 emits +, /, = which need
        // URL-encoding when sent as form fields (client_secret=…) or path
        // segments — toolchains that forget to encode silently send a
        // different secret, leading to confusing 401s. Base64Url avoids
        // the class of bug.
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static string HashSecret(string secret) =>
        BCrypt.Net.BCrypt.HashPassword(secret, workFactor: 12);

    /// <summary>
    /// Pure constructor for an <see cref="ApiSecretEntry"/>. The non-pure work
    /// (generating a fresh <paramref name="secretId"/>, capturing
    /// <paramref name="createdAt"/>, hashing the plaintext into
    /// <paramref name="hashedValue"/>) stays at the call site; this helper only
    /// arranges the fields. Used by both initial-secret creation
    /// (CreateApiAsync) and ad-hoc secret creation (CreateApiSecretAsync).
    /// </summary>
    internal static ApiSecretEntry BuildApiSecretEntry(
        Guid secretId,
        string type,
        string hashedValue,
        string? description,
        DateTimeOffset? expiration,
        DateTimeOffset createdAt)
        => new()
        {
            SecretId = secretId,
            Type = type,
            HashedValue = hashedValue,
            Description = description,
            Expiration = expiration,
            CreatedAt = createdAt,
        };

    internal static bool VerifySecret(string secret, string hash)
    {
        try { return BCrypt.Net.BCrypt.Verify(secret, hash); }
        catch { return false; }
    }
}
