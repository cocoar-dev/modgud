using Marten;
using Microsoft.AspNetCore.Http;
using Modgud.Authorization.AspNetCore;
using Modgud.Authentication.AuthLog;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Api.Admin;

/// <summary>
/// Admin auth-log surface. Entries are persisted to the system DB (a single
/// cross-realm audit store) but attributed to a realm via
/// <c>AuthLogDocument.Realm</c>. The read/clear here scope by the CALLER'S realm
/// so a tenant realm-admin sees and clears only their own realm's events; the
/// <b>control-plane</b> realm (the cross-realm operator, per
/// <c>Realm.IsControlPlane</c>) sees and can clear the full cross-realm log.
///
/// <para>The control-plane check reads the request's resolved
/// <see cref="TenantInfo"/> (<c>IsControlPlane</c>), NOT a hard-coded "system"
/// slug — so the global view follows the control-plane role if it is ever
/// transferred to another realm, and a realm that merely happens to be named
/// "system" but no longer holds the role cannot see other realms' events.</para>
///
/// <para>Without this the read used a tenant-scoped session against the caller's
/// (empty) tenant DB, so non-system realm-admins saw nothing while the system
/// view commingled every realm.</para>
/// </summary>
public static class AuthLogEndpoints
{
    public static WebApplication MapAuthLogEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/auth-log")
            .WithTags("Admin Auth Log")
            .RequireAuthorization();

        group.MapGet("", async (IDocumentStore store, HttpContext http, int? limit) =>
        {
            await using var session = store.QuerySession(TenantConstants.SystemTenantId);
            var entries = await ScopeToCallerRealm(
                    session.Query<AuthLogDocument>(), TenantContext.Current, IsControlPlane(http))
                .OrderByDescending(x => x.Timestamp)
                .Take(limit ?? 200)
                .ToListAsync();

            return Results.Ok(entries);
        })
        .WithName("AdminAuthLog_Get")
        .RequiresPermission("auth-log:read");

        // Clearing the auth log is destructive — gate behind the global app:admin
        // bypass. (We deliberately don't add an `auth-log:write` since the only
        // write op is wipe-all.) Scoped to the caller's realm; the control-plane
        // realm wipes the full log.
        group.MapDelete("", async (IDocumentStore store, HttpContext http) =>
        {
            var callerRealm = TenantContext.Current;
            await using var session = store.LightweightSession(TenantConstants.SystemTenantId);
            if (IsControlPlane(http))
                session.DeleteWhere<AuthLogDocument>(x => true);
            else
                session.DeleteWhere<AuthLogDocument>(x => x.Realm == callerRealm);
            await session.SaveChangesAsync();
            return Results.Ok(new { Message = "Auth log cleared" });
        })
        .WithName("AdminAuthLog_Clear")
        .RequiresPermission("realm:admin");

        return application;
    }

    private static bool IsControlPlane(HttpContext http) =>
        http.Items[TenantConstants.HttpContextTenantInfoKey] is TenantInfo info && info.IsControlPlane;

    /// <summary>
    /// Realm-scopes an auth-log query: the control-plane realm sees the full
    /// cross-realm log; every other realm sees only its own entries. Pure +
    /// provider-agnostic so it composes over either Marten's IQueryable or an
    /// in-memory one (used by the unit tests).
    /// </summary>
    public static IQueryable<AuthLogDocument> ScopeToCallerRealm(
        IQueryable<AuthLogDocument> query, string callerRealm, bool callerIsControlPlane)
        => callerIsControlPlane
            ? query
            : query.Where(x => x.Realm == callerRealm);
}
