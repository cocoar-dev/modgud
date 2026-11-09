using Modgud.Infrastructure.Persistence.Tenancy;
using Serilog.Core;
using Serilog.Events;

namespace Modgud.Authentication.AuthLog;

/// <summary>
/// Serilog enricher that stamps every log event with the ambient realm slug
/// (<see cref="TenantContext.CurrentOrNull"/>) as a <c>Realm</c> property.
///
/// <para><b>Kept after the "Auth:" audit sink was retired</b> (logging/audit
/// redesign Phase 3): the tenant audit no longer flows through Serilog, but the
/// realm tag is still how OPERATIONAL logs are attributed — Console/File today, and
/// the Phase-4 OTel Logs export tomorrow. The streamless security store captures its
/// own realm directly from <c>TenantContext</c> at emit, independent of this.</para>
///
/// <para>The enrichment happens synchronously at emit time, on the request thread
/// where the tenant is set in AsyncLocal, so the realm travels WITH the
/// <see cref="LogEvent"/> to every sink — including ones that run out-of-band with
/// no <c>TenantContext</c>.</para>
///
/// <para>Uses <see cref="TenantContext.CurrentOrNull"/>: an event logged with no realm
/// context (deployment-wide or background work outside a realm) gets no <c>Realm</c>
/// property at all. There is no <c>system</c> fallback — guessing a realm would
/// attribute such work to whichever realm holds the control plane.</para>
///
/// <para><b>Attribution is dual-sourced.</b> <see cref="LogEvent.AddPropertyIfAbsent"/>
/// does not overwrite — so a log call that binds its own <c>{Realm}</c> placeholder
/// wins, and the enricher's ambient value is the fallback for sites that don't. A
/// realm-iterating background job can thus bind the iterated <c>realm.Slug</c> for
/// correct per-realm attribution.</para>
/// </summary>
public sealed class RealmLogEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var realm = TenantContext.CurrentOrNull;
        if (!string.IsNullOrEmpty(realm))
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("Realm", realm));
    }
}
