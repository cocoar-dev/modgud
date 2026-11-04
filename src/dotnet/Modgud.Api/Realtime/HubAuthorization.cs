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
/// caller's permission imperatively — at subscribe time and before every item (B9-RT01). This is the shared,
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
    /// Gates <paramref name="source"/> behind <paramref name="permission"/> (within
    /// <see cref="AppSlugs.Modgud"/>) in the caller's realm. Unauthorized at subscribe
    /// time: the stream completes empty. Authorized: items are forwarded in order and
    /// torn down on unsubscribe — but the permission is checked again before EACH item
    /// (B9-RT01): a caller whose permission was withdrawn while the browser session and
    /// the connection stay alive receives no further payload, and the stream completes.
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

            // Captured now: the per-item checks run long after this subscribe call, so
            // they use the root scope factory (never a request scope that may be gone by
            // then) and the user id the connection authenticated as.
            var scopes = http!.RequestServices.GetRequiredService<IServiceScopeFactory>();
            var userId = http.GetUserId()!.Value;

            // Concat keeps the source order; each item waits for its own check.
            return source
                .Select(item => Observable.FromAsync(async () =>
                    (Item: item, Allowed: await StillAuthorizedAsync(scopes, userId, realm!, permission))))
                .Concat()
                .TakeWhile(checkedItem => checkedItem.Allowed)
                .Select(checkedItem => checkedItem.Item)
                .Subscribe(observer);
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

    /// <summary>
    /// Gate for high-rate feeds that are pushed from synchronous event handlers (the
    /// observability activity and error feeds). <paramref name="attach"/> attaches a handler
    /// and returns the detach action; it is wired only once the caller is authorised and torn
    /// down when the client unsubscribes.
    /// <para>gvm6 — a check per item (as <see cref="AuthorizedRealmStream{T}"/> does) would cost
    /// a permission lookup per event on these feeds, so the permission is re-checked every
    /// <paramref name="recheckInterval"/> instead. Once it is gone (or the lookup throws), no
    /// further item is forwarded, the handler is detached and the feed completes. Items pushed
    /// between the withdrawal and the next check are still delivered.</para>
    /// </summary>
    public static IObservable<T> AuthorizedRealmFeed<T>(
        HttpContext? http, string? realm, string permission, TimeSpan recheckInterval, Func<Action<T>, Action> attach)
    {
        return Observable.Create<T>(async (observer, ct) =>
        {
            if (!await IsAuthorizedAsync(http, realm, permission))
            {
                observer.OnCompleted();
                return;
            }

            // Captured now, like the per-item gate: the re-checks run long after the
            // subscribing request, so they must not depend on its scope.
            var scopes = http!.RequestServices.GetRequiredService<IServiceScopeFactory>();
            var userId = http.GetUserId()!.Value;

            var gate = new object();
            var open = true;
            void OnNext(T item)
            {
                lock (gate)
                {
                    if (!open) return;
                    try { observer.OnNext(item); }
                    catch { /* observer disposed mid-flight — handled on cancel */ }
                }
            }

            var detach = attach(OnNext);
            try
            {
                while (true)
                {
                    await Task.Delay(recheckInterval, ct);
                    if (await StillAuthorizedAsync(scopes, userId, realm!, permission)) continue;

                    lock (gate) open = false;
                    detach();
                    detach = static () => { };
                    observer.OnCompleted();
                    return;
                }
            }
            catch (OperationCanceledException) { /* expected on teardown */ }
            finally
            {
                lock (gate) open = false;
                detach();
            }
        });
    }

    /// <summary>The per-item re-check of <see cref="AuthorizedRealmStream{T}"/>. Fails
    /// closed: a lookup that throws ends the stream instead of forwarding the item.</summary>
    private static async Task<bool> StillAuthorizedAsync(
        IServiceScopeFactory scopes, Guid userId, string realm, string permission)
    {
        try
        {
            using var _ = TenantContext.Enter(realm);
            await using var scope = scopes.CreateAsyncScope();
            var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();
            return await permissions.HasPermissionAsync(userId, AppSlugs.Modgud, permission);
        }
        catch
        {
            return false;
        }
    }
}
