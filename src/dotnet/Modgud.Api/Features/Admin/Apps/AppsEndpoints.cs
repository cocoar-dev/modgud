using BuildingBlocks.Helper;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authorization.Apps;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Events;
using Modgud.Authorization.Roles;
using Modgud.Domain.OAuth.Apis;
using Marten;

namespace Modgud.Api.Features.Admin.Apps;

/// <summary>
/// Permission entry on the create / update payload. The id is optional on
/// create (server generates one when omitted) and required on update for
/// entries that should keep their stable identity. New entries on update
/// (id == null) get a fresh id; entries present in the projected catalog
/// but missing from the payload are removed.
/// </summary>
public record AppPermissionDto(
    string? Id,
    string Resource,
    string Action,
    string? Description);

public record CreateAppDto(
    string Slug,
    string DisplayName,
    string? Description,
    List<AppPermissionDto> Permissions);

public record UpdateAppDto(
    string DisplayName,
    string? Description,
    List<AppPermissionDto> Permissions);

/// <summary>
/// Admin surface for managing <see cref="App"/> records — the per-realm list
/// of registered Cocoar SaaS apps. The system app
/// (<see cref="AppSlugs.Modgud"/>) is seeded automatically by
/// <c>AppRealmSeeder</c>, cannot be created through this API and cannot be
/// deleted; it can have its display name / description / permission catalog
/// edited.
/// </summary>
public static class AppsEndpoints
{
    public static WebApplication MapAppsEndpoints(this WebApplication application, string path)
    {
        var appGroup = application.MapGroup($"{path}/app")
            .WithTags("Apps")
            .RequireAuthorization();

        // ── Lookup (any authenticated user) ──────────────────────────────
        appGroup.MapGet("lookup", async (IDocumentSession session) =>
            {
                var apps = await session.Query<App>()
                    .Where(a => !a.IsDeleted)
                    .OrderBy(a => a.Slug)
                    .ToListAsync();

                return Results.Ok(apps.Select(a => new
                {
                    Id = new ShortGuid(a.Id).ToString(),
                    a.Slug,
                    a.DisplayName,
                }));
            })
            .WithName("V2_App_Lookup");

        // ── Admin-only endpoints ─────────────────────────────────────────

        appGroup.MapGet("", async (IDocumentSession session) =>
            {
                var apps = await session.Query<App>()
                    .Where(a => !a.IsDeleted)
                    .OrderBy(a => a.Slug)
                    .ToListAsync();

                return Results.Ok(apps.Select(MapToResponse));
            })
            .WithName("V2_App_GetAll")
            .RequiresPermission("app:read");

        appGroup.MapGet("{id}", async (ShortGuid id, IDocumentSession session) =>
            {
                var app = await session.LoadAsync<App>(id.Guid);
                if (app is null || app.IsDeleted) return Results.NotFound();
                return Results.Ok(MapToResponse(app));
            })
            .WithName("V2_App_GetById")
            .RequiresPermission("app:read");

        // Create delegates to the shared AppAdminService — the single canonical create
        // path that the realm-provisioning applier also calls (no divergence). Update /
        // delete stay inline below (their reference-checking is consolidated when the
        // applier gains update via UpdateRealm).
        appGroup.MapPost("", async (CreateAppDto dto, AppAdminService appAdmin, CancellationToken ct) =>
            {
                var result = await appAdmin.CreateAppAsync(dto, ct);
                return result.ToResult(app => Results.Ok(MapToResponse(app)));
            })
            .WithName("V2_App_Create")
            .RequiresPermission("app:write");

        appGroup.MapPut("{id}", async (ShortGuid id, UpdateAppDto dto, IDocumentSession session) =>
            {
                var app = await session.LoadAsync<App>(id.Guid);
                if (app is null || app.IsDeleted) return Results.NotFound();

                if (string.IsNullOrWhiteSpace(dto.DisplayName))
                    return Results.BadRequest(new { Error = "App.DisplayNameRequired",
                        Message = "DisplayName is required." });

                // Existing-permission lookup by id keeps stable identities
                // across updates: an entry already present in the payload by
                // id retains it, an entry without an id gets a fresh one.
                var existingByKey = app.Permissions.ToDictionary(p => p.Id, p => p);
                var permissionsResult = NormalizePermissions(dto.Permissions, existingByKey);
                if (permissionsResult.Error is not null) return permissionsResult.Error;

                // Detect catalog deletions that would orphan FKs in
                // PermissionRole.PermissionIds or OAuthApiState.PermissionIds.
                // Removing an entry that's still referenced by a role or RS is
                // a silent permission revocation in disguise — refuse with 409
                // and surface what's blocking so the admin can clean up.
                var newIds = permissionsResult.Permissions.Select(p => p.Id).ToHashSet();
                var removedIds = app.Permissions
                    .Where(p => !newIds.Contains(p.Id))
                    .ToList();
                if (removedIds.Count > 0)
                {
                    var blockers = await FindReferencesAsync(removedIds.Select(p => p.Id).ToList(), session);
                    if (blockers.Count > 0)
                    {
                        return Results.Conflict(new
                        {
                            Error = "App.CatalogEntriesReferenced",
                            Message = "Cannot remove catalog entries that are still referenced by roles or resource servers. Detach them first.",
                            Blockers = blockers.Select(b => new
                            {
                                PermissionId = new ShortGuid(b.PermissionId).ToString(),
                                Permission = removedIds.First(p => p.Id == b.PermissionId).ToPermissionString(),
                                ReferencedByRoles = b.RoleNames,
                                ReferencedByResourceServers = b.OAuthApiNames,
                            }),
                        });
                    }
                }

                session.Events.Append(id.Guid, new AppUpdatedEvent(
                    id.Guid,
                    dto.DisplayName,
                    dto.Description,
                    permissionsResult.Permissions));
                await session.SaveChangesAsync();

                var loaded = await session.LoadAsync<App>(id.Guid);
                return Results.Ok(MapToResponse(loaded!));
            })
            .WithName("V2_App_Update")
            .RequiresPermission("app:write");

        appGroup.MapDelete("{id}", async (ShortGuid id, IDocumentSession session) =>
            {
                var app = await session.LoadAsync<App>(id.Guid);
                if (app is null || app.IsDeleted) return Results.NotFound();

                if (app.IsSystem)
                    return Results.BadRequest(new { Error = "App.CannotDeleteSystemApp",
                        Message = $"The system app '{app.Slug}' cannot be deleted." });

                // App-level delete-block: if any role or resource-server FKs
                // into this App's catalog (or directly into the App via
                // PermissionRole.AppId / OAuthApiState.AppId), refuse. Same
                // rationale as the per-entry block: deleting an App with live
                // grants is a silent revoke.
                var allCatalogIds = app.Permissions.Select(p => p.Id).ToList();
                var blockingByPermissionId = allCatalogIds.Count > 0
                    ? await FindReferencesAsync(allCatalogIds, session)
                    : [];
                var rolesByApp = await session.Query<PermissionRole>()
                    .Where(r => !r.IsDeleted && r.AppId == app.Id)
                    .Select(r => r.Name)
                    .ToListAsync();
                var apisByApp = await session.Query<OAuthApiState>()
                    .Where(a => !a.IsDeleted && a.AppId == app.Id)
                    .Select(a => a.Name)
                    .ToListAsync();

                if (blockingByPermissionId.Count > 0 || rolesByApp.Count > 0 || apisByApp.Count > 0)
                {
                    return Results.Conflict(new
                    {
                        Error = "App.HasReferences",
                        Message = "Cannot delete an App that's still referenced. Detach roles and resource servers first.",
                        ReferencedByRoles = rolesByApp,
                        ReferencedByResourceServers = apisByApp,
                        CatalogEntryReferences = blockingByPermissionId.Select(b => new
                        {
                            PermissionId = new ShortGuid(b.PermissionId).ToString(),
                            Permission = app.Permissions.First(p => p.Id == b.PermissionId).ToPermissionString(),
                            ReferencedByRoles = b.RoleNames,
                            ReferencedByResourceServers = b.OAuthApiNames,
                        }),
                    });
                }

                session.Events.Append(id.Guid, new AppDeletedEvent(id.Guid));
                await session.SaveChangesAsync();
                return Results.NoContent();
            })
            .WithName("V2_App_Delete")
            .RequiresPermission("app:write");

        return application;
    }

    private static object MapToResponse(App a) => new
    {
        Id = new ShortGuid(a.Id).ToString(),
        a.Slug,
        a.DisplayName,
        a.Description,
        Permissions = a.Permissions
            .Select(p => new
            {
                Id = new ShortGuid(p.Id).ToString(),
                p.Resource,
                p.Action,
                p.Description,
            })
            .ToList(),
        a.IsSystem,
    };

    /// <summary>
    /// Per-permission-id reference summary used by the catalog editor's
    /// delete-block panel. Only entries with at least one referencing role
    /// or RS are returned.
    /// </summary>
    private record PermissionReference(Guid PermissionId, List<string> RoleNames, List<string> OAuthApiNames);

    /// <summary>
    /// Finds every <see cref="PermissionRole"/> and <see cref="OAuthApiState"/>
    /// that references any of the supplied permission ids in their respective
    /// <c>PermissionIds</c> FK list. Returns one entry per permission-id that
    /// has at least one referencing row — empty list = safe to delete.
    /// </summary>
    private static async Task<List<PermissionReference>> FindReferencesAsync(
        List<Guid> permissionIds, IDocumentSession session)
    {
        if (permissionIds.Count == 0) return [];

        // Marten's LINQ provider supports IsOneOf for membership; for a
        // small list of ids in our case (handful of catalog entries) it's
        // acceptable to load every role/api with any non-empty PermissionIds
        // and filter in memory. Tenant DBs aren't huge here.
        var roles = await session.Query<PermissionRole>()
            .Where(r => !r.IsDeleted && r.PermissionIds.Any())
            .ToListAsync();
        var apis = await session.Query<OAuthApiState>()
            .Where(a => !a.IsDeleted && a.PermissionIds.Any())
            .ToListAsync();

        var result = new List<PermissionReference>();
        foreach (var pid in permissionIds)
        {
            var roleNames = roles
                .Where(r => r.PermissionIds.Contains(pid))
                .Select(r => r.Name)
                .ToList();
            var apiNames = apis
                .Where(a => a.PermissionIds.Contains(pid))
                .Select(a => a.Name)
                .ToList();
            if (roleNames.Count > 0 || apiNames.Count > 0)
                result.Add(new PermissionReference(pid, roleNames, apiNames));
        }
        return result;
    }

    /// <summary>
    /// Validates and normalises the permission list off a create / update
    /// payload: parses incoming ids (ShortGuid → Guid, generating a fresh
    /// one when absent or unknown), dedupes by (Resource, Action), enforces
    /// the segment grammar, and returns either a clean list ready to embed
    /// in an event or an HTTP 400 with the first offending entry.
    /// </summary>
    private static (List<AppPermission> Permissions, IResult? Error) NormalizePermissions(
        List<AppPermissionDto>? payload,
        IReadOnlyDictionary<Guid, AppPermission>? existingByKey)
    {
        var input = payload ?? [];
        var normalised = new List<AppPermission>(input.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in input)
        {
            var resource = entry.Resource?.Trim() ?? string.Empty;
            var action = entry.Action?.Trim() ?? string.Empty;

            if (!AppPermissionRules.IsValidSegment(resource) ||
                !AppPermissionRules.IsValidSegment(action))
            {
                return (normalised, Results.BadRequest(new
                {
                    Error = "App.InvalidPermissionSegment",
                    Message = $"Permission '{resource}:{action}' is invalid — both segments must match ^[a-z0-9-]+$.",
                }));
            }

            // realm:admin is the synthetic realm-wide bypass — it must never be
            // a catalog entry (audit H1, vector 3). Conferring realm:admin is
            // reserved to a role's IsRealmAdmin flag, which is itself gated on
            // the caller already holding realm:admin.
            if (AppPermissionRules.IsReservedBypass(resource, action))
            {
                return (normalised, Results.BadRequest(new
                {
                    Error = "App.ReservedPermission",
                    Message = "The permission 'realm:admin' is reserved — it is the realm-wide bypass and cannot be a catalog entry. Use a role's IsRealmAdmin flag instead.",
                }));
            }

            var key = $"{resource}:{action}";
            if (!seen.Add(key))
            {
                // Silently drop exact duplicates — admin UIs may submit a
                // fresh row alongside the existing one when toggling.
                continue;
            }

            // Resolve identity: explicit id wins (when it parses + matches an
            // entry in existingByKey, that's the rename path); otherwise mint
            // a new one.
            Guid id = Guid.NewGuid();
            if (!string.IsNullOrEmpty(entry.Id) && ShortGuid.TryParse(entry.Id, out Guid parsed))
            {
                if (existingByKey is not null && existingByKey.ContainsKey(parsed))
                {
                    id = parsed;
                }
                else
                {
                    // Caller submitted an id we don't recognise. Keep their
                    // value rather than minting a new one — this lets a
                    // detached client hold on to a generated id and replay
                    // the payload without the server treating it as a fresh
                    // entity.
                    id = parsed;
                }
            }

            var description = string.IsNullOrWhiteSpace(entry.Description)
                ? null
                : entry.Description.Trim();

            normalised.Add(new AppPermission(id, resource, action, description));
        }

        return (normalised, null);
    }
}
