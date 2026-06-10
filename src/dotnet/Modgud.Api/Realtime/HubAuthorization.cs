using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Services;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Realtime;

/// <summary>
/// Per-method authorization for SignalARR hub stream methods.
///
/// <para>SignalARR shares the cookie-auth pipeline and <c>UIHub</c>'s class-level
/// <c>[Authorize]</c> enforces authentication — but there is no per-method
/// authorization attribute, so a hub that streams privileged data must check the
/// caller's permission imperatively at subscribe time. This is the shared,
/// documented default for every entity stream (audit H2): the previous revision
/// only gated <c>ObservabilityHub</c>, leaving <c>UserHub</c> /
/// <c>ServiceAccountHub</c> streaming PII to any authenticated realm user.</para>
///
/// <para><b>Realm binding:</b> the caller's realm comes from the connection
/// <c>HttpContext.Items</c> (set at connect by <c>RealmMiddleware</c>), NOT from
/// <see cref="TenantContext.Current"/> — the ambient tenant AsyncLocal is unset
/// during hub dispatch. The permission lookup re-enters that realm explicitly so
/// the tenant-scoped <see cref="IPermissionService"/> resolves against the right
/// database. Fails closed: unauthenticated / unresolved-realm / missing-permission
/// all yield an immediately-completed empty stream.</para>
/// </summary>
public static class HubAuthorization
{
    /// <summary>
    /// The caller's realm, read from the connection context. Null when no tenant
    /// was resolved — callers fail closed.
    /// </summary>
    public static string? CallerRealm(HttpContext? http)
        => http?.Items[TenantConstants.HttpContextTenantIdKey] as string;

    /// <summary>
    /// Gates <paramref name="source"/> behind a one-shot <paramref name="permission"/>
    /// check (within <see cref="AppSlugs.Modgud"/>) in the caller's realm. When
    /// authorized the source is forwarded verbatim and torn down on unsubscribe;
    /// otherwise the stream completes empty. The check runs once at subscribe
    /// time — standard for long-lived push channels.
    /// </summary>
    public static IObservable<T> AuthorizedRealmStream<T>(
        HttpContext? http, string? realm, string permission, IObservable<T> source)
    {
        return Observable.Create<T>(async (observer, _) =>
        {
            if (!await IsAuthorizedAsync(http, realm, permission))
            {
                observer.OnCompleted();
                return Disposable.Empty;
            }

            return source.Subscribe(observer);
        });
    }

    /// <summary>
    /// True if the authenticated caller holds <paramref name="permission"/> in
    /// <paramref name="realm"/>. Opens a fresh DI scope bound to the realm so the
    /// lookup never reuses a connection-scoped session pinned to another tenant.
    /// </summary>
    public static async Task<bool> IsAuthorizedAsync(HttpContext? http, string? realm, string permission)
    {
        if (http is null || string.IsNullOrEmpty(realm)) return false;

        var userId = http.GetUserId();
        if (userId is null) return false;

        using var _ = TenantContext.Enter(realm);
        await using var scope = http.RequestServices.CreateAsyncScope();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();
        return await permissions.HasPermissionAsync(userId.Value, AppSlugs.Modgud, permission);
    }
}
