using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Modgud.Domain.OAuth.Applications;
using OpenIddict.Abstractions;

namespace Modgud.Api.Cors;

/// <summary>
/// Resolves whether a browser <c>Origin</c> is registered on any OAuth client
/// in the current realm (the per-client "Allowed CORS Origins" field). Used by
/// <see cref="OAuthCorsMiddleware"/> to decide whether to emit CORS headers on
/// the browser-reachable OIDC endpoints (token / userinfo / revocation).
/// </summary>
public interface IClientCorsOriginProvider
{
    ValueTask<bool> IsOriginAllowedAsync(string origin, CancellationToken ct);
}

/// <summary>
/// Tenant-scoped implementation. The injected <see cref="IOpenIddictApplicationManager"/>
/// is already realm-scoped (the Marten store reads the active tenant from
/// <c>HttpContext</c>), so <c>ListAsync</c> only ever returns the current realm's
/// clients. The collected origin set is cached per-tenant for a short window so a
/// CORS preflight is O(1) and does not hit Postgres on every request — an admin
/// adding an origin sees it take effect within <see cref="CacheTtl"/>.
/// </summary>
public sealed class ClientCorsOriginProvider : IClientCorsOriginProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IOpenIddictApplicationManager _applications;
    private readonly IMemoryCache _cache;
    private readonly IHttpContextAccessor _http;

    public ClientCorsOriginProvider(
        IOpenIddictApplicationManager applications,
        IMemoryCache cache,
        IHttpContextAccessor http)
    {
        _applications = applications;
        _cache = cache;
        _http = http;
    }

    public async ValueTask<bool> IsOriginAllowedAsync(string origin, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;

        // Cache key is per-tenant so realm A's origins never authorise realm B.
        // The manager itself is already tenant-scoped via the Marten store.
        var tenantId = _http.HttpContext?.Items["TenantId"] as string ?? "system";
        var allowed = await GetOriginsAsync(tenantId, ct);
        return allowed.Contains(origin.TrimEnd('/'));
    }

    private async ValueTask<HashSet<string>> GetOriginsAsync(string tenantId, CancellationToken ct)
    {
        var cacheKey = $"cors-origins::{tenantId}";
        if (_cache.TryGetValue(cacheKey, out HashSet<string>? cached) && cached is not null)
            return cached;

        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var application in _applications.ListAsync(count: null, offset: null, ct))
        {
            var properties = await _applications.GetPropertiesAsync(application, ct);
            if (!properties.TryGetValue(OAuthApplicationPropertyKeys.AllowedCorsOrigins, out var element) ||
                element.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in element.EnumerateArray())
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    origins.Add(value.TrimEnd('/'));
            }
        }

        _cache.Set(cacheKey, origins, CacheTtl);
        return origins;
    }
}
