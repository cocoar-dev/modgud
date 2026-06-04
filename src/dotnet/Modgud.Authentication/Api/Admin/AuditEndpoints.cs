using Marten;
using Modgud.Authentication.Audit;
using Modgud.Authentication.Domain;
using Modgud.Authentication.RealmSettings;
using Modgud.Authorization.AspNetCore;
using Modgud.Domain.Realms;

namespace Modgud.Authentication.Api.Admin;

/// <summary>
/// Tenant audit read surface (logging/audit redesign Track A — the GDPR-audit half).
///
/// <para>Unlike the legacy <c>AuthLog</c> (cross-realm in the system DB, scoped at
/// read via <c>ScopeToCallerRealm</c>), <see cref="AuthAuditView"/> lives
/// <b>per-realm in the tenant DB</b>. So the tenant-scoped <see cref="IDocumentSession"/>
/// returns only the caller's realm by <b>physical isolation</b> — no
/// <c>WHERE Realm =</c> filter is needed and a filter bug cannot leak cross-realm.
/// Control-plane cross-realm fan-out across realm DBs is deferred; the platform-wide
/// surface is the streamless security store (Phase 3).</para>
/// </summary>
public static class AuditEndpoints
{
    public static WebApplication MapAuditEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/audit")
            .WithTags("Admin Audit")
            .RequireAuthorization();

        group.MapGet("", async (
            IDocumentSession session,
            IRealmSettingsService realmSettings,
            string? category,
            string? eventType,
            int? limit,
            CancellationToken ct) =>
        {
            // Visibility window (§A.6): show only the last N days. A *view* bound,
            // not a deletion — older rows stay on the (masked-on-erase) streams.
            var window = (await realmSettings.LoadAsync(ct)).Audit ?? AuditSettings.Defaults;
            var cutoff = DateTimeOffset.UtcNow.AddDays(-window.VisibilityWindowDays);

            // Tenant-scoped session → only the caller's realm (per-realm DB).
            IQueryable<AuthAuditView> query = session.Query<AuthAuditView>()
                .Where(x => x.Timestamp >= cutoff);
            if (!string.IsNullOrWhiteSpace(category))
                query = query.Where(x => x.Category == category);
            if (!string.IsNullOrWhiteSpace(eventType))
                query = query.Where(x => x.EventType == eventType);

            var rows = await query
                .OrderByDescending(x => x.Timestamp)
                .Take(Math.Clamp(limit ?? 200, 1, 1000))
                .ToListAsync(ct);

            // Resolve the actor's identity at read time by joining to ApplicationUser
            // (NOT the UserView projection) on purpose: GdprService masks the
            // ApplicationUser doc IN PLACE on erase (UserName -> "deleted-{guid}",
            // name/email nulled, GdprService.cs:230-243), so an erased user shows up
            // de-identified here for free. UserView keeps the stale real name until a
            // rebuild and would leak it.
            var userIds = rows.Where(r => r.UserId.HasValue).Select(r => r.UserId!.Value).Distinct().ToList();
            var names = userIds.Count == 0
                ? new Dictionary<Guid, string?>()
                : (await session.Query<ApplicationUser>().Where(u => userIds.Contains(u.Id)).ToListAsync(ct))
                    .ToDictionary(u => u.Id, u => (string?)u.UserName);

            var dtos = rows.Select(r => new AuditLogEntryDto(
                r.Timestamp, r.Realm, r.Category, r.EventType,
                r.UserId is { } uid && names.TryGetValue(uid, out var name) ? name : null,
                r.Ip, r.Method, r.Count, r.Level));

            return Results.Ok(dtos);
        })
        .WithName("AdminAudit_Get")
        .RequiresPermission("audit-log:read");

        return application;
    }
}

/// <summary>Read DTO for the tenant audit grid: the <see cref="AuthAuditView"/> row
/// plus the actor's display identity (<see cref="User"/>) resolved at read time from
/// the erasure-masked <c>ApplicationUser</c> doc — so an erased user reads as
/// <c>deleted-{guid}</c>, never their real name.</summary>
public sealed record AuditLogEntryDto(
    DateTimeOffset Timestamp,
    string? Realm,
    string Category,
    string EventType,
    string? User,
    string? Ip,
    string? Method,
    int? Count,
    string Level);
