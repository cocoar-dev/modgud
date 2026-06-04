using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cocoar.SignalARRR.Common.Attributes;
using Cocoar.SignalARRR.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Services;
using Modgud.Infrastructure.Observability;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Features.Admin;

/// <summary>
/// SignalARR hub that streams live observability data to subscribed admin
/// clients. Two streams: <see cref="Subscribe"/> (metered activity events) and
/// <see cref="LogsSubscribe"/> (the Phase-5 per-realm live error feed). Both
/// pair with the REST <c>/api/admin/observability/*</c> endpoints — REST
/// delivers the initial rolling-window snapshot, the streams push every new
/// item as it happens.
///
/// <para><b>Realm filtering:</b> the caller's realm is read from the connection
/// <c>HttpContext.Items</c> (set at connect by <c>RealmMiddleware</c>), NOT from
/// <see cref="TenantContext.Current"/> — the ambient tenant AsyncLocal is unset
/// during SignalARR hub dispatch (it unwinds when the negotiate request ends),
/// so it would fall back to <c>system</c>. This mirrors the sibling hubs
/// (UserHub / InboxHub / ServiceAccountHub). Each subscriber sees only their own
/// realm's items. (Control-plane cross-realm aggregation is deferred — the whole
/// observability surface is realm-scoped today, REST included.)</para>
///
/// <para><b>Permission gating (Phase-5 hardening):</b> SignalARR hubs share the
/// cookie-auth pipeline, and <see cref="UIHub"/>'s class-level <c>[Authorize]</c>
/// enforces authentication — but SignalARR has no per-method authorisation
/// attribute. So each stream method here imperatively checks
/// <c>observability:read</c> (the same permission the REST endpoints gate on)
/// against the caller's realm via <see cref="IPermissionService"/>; an
/// unauthorised caller gets an immediately-completed empty stream. The check is
/// performed once at subscribe time (standard for long-lived push channels) and
/// closes the gap the previous revision flagged as a follow-up.</para>
///
/// <para>Neither stream replays history on subscribe — the client already has
/// it from the REST snapshot; replaying would double-count.</para>
/// </summary>
[MessageName("Observability")]
public class ObservabilityHub(ObservabilityActivityBuffer buffer, RealmErrorBuffer errorBuffer)
    : ServerMethods<UIHub>
{
    /// <summary>Live metered activity events (login, token, DCR, …) for the caller's realm.</summary>
    public IObservable<ObservabilityEvent> Subscribe()
    {
        var http = Context.GetHttpContext();
        var realm = CallerRealm(http);

        return AuthorizedRealmStream<ObservabilityEvent>(http, realm, onNext =>
        {
            void Handler(ObservabilityEvent ev)
            {
                if (string.Equals(ev.Realm, realm, StringComparison.Ordinal)) onNext(ev);
            }

            buffer.EventRecorded += Handler;
            return () => buffer.EventRecorded -= Handler;
        });
    }

    /// <summary>
    /// Live operational error feed (Phase 5, §B.3) for the caller's realm.
    /// Pushes every <see cref="ErrorLogEntry"/> the <see cref="ErrorFeedSink"/>
    /// captures into this realm's bounded ring.
    /// </summary>
    public IObservable<ErrorLogEntry> LogsSubscribe()
    {
        var http = Context.GetHttpContext();
        var realm = CallerRealm(http);

        return AuthorizedRealmStream<ErrorLogEntry>(http, realm, onNext =>
        {
            void Handler(ErrorLogEntry entry)
            {
                if (string.Equals(entry.Realm, realm, StringComparison.Ordinal)) onNext(entry);
            }

            errorBuffer.EntryRecorded += Handler;
            return () => errorBuffer.EntryRecorded -= Handler;
        });
    }

    /// <summary>
    /// The caller's realm, read from the connection context (set at connect by
    /// RealmMiddleware). Null when no tenant was resolved — callers fail closed.
    /// </summary>
    private static string? CallerRealm(HttpContext? http)
        => http?.Items[TenantConstants.HttpContextTenantIdKey] as string;

    /// <summary>
    /// Wraps a buffer subscription in an <c>observability:read</c> permission
    /// gate. <paramref name="subscribe"/> attaches a handler and returns the
    /// detach action; it is wired only once the caller is authorised, and torn
    /// down when the client unsubscribes (the stream's cancellation token).
    /// </summary>
    private static IObservable<T> AuthorizedRealmStream<T>(
        HttpContext? http, string? realm, Func<Action<T>, Action> subscribe)
    {
        return Observable.Create<T>(async (observer, ct) =>
        {
            if (!await IsAuthorizedAsync(http, realm))
            {
                observer.OnCompleted();
                return;
            }

            void OnNext(T item)
            {
                try { observer.OnNext(item); }
                catch { /* observer disposed mid-flight — handled on cancel */ }
            }

            var detach = subscribe(OnNext);
            try
            {
                // Keep the subscription alive until the client unsubscribes /
                // disconnects, which cancels ct.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) { /* expected on teardown */ }
            finally
            {
                detach();
            }
        });
    }

    private static async Task<bool> IsAuthorizedAsync(HttpContext? http, string? realm)
    {
        if (http is null || string.IsNullOrEmpty(realm)) return false;
        var userId = http.GetUserId();
        if (userId is null) return false;

        // Hub dispatch runs outside the request's tenant scope, so bind the
        // permission lookup to the caller's realm explicitly: the tenant-scoped
        // IQuerySession behind IPermissionService resolves TenantContext.Current
        // at construction. A fresh DI scope avoids reusing a connection-scoped
        // session that may already be bound to another tenant.
        using var _ = TenantContext.Enter(realm);
        await using var scope = http.RequestServices.CreateAsyncScope();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();
        return await permissions.HasPermissionAsync(userId.Value, AppSlugs.Modgud, "observability:read");
    }
}
