using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Modgud.Domain.Applications;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
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
/// <para>v1 is public-only (<c>token_endpoint_auth_method=none</c> + PKCE);
/// the synthesized client gets JWT access tokens and is marked
/// <c>DcrIsDynamicallyRegistered</c> so the existing DCR audience-containment
/// + "unverified" consent treatment apply unchanged.</para>
/// </summary>
public sealed class CimdClientResolver
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client whose primary
    /// handler carries the SSRF guard (<see cref="CimdHttpMessageHandlerFactory"/>).</summary>
    public const string HttpClientName = "Modgud.Cimd.MetadataFetcher";

    private const int MaxBodyBytes = 5 * 1024;
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
        }
        if (settings is null || !settings.Enabled) return null;

        var cacheKey = $"cimd:doc:{TenantContext.Current}:{clientId}";
        if (_cache.TryGetValue<CachedCimd>(cacheKey, out var cached) && cached is not null)
            return Synthesize(cached);

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

        return Synthesize(entry);
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

            var json = await ReadBoundedAsync(response.Content, cancellationToken);
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

    private static async Task<string?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length &&
               (read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)) > 0)
        {
            total += read;
        }
        // We deliberately read one byte past the cap: if we filled the whole
        // buffer the body is at least MaxBodyBytes+1 → over the limit.
        if (total > MaxBodyBytes) return null;
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

    private static OAuthApplicationState Synthesize(CachedCimd entry)
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
            ClientType = OAuthClientTypes.Public,
            ConsentType = OAuthConsentTypes.Explicit,
            ApplicationType = OAuthApplicationTypes.Web,
            RedirectUris = meta.RedirectUris.ToList(),
            PostLogoutRedirectUris = new List<string>(),
            Permissions = BuildPermissions(meta),
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
            Properties = new Dictionary<string, object?>
            {
                [OAuthApplicationPropertyKeys.Enabled] = JsonSerializer.SerializeToElement(true),
                [OAuthApplicationPropertyKeys.DcrIsDynamicallyRegistered] = JsonSerializer.SerializeToElement(true),
                [OAuthApplicationPropertyKeys.CimdIsResolvedClient] = JsonSerializer.SerializeToElement(true),
            },
            AccessTokenType = AccessTokenType.Jwt,
            AppIds = new List<Guid>(),
        };
    }

    private static List<string> BuildPermissions(CimdMetadata meta)
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

        foreach (var scope in meta.Scopes)
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
