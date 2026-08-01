using System.Collections.Concurrent;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Hosting;
using Modgud.Authentication.RealmSettings;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Middleware;

/// <summary>
/// Bridges the per-realm auth rate-limit config into the ASP.NET rate limiter.
///
/// <para>The limiter's policy factories (<c>AddPolicy(...)</c> in <c>Program.cs</c>)
/// run synchronously and cannot resolve the realm's configured ceilings (an async
/// Marten lookup). This middleware does that lookup once per request — after
/// <c>RealmMiddleware</c> has resolved the tenant and before <c>UseRateLimiter</c> —
/// and stashes the realm's <see cref="AuthRateLimitSettings"/> on
/// <see cref="HttpContext.Items"/> under <see cref="ItemsKey"/>, where the factories
/// read it. Absent (e.g. an endpoint that doesn't rate-limit, or a resolution
/// failure) ⇒ the factory falls back to the shipped <see cref="AuthRateLimitDefaults"/>.</para>
///
/// <para>It only does the lookup for endpoints that actually opt into a limiter
/// policy (<see cref="EnableRateLimitingAttribute"/> metadata), and caches the
/// per-realm result for a few seconds so a flood against an anonymous auth endpoint
/// doesn't turn each would-be-throttled request into a fresh DB hit — keeping the
/// limiter's cheap-rejection property. Config edits take effect within the TTL.</para>
/// </summary>
public sealed class AuthRateLimitResolutionMiddleware(RequestDelegate next, IHostEnvironment env)
{
    public const string ItemsKey = "Modgud.AuthRateLimits";

    // No cache in Testing — each request reloads so a PATCH to the realm's limits
    // takes effect immediately and rate-limit tests are deterministic. In every
    // other environment a short TTL keeps the limiter's cheap-rejection property
    // under flood; config edits take effect within the window.
    private readonly TimeSpan _cacheTtl =
        env.IsEnvironment("Testing") ? TimeSpan.Zero : TimeSpan.FromSeconds(10);

    // realm slug → (expiry, settings). One instance for the app lifetime, so the
    // cache is process-wide. A null Value is a legitimately-cached "no overrides".
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    private readonly record struct CacheEntry(DateTimeOffset Expires, AuthRateLimitSettings? Value);

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>() is not null)
        {
            var slug = context.Items[TenantConstants.HttpContextTenantIdKey] as string;
            if (string.IsNullOrEmpty(slug))
            {
                // Installation/health routes have no realm. The limiter uses
                // its shipped defaults without touching tenant settings.
                await next(context);
                return;
            }

            if (!_cache.TryGetValue(slug, out var entry) || entry.Expires <= DateTimeOffset.UtcNow)
            {
                try
                {
                    var realmSettings = context.RequestServices.GetRequiredService<IRealmSettingsService>();
                    var doc = await realmSettings.LoadAsync(context.RequestAborted);
                    entry = new CacheEntry(DateTimeOffset.UtcNow + _cacheTtl, doc.AuthRateLimits);
                    if (_cacheTtl > TimeSpan.Zero) _cache[slug] = entry;
                }
                catch
                {
                    // Tenant not resolved / DB hiccup: leave Items unset so the
                    // policy factory uses the shipped defaults. Never block the
                    // request on a settings-resolution failure.
                    entry = default;
                }
            }

            if (entry.Expires != default)
                context.Items[ItemsKey] = entry.Value;
        }

        await next(context);
    }
}
