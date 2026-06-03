using Marten;
using Modgud.Authentication.Audit;
using Modgud.Authorization.AspNetCore;

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
            string? category,
            string? eventType,
            int? limit,
            CancellationToken ct) =>
        {
            // Tenant-scoped session → only the caller's realm (per-realm DB).
            IQueryable<AuthAuditView> query = session.Query<AuthAuditView>();
            if (!string.IsNullOrWhiteSpace(category))
                query = query.Where(x => x.Category == category);
            if (!string.IsNullOrWhiteSpace(eventType))
                query = query.Where(x => x.EventType == eventType);

            var rows = await query
                .OrderByDescending(x => x.Timestamp)
                .Take(Math.Clamp(limit ?? 200, 1, 1000))
                .ToListAsync(ct);

            return Results.Ok(rows);
        })
        .WithName("AdminAudit_Get")
        .RequiresPermission("auth-log:read");

        return application;
    }
}
