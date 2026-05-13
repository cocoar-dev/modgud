using System.Reactive.Linq;
using Cocoar.Auth.Infrastructure.Observability;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Cocoar.SignalARRR.Common.Attributes;
using Cocoar.SignalARRR.Server;

namespace Cocoar.Auth.Api.Features.Admin;

/// <summary>
/// SignalARR hub that streams live observability events to subscribed
/// admin clients. Pairs with the REST <c>/api/admin/observability/snapshot</c>
/// + <c>/activity</c> endpoints — the REST surface delivers the initial
/// rolling-window state, this stream pushes every new event as it happens.
///
/// <para>Realm filtering: <see cref="TenantContext.Current"/> is captured
/// at subscription time. Each subscriber sees only their own realm's
/// events. Cross-realm aggregation is Phase 5.5 (see audit-followup
/// doc).</para>
///
/// <para>This hub deliberately does NOT replay history on subscribe —
/// the client already has it from the REST snapshot, replaying would
/// double-count.</para>
///
/// <para>Permission gating: SignalARR hubs share the cookie-auth pipeline
/// of normal endpoints, but per-method authorisation is not yet on this
/// stack. The hub is callable by any authenticated admin; the realm
/// filter is the effective scope gate. A formal
/// <c>observability:read</c> check belongs on the SignalARR layer as a
/// followup (matches <c>UserHub</c> which has the same limitation today).</para>
/// </summary>
[MessageName("Observability")]
public class ObservabilityHub(ObservabilityActivityBuffer buffer)
    : ServerMethods<UIHub>
{
    public IObservable<ObservabilityEvent> Subscribe()
    {
        // Capture realm at subscription time — TenantContext is set by
        // RealmMiddleware before the SignalARR dispatch runs.
        var realm = TenantContext.Current;

        return Observable.Create<ObservabilityEvent>(observer =>
        {
            void Handler(ObservabilityEvent ev)
            {
                if (!string.Equals(ev.Realm, realm, StringComparison.Ordinal)) return;
                try { observer.OnNext(ev); }
                catch { /* observer disposed mid-flight — handled by IDisposable */ }
            }

            buffer.EventRecorded += Handler;
            return () => buffer.EventRecorded -= Handler;
        });
    }
}
