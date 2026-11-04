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
using Modgud.Api.Realtime;
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
/// unauthorised caller gets an immediately-completed empty stream. The check runs at
/// subscribe time and again every <see cref="PermissionRecheckInterval"/> (gvm6): once
/// the permission is withdrawn the feed detaches and completes, so a running admin
/// console stops receiving data at the latest one interval later.</para>
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
    /// Wraps a buffer subscription in the <c>observability:read</c> gate (see
    /// <see cref="HubAuthorization.AuthorizedRealmFeed{T}"/>).
    /// </summary>
    private static IObservable<T> AuthorizedRealmStream<T>(
        HttpContext? http, string? realm, Func<Action<T>, Action> subscribe)
        => HubAuthorization.AuthorizedRealmFeed(http, realm, "observability:read", PermissionRecheckInterval, subscribe);

    /// <summary>gvm6 — how often a running feed re-checks <c>observability:read</c>.</summary>
    internal static readonly TimeSpan PermissionRecheckInterval = TimeSpan.FromSeconds(15);
}
