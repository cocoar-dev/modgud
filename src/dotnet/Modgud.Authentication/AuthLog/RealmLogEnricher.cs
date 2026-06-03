using Modgud.Infrastructure.Persistence.Tenancy;
using Serilog.Core;
using Serilog.Events;

namespace Modgud.Authentication.AuthLog;

/// <summary>
/// Serilog enricher that stamps every log event with the ambient realm slug
/// (<see cref="TenantContext.Current"/>) as a <c>Realm</c> property.
///
/// <para>The enrichment happens synchronously at emit time, on the request
/// thread where the tenant is set in AsyncLocal — so the realm travels WITH the
/// <see cref="LogEvent"/> to the sink. This is essential because
/// <c>AuthLogPersistenceService</c> drains <see cref="AuthLogSink"/> out-of-band
/// in a BackgroundService that has no <c>TenantContext</c>; the attribution must
/// be captured here, not at persist time.</para>
///
/// <para>Uses <see cref="TenantContext.Current"/> (which falls back to the
/// <c>system</c> tenant) rather than the nullable variant, so background / no-
/// tenant work is attributed to <c>system</c> — the deployment-level realm —
/// instead of being orphaned with no realm at all.</para>
///
/// <para><b>Attribution is dual-sourced.</b> <see cref="LogEvent.AddPropertyIfAbsent"/>
/// does not overwrite — so a log call whose <c>Auth:</c> message template binds
/// its own <c>{Realm}</c> placeholder wins, and the enricher's ambient value is
/// the fallback for the (majority) of sites that don't. This is intentional: the
/// realm-iterating background jobs (e.g. the signing-key janitor / DCR GC) run in
/// a single <c>system</c> session and bind the iterated <c>realm.Slug</c> in
/// their template, which is the CORRECT per-realm attribution that the ambient
/// <c>system</c> fallback could not give. <b>Convention:</b> any <c>{Realm}</c>
/// bound in an <c>Auth:</c> template MUST be the realm the event pertains to —
/// never another realm — because that value scopes who sees the row.</para>
/// </summary>
public sealed class RealmLogEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("Realm", TenantContext.Current));
    }
}
