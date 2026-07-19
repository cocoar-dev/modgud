using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BuildingBlocks.Helper;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Errors;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.OAuth.Scopes;
using ErrorOr;

namespace Modgud.Application.Services;

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
            // RFC 9126 (#118) — every client may use Pushed Authorization
            // Requests. PAR strictly hardens the front channel, so there's no
            // reason to gate it: grant it alongside the authorization endpoint.
            OAuthPermissions.Endpoints.PushedAuthorization,
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

    /// <summary>Builds the OpenIddict <c>Requirements</c> list from the client's
    /// requirement toggles. Currently only the RFC 9126 PAR requirement.</summary>
    internal static List<string> BuildClientRequirements(bool requirePushedAuthorizationRequests)
    {
        var requirements = new List<string>();
        if (requirePushedAuthorizationRequests)
            requirements.Add(OAuthPermissions.Requirements.PushedAuthorizationRequests);
        return requirements;
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
        // ADR-0010 — native (cookieless) passwordless grants. Admin-set per-client
        // opt-in surfaces here so the OAuth-client admin CRUD can grant them.
        CocoarGrantTypes.Otp => OAuthPermissions.GrantTypes.CocoarOtp,
        CocoarGrantTypes.Magic => OAuthPermissions.GrantTypes.CocoarMagic,
        CocoarGrantTypes.Passkey => OAuthPermissions.GrantTypes.CocoarPasskey,
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
        OAuthPermissions.GrantTypes.CocoarOtp => CocoarGrantTypes.Otp,
        OAuthPermissions.GrantTypes.CocoarMagic => CocoarGrantTypes.Magic,
        OAuthPermissions.GrantTypes.CocoarPasskey => CocoarGrantTypes.Passkey,
        _ => null,
    };

    // ───────────────────────────────────────────── Settings / Properties ──────

    internal static Dictionary<string, string> BuildClientSettings(CreateOAuthClientDto dto)
    {
        var settings = new Dictionary<string, string>
        {
            [OAuthApplicationSettingKeys.AccessTokenType] = dto.AccessTokenType.ToString(),
        };
        if (dto.IdentityTokenLifetime.HasValue) settings[OAuthApplicationSettingKeys.IdentityTokenLifetime] = dto.IdentityTokenLifetime.Value.ToString();
        if (dto.AccessTokenLifetime.HasValue) settings[OAuthApplicationSettingKeys.AccessTokenLifetime] = dto.AccessTokenLifetime.Value.ToString();
        if (dto.AuthorizationCodeLifetime.HasValue) settings[OAuthApplicationSettingKeys.AuthorizationCodeLifetime] = dto.AuthorizationCodeLifetime.Value.ToString();
        if (dto.SlidingRefreshTokenLifetime.HasValue) settings[OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime] = dto.SlidingRefreshTokenLifetime.Value.ToString();
        if (dto.ClientClaimsPrefix is not null) settings[OAuthApplicationSettingKeys.ClientClaimsPrefix] = dto.ClientClaimsPrefix;
        // ADR-0009 — store the normalized (trimmed, lowercased) per-client RP ID; a
        // blank value leaves it realm-scoped (no key). Format is validated upstream.
        if (!string.IsNullOrWhiteSpace(dto.WebAuthnRpId)) settings[OAuthApplicationSettingKeys.WebAuthnRpId] = dto.WebAuthnRpId.Trim().ToLowerInvariant();
        return settings;
    }

    internal static Dictionary<string, object?> BuildClientProperties(
        bool enabled, bool allowBrowser, bool requireSecret, bool enableLocal,
        bool requireConsent, bool allowRemember, IReadOnlyList<string> corsOrigins,
        bool alwaysSend, bool updateClaims,
        IReadOnlyList<OAuthClientClaimDto> claims, IReadOnlyList<string> roles,
        // RFC 9449 (#118) — trailing optional so existing call sites (and the pin
        // tests that don't care about DPoP) keep compiling; the two real callers
        // pass it explicitly. Always written (like the other flags) so it's
        // queryable regardless of when it was set.
        bool requireDpop = false,
        bool requireDpopNonce = false)
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
            [OAuthApplicationPropertyKeys.RequireDpop] = JsonSerializer.SerializeToElement(requireDpop),
            [OAuthApplicationPropertyKeys.RequireDpopNonce] = JsonSerializer.SerializeToElement(requireDpopNonce),
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
        if (dto.IdentityTokenLifetime.HasValue) settings[OAuthApplicationSettingKeys.IdentityTokenLifetime] = dto.IdentityTokenLifetime.Value.ToString();
        if (dto.AccessTokenLifetime.HasValue) settings[OAuthApplicationSettingKeys.AccessTokenLifetime] = dto.AccessTokenLifetime.Value.ToString();
        if (dto.AuthorizationCodeLifetime.HasValue) settings[OAuthApplicationSettingKeys.AuthorizationCodeLifetime] = dto.AuthorizationCodeLifetime.Value.ToString();
        if (dto.SlidingRefreshTokenLifetime.HasValue) settings[OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime] = dto.SlidingRefreshTokenLifetime.Value.ToString();
        if (dto.ClientClaimsPrefix is not null) settings[OAuthApplicationSettingKeys.ClientClaimsPrefix] = dto.ClientClaimsPrefix;
        // ADR-0009 PATCH: null = omit; empty/blank = clear back to realm-scoped;
        // non-blank = set (normalized). Format is validated upstream.
        if (dto.WebAuthnRpId is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.WebAuthnRpId))
                settings.Remove(OAuthApplicationSettingKeys.WebAuthnRpId);
            else
                settings[OAuthApplicationSettingKeys.WebAuthnRpId] = dto.WebAuthnRpId.Trim().ToLowerInvariant();
        }
        return settings;
    }

    // ───────────────────────────── Native token-lifetime wiring (issue #115) ──
    //
    // The admin UI collects Identity/Access/Sliding-Refresh token lifetimes
    // for standard (manually created) clients in SECONDS (see ClientDetails.
    // vue's "Werte in Sekunden" hint) and, until issue #115, only ever wrote
    // them into the display-only modgud:* Settings keys above — no OpenIddict
    // handler reads those, so the values had zero effect. This mirrors the
    // DCR/CIMD/native-grants path by ALSO writing OpenIddict's own tkn_lft:*
    // Settings keys, which OpenIddict's EvaluateGeneratedTokens pipeline reads
    // natively at token-issue time (empirically confirmed via decompilation —
    // OpenIddict.Server's per-application settings reader keys off exactly
    // these strings). Issue #130 extended the same wiring to
    // AuthorizationCodeLifetime, which had shipped as a display-only field
    // with no effect until then. The modgud:* keys are kept so MapClient's
    // round-trip to the admin UI is unaffected.
    //
    // OpenIddict has no distinct "absolute" vs "sliding" refresh-token
    // concept — a single tkn_lft:reft governs the lifetime applied at each
    // (rolling, one-time-use) refresh-token mint, which is a sliding window
    // by construction. That's why only SlidingRefreshTokenLifetime survives
    // as a DTO field; AbsoluteRefreshTokenLifetime was removed (no native
    // equivalent to wire it to).

    /// <summary>OpenIddict's well-known Settings key for per-application
    /// identity-token lifetime (<c>OpenIddictConstants.Settings.
    /// TokenLifetimes.IdentityToken</c>). Inlined to avoid pulling
    /// OpenIddict.Abstractions into the Application layer; pinned against
    /// drift by <c>OpenIddictLifetimeSettingKeysTests</c>.</summary>
    internal const string OpenIddictIdentityTokenLifetimeSettingKey = "tkn_lft:idt";

    /// <summary>OpenIddict's well-known Settings key for per-application
    /// access-token lifetime (<c>OpenIddictConstants.Settings.
    /// TokenLifetimes.AccessToken</c>). See
    /// <see cref="OpenIddictIdentityTokenLifetimeSettingKey"/>.</summary>
    internal const string OpenIddictAccessTokenLifetimeSettingKey = "tkn_lft:act";

    /// <summary>OpenIddict's well-known Settings key for per-application
    /// refresh-token lifetime (<c>OpenIddictConstants.Settings.
    /// TokenLifetimes.RefreshToken</c>). See
    /// <see cref="OpenIddictIdentityTokenLifetimeSettingKey"/>.</summary>
    internal const string OpenIddictRefreshTokenLifetimeSettingKey = "tkn_lft:reft";

    /// <summary>OpenIddict's well-known Settings key for per-application
    /// authorization-code lifetime (<c>OpenIddictConstants.Settings.
    /// TokenLifetimes.AuthorizationCode</c>, confirmed present via
    /// reflection against OpenIddict.Abstractions 7.5.0 — issue #130). See
    /// <see cref="OpenIddictIdentityTokenLifetimeSettingKey"/>.</summary>
    internal const string OpenIddictAuthorizationCodeLifetimeSettingKey = "tkn_lft:auc";

    // Same 1..60 minute bound RealmSettingsService.ValidateTokenLifetimes
    // applies to the realm-wide DCR/CIMD/native-grants access-token
    // lifetime — a per-client override shouldn't be able to create a
    // materially different security posture than the realm-wide knobs.
    // Reused for the identity token too: both are short-lived, minted fresh
    // per-authentication, and neither is independently revocable.
    // Modgud.Application cannot reference Modgud.Authentication (see
    // RealmSettingsService), so the bound is duplicated rather than shared.
    private const int MinShortLivedTokenSeconds = 60;
    private const int MaxShortLivedTokenSeconds = 60 * 60;

    // Same 1..30 day bound RealmSettingsService.ValidateTokenLifetimes
    // applies to the realm-wide refresh-token lifetime.
    private const int MinRefreshTokenSeconds = 60 * 60 * 24;
    private const int MaxRefreshTokenSeconds = 60 * 60 * 24 * 30;

    // Authorization codes are single-use and redeemed within the same
    // browser round-trip (seconds, not minutes, in the common case), so
    // they get a materially tighter ceiling than the other short-lived
    // tokens above — the realm-wide default is 5 minutes (see
    // OpenIddictSettings.AuthorizationCodeLifetimeMinutes). 1..10 minutes
    // gives admins headroom for slow consent/redirect chains without
    // letting a per-client override turn an auth code into a long-lived
    // bearer credential.
    private const int MinAuthorizationCodeSeconds = 60;
    private const int MaxAuthorizationCodeSeconds = 60 * 10;

    /// <summary>
    /// Validates and writes the OpenIddict-native <c>tkn_lft:*</c> keys for a
    /// standard client's Identity/Access/Authorization-Code/Sliding-Refresh
    /// token lifetimes (admin UI values are seconds) directly into
    /// <paramref name="settings"/>. PATCH semantics match the surrounding
    /// modgud:* keys: a <c>null</c> input leaves the corresponding key (and
    /// therefore any lifetime override already in <paramref name="settings"/>)
    /// untouched; a provided value is bounds-checked and, on success,
    /// overwrites the key. Validation runs for every provided value BEFORE
    /// any write, so a rejection never leaves <paramref name="settings"/>
    /// partially mutated. Returns the first bounds violation as a validation
    /// <see cref="Error"/>; <c>null</c> when every provided value is in range.
    /// </summary>
    internal static Error? ApplyNativeTokenLifetimes(
        Dictionary<string, string> settings,
        int? identityTokenLifetimeSeconds,
        int? accessTokenLifetimeSeconds,
        int? authorizationCodeLifetimeSeconds,
        int? slidingRefreshTokenLifetimeSeconds)
    {
        if (identityTokenLifetimeSeconds.HasValue &&
            ValidateShortLivedSeconds("IdentityTokenLifetime", identityTokenLifetimeSeconds.Value) is { } idErr)
            return idErr;
        if (accessTokenLifetimeSeconds.HasValue &&
            ValidateShortLivedSeconds("AccessTokenLifetime", accessTokenLifetimeSeconds.Value) is { } atErr)
            return atErr;
        if (authorizationCodeLifetimeSeconds.HasValue &&
            ValidateAuthorizationCodeSeconds(authorizationCodeLifetimeSeconds.Value) is { } acErr)
            return acErr;
        if (slidingRefreshTokenLifetimeSeconds.HasValue &&
            ValidateRefreshSeconds(slidingRefreshTokenLifetimeSeconds.Value) is { } rtErr)
            return rtErr;

        if (identityTokenLifetimeSeconds.HasValue)
            settings[OpenIddictIdentityTokenLifetimeSettingKey] = ToLifetimeString(identityTokenLifetimeSeconds.Value);
        if (accessTokenLifetimeSeconds.HasValue)
            settings[OpenIddictAccessTokenLifetimeSettingKey] = ToLifetimeString(accessTokenLifetimeSeconds.Value);
        if (authorizationCodeLifetimeSeconds.HasValue)
            settings[OpenIddictAuthorizationCodeLifetimeSettingKey] = ToLifetimeString(authorizationCodeLifetimeSeconds.Value);
        if (slidingRefreshTokenLifetimeSeconds.HasValue)
            settings[OpenIddictRefreshTokenLifetimeSettingKey] = ToLifetimeString(slidingRefreshTokenLifetimeSeconds.Value);

        return null;
    }

    private static Error? ValidateShortLivedSeconds(string field, int seconds) =>
        seconds < MinShortLivedTokenSeconds || seconds > MaxShortLivedTokenSeconds
            ? Error.Validation(
                $"OAuthClient.Invalid{field}",
                $"{field} must be between {MinShortLivedTokenSeconds} and {MaxShortLivedTokenSeconds} seconds.")
            : null;

    private static Error? ValidateAuthorizationCodeSeconds(int seconds) =>
        seconds < MinAuthorizationCodeSeconds || seconds > MaxAuthorizationCodeSeconds
            ? Error.Validation(
                "OAuthClient.InvalidAuthorizationCodeLifetime",
                $"AuthorizationCodeLifetime must be between {MinAuthorizationCodeSeconds} and {MaxAuthorizationCodeSeconds} seconds.")
            : null;

    private static Error? ValidateRefreshSeconds(int seconds) =>
        seconds < MinRefreshTokenSeconds || seconds > MaxRefreshTokenSeconds
            ? Error.Validation(
                "OAuthClient.InvalidSlidingRefreshTokenLifetime",
                $"SlidingRefreshTokenLifetime must be between {MinRefreshTokenSeconds} and {MaxRefreshTokenSeconds} seconds.")
            : null;

    private static string ToLifetimeString(int seconds) =>
        TimeSpan.FromSeconds(seconds).ToString("c", CultureInfo.InvariantCulture);

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
            roles: dto.Roles ?? GetStringListProp(current, OAuthApplicationPropertyKeys.Roles),
            requireDpop: dto.RequireDpop ?? GetBoolProp(current, OAuthApplicationPropertyKeys.RequireDpop, false),
            requireDpopNonce: dto.RequireDpopNonce ?? GetBoolProp(current, OAuthApplicationPropertyKeys.RequireDpopNonce, false));

    // ───────────────────────────────────────────── State → DTO ────────────────

    internal static OAuthClientDto MapClient(OAuthApplicationState s)
    {
        var props = s.Properties;
        var settings = s.Settings;

        var accessTokenType = AccessTokenType.Reference;
        if (settings.TryGetValue(OAuthApplicationSettingKeys.AccessTokenType, out var v1) &&
            Enum.TryParse<AccessTokenType>(v1, out var parsed1))
            accessTokenType = parsed1;

        int? GetIntSetting(string key) =>
            settings.TryGetValue(key, out var sv) && int.TryParse(sv, out var iv) ? iv : null;

        settings.TryGetValue(OAuthApplicationSettingKeys.ClientClaimsPrefix, out var prefix);
        settings.TryGetValue(OAuthApplicationSettingKeys.WebAuthnRpId, out var webAuthnRpId);

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
            AllowAccessTokensViaBrowser = GetBoolProp(props, OAuthApplicationPropertyKeys.AllowAccessTokensViaBrowser, false),
            RequireClientSecret = GetBoolProp(props, OAuthApplicationPropertyKeys.RequireClientSecret, true),
            EnableLocalLogin = GetBoolProp(props, OAuthApplicationPropertyKeys.EnableLocalLogin, true),
            RequireConsent = GetBoolProp(props, OAuthApplicationPropertyKeys.RequireConsent, false),
            AllowRememberConsent = GetBoolProp(props, OAuthApplicationPropertyKeys.AllowRememberConsent, true),
            RequirePushedAuthorizationRequests =
                s.Requirements.Contains(OAuthPermissions.Requirements.PushedAuthorizationRequests),
            RequireDpop = GetBoolProp(props, OAuthApplicationPropertyKeys.RequireDpop, false),
            RequireDpopNonce = GetBoolProp(props, OAuthApplicationPropertyKeys.RequireDpopNonce, false),
            AllowedGrantTypes = ExtractGrantTypes(s.Permissions),
            AllowedCorsOrigins = GetStringListProp(props, OAuthApplicationPropertyKeys.AllowedCorsOrigins),
            IdentityTokenLifetime = GetIntSetting(OAuthApplicationSettingKeys.IdentityTokenLifetime),
            AccessTokenLifetime = GetIntSetting(OAuthApplicationSettingKeys.AccessTokenLifetime),
            AuthorizationCodeLifetime = GetIntSetting(OAuthApplicationSettingKeys.AuthorizationCodeLifetime),
            SlidingRefreshTokenLifetime = GetIntSetting(OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime),
            AlwaysSendClientClaims = GetBoolProp(props, OAuthApplicationPropertyKeys.AlwaysSendClientClaims, false),
            UpdateAccessTokenClaimsOnRefresh = GetBoolProp(props, OAuthApplicationPropertyKeys.UpdateAccessTokenClaimsOnRefresh, false),
            ClientClaimsPrefix = prefix,
            WebAuthnRpId = webAuthnRpId,
            Claims = GetClaimsProp(props),
            Roles = GetStringListProp(props, OAuthApplicationPropertyKeys.Roles),
            AppIds = s.AppIds.Select(g => new ShortGuid(g).ToString()).ToList(),
            IsDynamicallyRegistered = GetBoolProp(props, OAuthApplicationPropertyKeys.DcrIsDynamicallyRegistered, false),
            DcrRegisteredAt = GetDateTimeOffsetProp(props, OAuthApplicationPropertyKeys.DcrRegisteredAt),
            DcrRegisteredFromIp = GetStringProp(props, OAuthApplicationPropertyKeys.DcrRegisteredFromIp),
            DcrLastUsedAt = GetDateTimeOffsetProp(props, OAuthApplicationPropertyKeys.DcrLastUsedAt),
            LinkedServiceAccountId = s.LinkedServiceAccountId is null
                ? null
                : new ShortGuid(s.LinkedServiceAccountId.Value).ToString(),
        };
    }

    internal static DateTimeOffset? GetDateTimeOffsetProp(IDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null) return null;
        return raw switch
        {
            // Newtonsoft.Json (Marten's default serializer) auto-parses
            // ISO-8601 strings to DateTime/DateTimeOffset on the dict roundtrip,
            // so the value comes back typed even though we wrote a string.
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt),
            string s => DateTimeOffset.TryParse(s, out var parsed) ? parsed : null,
            JsonElement e when e.ValueKind is JsonValueKind.String
                && DateTimeOffset.TryParse(e.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    internal static string? GetStringProp(IDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null) return null;
        return raw switch
        {
            string s => s,
            JsonElement e when e.ValueKind is JsonValueKind.String => e.GetString(),
            DateTimeOffset dto => dto.ToString("O"),
            DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt).ToString("O"),
            _ => null,
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
    /// Maps an <see cref="OAuthApiState"/> projection into the API DTO. A
    /// resource server has no credential surface of its own — RS-to-IdP
    /// authentication runs through OAuth (Client-Credentials with a linked
    /// ServiceAccount), so there is nothing secret-shaped on the response.
    /// </summary>
    internal static OAuthApiDto MapApiState(OAuthApiState s, bool hasImplicitScope = false)
        => new()
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
            HasImplicitScope = hasImplicitScope,
            AllowDynamicRegistration = GetBoolProp(s.Properties, OAuthApiPropertyKeys.AllowDynamicRegistration, false),
        };

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

    internal static bool VerifySecret(string secret, string hash)
    {
        try { return BCrypt.Net.BCrypt.Verify(secret, hash); }
        catch { return false; }
    }

    // ───────────────────────────────────────────── SA-link invariant ──────────
    //
    // Phase 2C — Service-Account-Credentials. Three rules, enforced at every
    // create/update path so the standard admin CRUD can never produce a
    // mixed-mode client:
    //
    //   R1  AllowedGrantTypes contains "client_credentials" ⇒ link required.
    //   R2  link set ⇒ AllowedGrantTypes contains "client_credentials".
    //   R3  link set ⇒ AllowedGrantTypes contains nothing else (no user-flow
    //       grants alongside client_credentials).
    //
    // The split between user-flow and M2M is structural, not a per-token
    // toggle — see the maintainers' 'service-account-credentials' design note
    // for the design.

    internal const string ClientCredentialsGrantType = "client_credentials";

    internal static readonly IReadOnlySet<string> UserFlowGrantTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "authorization_code",
        "implicit",
        "password",
        "refresh_token",
        "urn:ietf:params:oauth:grant-type:device_code",
        // ADR-0010 native grants authenticate a human, so they are user-flow:
        // a client_credentials (Service-Account) client must not also carry them.
        CocoarGrantTypes.Otp,
        CocoarGrantTypes.Magic,
        CocoarGrantTypes.Passkey,
    };

    /// <summary>
    /// Checks the SA-link invariant against the effective grant list + link
    /// after a create/update merge. Returns the first violation as an
    /// <see cref="Error"/>; null when the combination is valid.
    /// </summary>
    internal static Error? ValidateServiceAccountLinkInvariant(
        IReadOnlyList<string> effectiveGrants, Guid? linkedServiceAccountId)
    {
        var hasCc = effectiveGrants.Contains(ClientCredentialsGrantType, StringComparer.Ordinal);
        var hasUserFlow = effectiveGrants.Any(g => UserFlowGrantTypes.Contains(g));
        var hasLink = linkedServiceAccountId.HasValue;

        if (hasCc && !hasLink) return OAuthErrors.ClientCredentialsRequiresServiceAccountLink;
        if (hasLink && (!hasCc || hasUserFlow)) return OAuthErrors.ServiceAccountLinkRequiresClientCredentialsOnly;
        return null;
    }

    // ───────────────────────────────────────────── WebAuthn RP-ID (ADR-0009) ──

    /// <summary>
    /// Validates an admin-set per-client WebAuthn RP ID. Null/blank is valid
    /// (realm-scoped / clear). A set value must be a bare registrable hostname —
    /// no scheme, port, path, or whitespace. Per ADR-0009 the value is high-trust
    /// (admin-set, not client-supplied) so there is NO public-suffix-list check;
    /// this only rejects obvious malformity that would mint unverifiable credentials.
    /// </summary>
    internal static Error? ValidateWebAuthnRpId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.Contains('/') || v.Contains(':') || v.Any(char.IsWhiteSpace)
            || Uri.CheckHostName(v) != UriHostNameType.Dns)
            return OAuthErrors.InvalidWebAuthnRpId(value);
        return null;
    }
}
