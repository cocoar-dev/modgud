using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Cocoar.Auth.Client.AspNetCore.Distribution;

/// <summary>
/// Memoises distribution-API responses per (user, token, app) for the
/// duration configured in <see cref="CocoarAuthOptions.CacheDuration"/>.
/// Cache-key derivation matches the IdP's intent: when the access token
/// rotates (new <c>jti</c>), the cached entry is bypassed; when permissions
/// are revoked but the token is still valid, the staleness window is the
/// cache duration (default 30s).
///
/// <para>Public so the claims-transformation in this package can take it
/// as a constructor dependency. Resource-server consumers don't usually
/// need to interact with it directly.</para>
/// </summary>
public sealed class PermissionsCache
{
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;

    public PermissionsCache(IMemoryCache cache, IOptions<CocoarAuthOptions> options)
    {
        _cache = cache;
        _ttl = options.Value.CacheDuration;
    }

    public Task<MePermissionsResponse> GetOrFetchAsync(
        string sub,
        string jti,
        string appSlug,
        Func<CancellationToken, Task<MePermissionsResponse>> fetch,
        CancellationToken ct = default)
    {
        var key = BuildKey(sub, jti, appSlug);
        return _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _ttl;
            return await fetch(ct);
        })!;
    }

    private static string BuildKey(string sub, string jti, string appSlug) =>
        $"cocoar-auth.me-permissions::{sub}::{jti}::{appSlug}";
}
