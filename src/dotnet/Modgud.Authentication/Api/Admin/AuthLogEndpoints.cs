using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http;
using Modgud.Authorization.AspNetCore;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Api.Admin;

/// <summary>
/// Admin <b>Security</b> log surface (logging/audit redesign Track A — the streamless
/// half). Reads the typed <see cref="SecurityAuditEntry"/> store: unknown-actor login
/// attempts, probes, rate-limits, policy rejections, and operational actions. Entries
/// live cross-realm in the system DB but are attributed to a realm via
/// <see cref="SecurityAuditEntry.Realm"/>.
///
/// <para>The read/clear scope by the CALLER'S realm so a tenant realm-admin sees and
/// clears only their own realm's <b>tenant-visible</b> events; the <b>control-plane</b>
/// realm (per <c>TenantInfo.IsControlPlane</c>, not a hard-coded "system" slug) sees and
/// clears the full cross-realm log including control-plane-only operational rows
/// (<see cref="SecurityAuditEntry.PlatformOnly"/>). This carries PR #50's scoping forward
/// and extends it with the platform-only visibility gate.</para>
///
/// <para>The HTTP surface (route, shape) is carried forward from the legacy AuthLog so
/// the SPA keeps working; the backing store changed from the flat AuthLogDocument to the
/// typed SecurityAuditEntry.</para>
/// </summary>
public static class AuthLogEndpoints
{
    public static WebApplication MapAuthLogEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/auth-log")
            .WithTags("Admin Security Log")
            .RequireAuthorization();

        group.MapGet("", async (
            IDocumentStore store,
            HttpContext http,
            string? category,
            string? eventType,
            int? limit) =>
        {
            await using var session = store.QuerySession(TenantConstants.SystemTenantId);

            var query = ScopeToCallerRealm(
                session.Query<SecurityAuditEntry>(), TenantContext.Current, IsControlPlane(http));
            if (!string.IsNullOrWhiteSpace(category))
                query = query.Where(x => x.Category == category);
            if (!string.IsNullOrWhiteSpace(eventType))
                query = query.Where(x => x.EventType == eventType);

            var rows = await query
                .OrderByDescending(x => x.Timestamp)
                .Take(Math.Clamp(limit ?? 200, 1, 1000))
                .ToListAsync();

            // Carry-forward DTO: the legacy grid columns (Timestamp/Level/Message/
            // UserName/Ip/Realm) keep their names — Actor maps to UserName — plus the
            // new EventType/Category for taxonomy-chip filtering and Status/Reason.
            var dtos = rows.Select(r => new SecurityLogEntryDto(
                r.Timestamp, r.Realm, r.Category, r.EventType, r.Level,
                r.Actor, r.Ip, r.Status, r.Reason, r.Message));

            return Results.Ok(dtos);
        })
        .WithName("AdminAuthLog_Get")
        .RequiresPermission("auth-log:read");

        // Clearing the security log is destructive — gate behind the global app:admin
        // bypass. Scoped to the caller's realm; the control-plane realm wipes the full
        // log. The clear is itself audited (audit-of-the-audit): a typed
        // audit.log_cleared record naming the operator is emitted AFTER the wipe, so it
        // survives as the forensic trail of who cleared what, when.
        group.MapDelete("", async (
            IDocumentStore store,
            HttpContext http,
            ClaimsPrincipal user,
            ISecurityAuditLog securityAudit) =>
        {
            var callerRealm = TenantContext.Current;
            var isControlPlane = IsControlPlane(http);

            await using var session = store.LightweightSession(TenantConstants.SystemTenantId);
            if (isControlPlane)
                session.DeleteWhere<SecurityAuditEntry>(x => true);
            else
                session.DeleteWhere<SecurityAuditEntry>(x => x.Realm == callerRealm);
            await session.SaveChangesAsync();

            var operatorName = user.Identity?.Name ?? "(unknown)";
            securityAudit.Record(new SecurityAuditRecord
            {
                EventType = AuditEvents.AuditLogCleared,
                Level = "Warning",
                Actor = operatorName,
                Status = "cleared",
                Reason = isControlPlane ? "all realms (control-plane)" : $"realm {callerRealm}",
                Message = $"Security log cleared by {operatorName}",
            });

            return Results.Ok(new { Message = "Security log cleared" });
        })
        .WithName("AdminAuthLog_Clear")
        .RequiresPermission("realm:admin");

        return application;
    }

    private static bool IsControlPlane(HttpContext http) =>
        http.Items[TenantConstants.HttpContextTenantInfoKey] is TenantInfo info && info.IsControlPlane;

    /// <summary>
    /// Realm-scopes a security-log query: the control-plane realm sees the full
    /// cross-realm log (including control-plane-only operational rows); every other
    /// realm sees only its own realm's <b>tenant-visible</b> entries
    /// (<c>!PlatformOnly</c>). Pure + provider-agnostic so it composes over either
    /// Marten's IQueryable or an in-memory one (used by the unit tests).
    /// </summary>
    public static IQueryable<SecurityAuditEntry> ScopeToCallerRealm(
        IQueryable<SecurityAuditEntry> query, string callerRealm, bool callerIsControlPlane)
        => callerIsControlPlane
            ? query
            : query.Where(x => x.Realm == callerRealm && !x.PlatformOnly);
}

/// <summary>Read DTO for the Security log grid. Carries the legacy column names
/// (<see cref="UserName"/> = the entry's <c>Actor</c>) so the existing SPA keeps
/// working, plus the typed <see cref="EventType"/>/<see cref="Category"/> for chip
/// filtering and <see cref="Status"/>/<see cref="Reason"/> detail.</summary>
public sealed record SecurityLogEntryDto(
    DateTimeOffset Timestamp,
    string? Realm,
    string Category,
    string EventType,
    string Level,
    string? UserName,
    string? Ip,
    string? Status,
    string? Reason,
    string Message);
