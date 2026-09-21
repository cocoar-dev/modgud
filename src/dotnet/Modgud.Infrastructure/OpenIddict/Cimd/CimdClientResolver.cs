using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Modgud.Application.Dcr;
using Modgud.Domain.Applications;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using Microsoft.AspNetCore;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;

namespace Modgud.Infrastructure.OpenIddict.Cimd;

/// <summary>
/// Resolves a CIMD <c>client_id</c> URL into a synthesized,
/// NON-persisted <see cref="OAuthApplicationState"/> the OpenIddict pipeline
/// can treat as a registered public client — fetching, SSRF-guarding,
/// validating, and caching the client's metadata document on demand.
///
/// <para>This is the single CIMD-aware seam: <see cref="MartenApplicationStore"/>
/// calls it from <c>FindByClientIdAsync</c>, and the token-pipeline handlers
/// that resolve a client by direct Marten query (which would miss a
/// non-persisted client) fall back to it. The process-wide
/// <see cref="IMemoryCache"/> means the store's first resolve in a request
/// warms the cache for every later handler call in the same request.</para>
///
/// <para>A client is public (<c>token_endpoint_auth_method=none</c> + PKCE) or,
/// with <c>private_key_jwt</c>, confidential against the public keys it
/// publishes (<see cref="GetJsonWebKeySetAsync"/>); either way PKCE applies,
/// the synthesized client gets JWT access tokens and is marked
/// <c>DcrIsDynamicallyRegistered</c> so the existing DCR audience-containment
/// + "unverified" consent treatment apply unchanged.</para>
/// </summary>
public sealed class CimdClientResolver
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client whose primary
    /// handler carries the SSRF guard
    /// (<see cref="Modgud.Infrastructure.Http.SsrfSafeHttpHandlerFactory"/>).</summary>
    public const string HttpClientName = "Modgud.Cimd.MetadataFetcher";

    private const int MaxBodyBytes = 5 * 1024;
    private const int MaxJwksBytes = 64 * 1024;
    private static readonly TimeSpan JwksRefetchCooldown = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromHours(24);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ITenantSessionFactory _sessionFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<CimdClientResolver> _logger;

    // Inject the session FACTORY, not IDocumentSession: resolving an
    // IDocumentSession from DI eagerly opens a tenant-scoped Marten session in
    // this resolver's constructor, which throws "Unknown tenant id" on a realm
    // with no physical DB — even for requests that never resolve a CIMD client.
    // The factory opens a session lazily, only inside ResolveAsync after the
    // IsCimdClientId guard, mirroring MartenApplicationStore.
    public CimdClientResolver(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ITenantSessionFactory sessionFactory,
        IHttpContextAccessor httpContextAccessor,
        ILogger<CimdClientResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _sessionFactory = sessionFactory;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Returns a synthesized application for a CIMD <c>client_id</c> URL, or
    /// <c>null</c> when the identifier isn't a CIMD URL, the realm hasn't
    /// opted in, or the metadata document is unreachable/invalid. Never
    /// throws for a bad document — a failed resolve reads as "unknown
    /// client" upstream (safe default).
    /// </summary>
    public async Task<OAuthApplicationState?> ResolveAsync(string? clientId, CancellationToken cancellationToken)
    {
        if (!CimdClientId.IsCimdClientId(clientId)) return null;

        // Audit #27 — read the realm opt-in on EVERY resolve, BEFORE the cache
        // lookup. Previously Enabled was only checked on the cache-miss path, so an
        // already-cached client_id kept resolving (and minting tokens) for up to the
        // cache TTL (24h) after an admin set Cimd.Enabled=false — a stale security
        // decision unbounded by the admin action. A cheap tenant-singleton LoadAsync
        // makes the opt-in live while the cache still memoizes the expensive metadata
        // fetch. The query session is opened lazily here (never in the constructor)
        // so a non-CIMD request on a realm without a physical DB never touches it.
        // ADR-0011 — effective (App ⊕ realm) CIMD opt-in. Host-time: if the
        // authorize/token request arrived on an Application subdomain, the App's
        // CIMD override applies; else the realm setting. Loaded via the lazy
        // session (NOT the resolver) to preserve this resolver's no-eager-tenant-
        // session design (it must not throw on a realm without a physical DB).
        CimdSettings? settings;
        IReadOnlyList<string> requestableScopes;
        await using (var session = _sessionFactory.OpenQuerySession())
        {
            var realmCimd = (await session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId, cancellationToken))?.Cimd;
            var appId = _httpContextAccessor.HttpContext?.GetApplicationId();
            if (appId is { } id
                && (await session.LoadAsync<ApplicationSettings>(id, cancellationToken))?.Cimd is { } appCimd)
            {
                var baseCimd = realmCimd ?? new CimdSettings();
                settings = baseCimd with
                {
                    Enabled = appCimd.Enabled ?? baseCimd.Enabled,
                    AccessTokenLifetime = appCimd.AccessTokenLifetime ?? baseCimd.AccessTokenLifetime,
                    RefreshTokenLifetime = appCimd.RefreshTokenLifetime ?? baseCimd.RefreshTokenLifetime,
                };
            }
            else
            {
                settings = realmCimd;
            }
            if (settings is null || !settings.Enabled) return null;

            // The scope permissions are NOT part of the cached document: they are
            // the realm's live opt-in set (DynamicClientScopePolicy) intersected with
            // whatever the document declares, recomputed on every resolve so a scope
            // opted in or out by an admin takes effect at once — same liveness rule
            // as the Enabled check above. The document itself stays memoized.
            requestableScopes = await LoadRequestableScopesAsync(session, cancellationToken);
        }

        var cacheKey = $"cimd:doc:{TenantContext.Current}:{clientId}";
        if (_cache.TryGetValue<CachedCimd>(cacheKey, out var cached) && cached is not null)
            return Synthesize(cached, requestableScopes);

        if (!CimdClientId.TryValidateUrl(clientId, out var uri, out var urlError) || uri is null)
        {
            _logger.LogWarning("CIMD client_id rejected before fetch: {Reason} ({ClientId})", urlError, clientId);
            return null;
        }

        var (metadata, ttl) = await FetchAndValidateAsync(uri, clientId!, cancellationToken);
        if (metadata is null) return null;

        var entry = new CachedCimd(metadata, settings.AccessTokenLifetime, settings.RefreshTokenLifetime);
        if (ttl > TimeSpan.Zero)
            _cache.Set(cacheKey, entry, ttl);

        return Synthesize(entry, requestableScopes);
    }

    private const string RequestableScopesItemKey = "Modgud.Cimd.RequestableScopes";

    /// <summary>
    /// One realm-wide scope query per request, not per resolve: the store, the
    /// audience-containment handler and the authorize endpoint each resolve the
    /// same client once per request, so the set is memoized on HttpContext.Items
    /// (a background resolve without a request context simply queries).
    /// </summary>
    private async Task<IReadOnlyList<string>> LoadRequestableScopesAsync(IQuerySession session, CancellationToken cancellationToken)
    {
        var items = _httpContextAccessor.HttpContext?.Items;
        if (items is not null && items.TryGetValue(RequestableScopesItemKey, out var memo) && memo is IReadOnlyList<string> known)
            return known;

        var loaded = await DynamicClientScopePolicy.LoadRequestableNamesAsync(session, cancellationToken);
        if (items is not null) items[RequestableScopesItemKey] = loaded;
        return loaded;
    }

    private async Task<(CimdMetadata? Metadata, TimeSpan Ttl)> FetchAndValidateAsync(
        Uri uri, string requestedClientId, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CIMD fetch for {ClientId} returned {Status}", requestedClientId, (int)response.StatusCode);
                return (null, default);
            }

            if (response.Content.Headers.ContentLength is > MaxBodyBytes)
            {
                _logger.LogWarning("CIMD document for {ClientId} exceeds {Max} bytes (Content-Length)", requestedClientId, MaxBodyBytes);
                return (null, default);
            }

            var json = await ReadBoundedAsync(response.Content, MaxBodyBytes, cancellationToken);
            if (json is null)
            {
                _logger.LogWarning("CIMD document for {ClientId} exceeds {Max} bytes", requestedClientId, MaxBodyBytes);
                return (null, default);
            }

            var result = CimdMetadataParser.Parse(json, requestedClientId);
            if (result is CimdValidationResult.Invalid invalid)
            {
                _logger.LogWarning("CIMD document for {ClientId} rejected: {Reason}", requestedClientId, invalid.Reason);
                return (null, default);
            }

            var metadata = ((CimdValidationResult.Valid)result).Metadata;
            return (metadata, ResolveTtl(response.Headers.CacheControl));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller aborted — propagate, don't swallow as a fetch failure
        }
        catch (Exception ex)
        {
            // Network error, timeout, SSRF block (IOException from the connect
            // callback), TLS failure, … — all non-fatal; the client just
            // doesn't resolve.
            _logger.LogWarning(ex, "CIMD fetch failed for {ClientId}", requestedClientId);
            return (null, default);
        }
    }

    private static async Task<string?> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[maxBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length &&
               (read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)) > 0)
        {
            total += read;
        }
        // We deliberately read one byte past the cap: if we filled the whole
        // buffer the body is at least maxBytes+1 → over the limit.
        if (total > maxBytes) return null;
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static TimeSpan ResolveTtl(CacheControlHeaderValue? cacheControl)
    {
        if (cacheControl is not null && (cacheControl.NoStore || cacheControl.NoCache))
            return TimeSpan.Zero; // honour no-store/no-cache: resolve fresh every time

        var ttl = cacheControl?.MaxAge ?? DefaultTtl;
        if (ttl < MinTtl) ttl = MinTtl;
        if (ttl > MaxTtl) ttl = MaxTtl;
        return ttl;
    }

    private static OAuthApplicationState Synthesize(CachedCimd entry, IReadOnlyList<string> requestableScopes)
    {
        var meta = entry.Metadata;
        return new OAuthApplicationState
        {
            // Deterministic Id = hash of the client_id URL → every resolve of
            // the same URL yields the same ApplicationId, so authorizations
            // and tokens minted for it share a consistent owner without any
            // DB write.
            Id = DeterministicId(meta.ClientId),
            ClientId = meta.ClientId,
            DisplayName = DisplayNameFor(meta),
            // private_key_jwt makes it confidential: OpenIddict then demands the
            // signed assertion at the token endpoint and validates it against the
            // key set GetJsonWebKeySetAsync hands the store.
            ClientType = meta.TokenEndpointAuthMethod == "private_key_jwt"
                ? OAuthClientTypes.Confidential
                : OAuthClientTypes.Public,
            ConsentType = OAuthConsentTypes.Explicit,
            // What the document declares, else what its redirect URIs imply: a loopback
            // http URI makes the client native, which is what lets OpenIddict accept the
            // ephemeral port every local MCP client shows up with (RFC 8252 §7.3). This
            // used to be hard-wired to "web", which refused all of them.
            ApplicationType = OAuthApplicationTypes.Effective(meta.ApplicationType, meta.RedirectUris)
                              ?? OAuthApplicationTypes.Web,
            RedirectUris = meta.RedirectUris.ToList(),
            PostLogoutRedirectUris = new List<string>(),
            Permissions = BuildPermissions(meta, requestableScopes),
            Requirements = new List<string>(), // global RequireProofKeyForCodeExchange enforces PKCE
            Settings = new Dictionary<string, string>
            {
                // OpenIddict-native per-application lifetime keys — these are
                // what the token pipeline actually enforces (the modgud:* keys
                // are only for admin display of persisted clients, which a CIMD
                // client never is). The shorter lifetimes cap the blast radius
                // of a leaked token from an unverified, domain-bound client.
                [OpenIddictConstants.Settings.TokenLifetimes.AccessToken] =
                    entry.AccessTokenLifetime.ToString("c", CultureInfo.InvariantCulture),
                [OpenIddictConstants.Settings.TokenLifetimes.RefreshToken] =
                    entry.RefreshTokenLifetime.ToString("c", CultureInfo.InvariantCulture),
            },
            Properties = new Dictionary<string, object?>(KeySourceProperties(meta))
            {
                [OAuthApplicationPropertyKeys.Enabled] = JsonSerializer.SerializeToElement(true),
                [OAuthApplicationPropertyKeys.DcrIsDynamicallyRegistered] = JsonSerializer.SerializeToElement(true),
                [OAuthApplicationPropertyKeys.CimdIsResolvedClient] = JsonSerializer.SerializeToElement(true),
                // Same rule as DCR: an unverified public client never skips
                // the consent screen on a remembered authorization.
                [OAuthApplicationPropertyKeys.AllowRememberConsent] = JsonSerializer.SerializeToElement(false),
            },
            AccessTokenType = AccessTokenType.Jwt,
            AppIds = new List<Guid>(),
        };
    }

    private static IEnumerable<KeyValuePair<string, object?>> KeySourceProperties(CimdMetadata meta)
    {
        if (meta.JwksUri is not null)
            yield return new(OAuthApplicationPropertyKeys.CimdJwksUri, JsonSerializer.SerializeToElement(meta.JwksUri));
        if (meta.Jwks is not null)
            yield return new(OAuthApplicationPropertyKeys.CimdJwks, JsonSerializer.SerializeToElement(meta.Jwks));
    }

    // ─── private_key_jwt key set ─────────────────────────────────────────

    /// <summary>
    /// The public key set a <c>private_key_jwt</c> CIMD client authenticates
    /// with — the store's <c>GetJsonWebKeySetAsync</c> lands here because a
    /// synthesized client has no security record in the database. Inline sets
    /// come straight off the client; a <c>jwks_uri</c> is fetched through the
    /// same SSRF-guarded client as the document, cached per its Cache-Control
    /// (5 min – 24 h), and fetched again when an assertion names a <c>kid</c> the
    /// cached set lacks — the client has rotated — at most once a minute.
    /// Errors are never cached; a set that cannot be had reads as no keys, and
    /// OpenIddict refuses the assertion (<c>invalid_client</c>).
    /// </summary>
    public async Task<JsonWebKeySet?> GetJsonWebKeySetAsync(OAuthApplicationState application, CancellationToken cancellationToken)
    {
        if (ReadStringProperty(application, OAuthApplicationPropertyKeys.CimdJwks) is { } inline)
            return JsonWebKeySet.Create(inline);
        if (ReadStringProperty(application, OAuthApplicationPropertyKeys.CimdJwksUri) is not { } jwksUri
            || !Uri.TryCreate(jwksUri, UriKind.Absolute, out var uri))
            return null;

        var cacheKey = $"cimd:jwks:{jwksUri}";
        DateTimeOffset? kidRefetchAt = null;
        if (_cache.TryGetValue<CachedJwks>(cacheKey, out var cached) && cached is not null)
        {
            // A kid the cached set lacks means the client rotated: fetch again at
            // once, but at most once a minute, so a stream of made-up kids cannot
            // turn Modgud into a load generator against the client's host.
            var kid = AssertionKeyId();
            var rotated = kid is not null && !cached.Set.Keys.Any(k => k.Kid == kid);
            var coolingDown = cached.KidRefetchAt is { } last && DateTimeOffset.UtcNow - last < JwksRefetchCooldown;
            if (!rotated || coolingDown)
                return cached.Set;
            kidRefetchAt = DateTimeOffset.UtcNow;
        }

        var (set, ttl) = await FetchJwksAsync(uri, cancellationToken);
        if (set is null) return cached?.Set;
        if (ttl > TimeSpan.Zero)
            _cache.Set(cacheKey, new CachedJwks(set, kidRefetchAt), ttl);
        return set;
    }

    private async Task<(JsonWebKeySet? Set, TimeSpan Ttl)> FetchJwksAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CIMD jwks_uri {JwksUri} returned {Status}", uri, (int)response.StatusCode);
                return (null, default);
            }

            var json = response.Content.Headers.ContentLength is > MaxJwksBytes
                ? null
                : await ReadBoundedAsync(response.Content, MaxJwksBytes, cancellationToken);
            if (json is null)
            {
                _logger.LogWarning("CIMD jwks_uri {JwksUri} exceeds {Max} bytes", uri, MaxJwksBytes);
                return (null, default);
            }

            if (!CimdJwks.TryFilter(json, out var filtered, out var error))
            {
                _logger.LogWarning("CIMD jwks_uri {JwksUri} rejected: {Reason}", uri, error);
                return (null, default);
            }

            return (JsonWebKeySet.Create(filtered!), ResolveTtl(response.Headers.CacheControl));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CIMD jwks_uri fetch failed for {JwksUri}", uri);
            return (null, default);
        }
    }

    /// <summary>The <c>kid</c> in the header of the client assertion on the current
    /// token request, if any — read without validation, only to notice rotation.</summary>
    private string? AssertionKeyId()
    {
        var assertion = _httpContextAccessor.HttpContext?.GetOpenIddictServerRequest()?.ClientAssertion;
        if (string.IsNullOrEmpty(assertion)) return null;
        try
        {
            return new JsonWebToken(assertion).Kid;
        }
        catch (ArgumentException)
        {
            return null; // not a JWT — OpenIddict will refuse it on its own
        }
    }

    private static string? ReadStringProperty(OAuthApplicationState application, string key) =>
        application.Properties.TryGetValue(key, out var value) && value is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString()
            : null;

    private sealed record CachedJwks(JsonWebKeySet Set, DateTimeOffset? KidRefetchAt);

    private static List<string> BuildPermissions(CimdMetadata meta, IReadOnlyList<string> requestableScopes)
    {
        var permissions = new List<string>
        {
            OAuthPermissions.Endpoints.Authorization,
            OAuthPermissions.Endpoints.Token,
            OAuthPermissions.Endpoints.EndSession,
            OAuthPermissions.Endpoints.Introspection,
            OAuthPermissions.Endpoints.Revocation,
        };

        foreach (var grant in meta.GrantTypes)
        {
            var permission = grant switch
            {
                "authorization_code" => OAuthPermissions.GrantTypes.AuthorizationCode,
                "refresh_token" => OAuthPermissions.GrantTypes.RefreshToken,
                _ => null,
            };
            if (permission is not null) permissions.Add(permission);
        }

        // authorization_code is guaranteed present (the parser requires it).
        permissions.Add(OAuthPermissions.ResponseTypes.Code);

        // The document's `scope` is an upper bound, not a grant; a document without
        // one (every static MCP-client document — it cannot know one server's
        // scopes) holds the realm's whole dynamic-client set. Granting only the
        // declared scopes used to leave such a client with no scope permission at
        // all, so OpenIddict refused everything but openid/offline_access (ID2051)
        // before the per-scope opt-in was ever consulted.
        foreach (var scope in DynamicClientScopePolicy.Resolve(meta.Scopes, requestableScopes))
            permissions.Add(OAuthPermissions.Prefixes.Scope + scope);

        return permissions;
    }

    private static string DisplayNameFor(CimdMetadata meta)
    {
        if (!string.IsNullOrWhiteSpace(meta.ClientName)) return meta.ClientName!;
        return Uri.TryCreate(meta.ClientId, UriKind.Absolute, out var uri) ? uri.Host : meta.ClientId;
    }

    private static Guid DeterministicId(string clientId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(clientId));
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed record CachedCimd(CimdMetadata Metadata, TimeSpan AccessTokenLifetime, TimeSpan RefreshTokenLifetime);
}
