using Modgud.Authorization.AspNetCore;
using Modgud.Infrastructure.Observability;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Features.Admin;

/// <summary>
/// Admin endpoints for the in-app live observability view (Phase 5).
/// Backed by the in-memory <see cref="ObservabilityActivityBuffer"/>, which
/// captures every event <see cref="ModgudMeters"/> emits to OpenTelemetry.
///
/// <para>v1 filters by the caller's tenant realm — each realm-admin sees
/// only their own realm's events. Cross-realm aggregate (a "global-ops"
/// view) is parked for Phase 5.5; rationale + design in
/// the maintainers' <c>observability-opentelemetry</c> design note.</para>
/// </summary>
public static class AdminObservabilityEndpoints
{
    public static WebApplication MapAdminObservabilityEndpoints(this WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/admin/observability")
            .WithTags("Admin Observability")
            .RequireAuthorization()
            .RequiresPermission("observability:read");

        // GET /api/admin/observability/snapshot?windowMinutes=15
        // Aggregated event counts grouped by event_type for the rolling window.
        group.MapGet("snapshot", (
            ObservabilityActivityBuffer buffer,
            int? windowMinutes) =>
        {
            var window = TimeSpan.FromMinutes(Math.Clamp(windowMinutes ?? 15, 1, 60));
            var cutoff = DateTimeOffset.UtcNow - window;
            var realm = TenantContext.Current;
            var events = buffer.GetSince(cutoff, realm);

            var counts = events
                .GroupBy(e => e.EventType)
                .ToDictionary(g => g.Key, g => g.Count());

            // Login outcome breakdown — the headline KPI for an IdP.
            var loginByOutcome = events
                .Where(e => e.EventType == ObservabilityEventTypes.Login)
                .GroupBy(e => e.Tags.TryGetValue("outcome", out var o) ? o : "unknown")
                .ToDictionary(g => g.Key, g => g.Count());

            // Per-minute buckets for a sparkline. windowMinutes points.
            var totalMinutes = (int)window.TotalMinutes;
            var buckets = new int[totalMinutes];
            var now = DateTimeOffset.UtcNow;
            foreach (var e in events.Where(e => e.EventType == ObservabilityEventTypes.Login))
            {
                var ageMin = (int)Math.Floor((now - e.Timestamp).TotalMinutes);
                if (ageMin >= 0 && ageMin < totalMinutes)
                    buckets[totalMinutes - 1 - ageMin]++;
            }

            return Results.Ok(new
            {
                Realm = realm,
                WindowMinutes = totalMinutes,
                GeneratedAt = now,
                Counts = counts,
                LoginByOutcome = loginByOutcome,
                LoginSparkline = buckets,
            });
        })
        .WithName("Admin_Observability_Snapshot");

        // GET /api/admin/observability/activity?limit=50
        // Most-recent first.
        group.MapGet("activity", (
            ObservabilityActivityBuffer buffer,
            int? limit) =>
        {
            var realm = TenantContext.Current;
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var snapshot = buffer.GetSince(DateTimeOffset.UtcNow - TimeSpan.FromHours(1), realm);
            // GetSince is chronological — reverse for newest-first.
            var ordered = snapshot.Reverse().Take(take).Select(e => new
            {
                Timestamp = e.Timestamp,
                EventType = e.EventType,
                Realm = e.Realm,
                Tags = e.Tags,
            });
            return Results.Ok(ordered);
        })
        .WithName("Admin_Observability_Activity");

        // GET /api/admin/observability/errors?limit=50
        // Phase 5 (§B.3) — recent operational errors for the caller's realm,
        // newest-first, from the per-realm bounded ring. The initial snapshot
        // for the live error panel; the SignalR LogsSubscribe stream pushes
        // subsequent entries. Realm-scoped via TenantContext (physical scope —
        // each realm reads only its own ring).
        group.MapGet("errors", (
            RealmErrorBuffer errorBuffer,
            int? limit) =>
        {
            var realm = TenantContext.Current;
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var entries = errorBuffer.GetRecent(realm, take); // already newest-first
            var ordered = entries.Select(e => new
            {
                Timestamp = e.Timestamp,
                Realm = e.Realm,
                Level = e.Level,
                Message = e.Message,
                Exception = e.Exception,
                SourceContext = e.SourceContext,
                TraceId = e.TraceId,
            });
            return Results.Ok(ordered);
        })
        .WithName("Admin_Observability_Errors");

        return app;
    }
}
