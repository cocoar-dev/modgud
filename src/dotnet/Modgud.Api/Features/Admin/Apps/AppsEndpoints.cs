using BuildingBlocks.Helper;
using ErrorOr;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
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

        // Create / Update both delegate to the shared AppAdminService — the single
        // canonical write path the realm-provisioning applier also calls (no divergence).
        appGroup.MapPost("", async (CreateAppDto dto, AppAdminService appAdmin, CancellationToken ct) =>
            {
                var result = await appAdmin.CreateAppAsync(dto, ct);
                return result.IsError ? ToErrorResult(result.FirstError) : Results.Ok(MapToResponse(result.Value));
            })
            .WithName("V2_App_Create")
            .RequiresPermission("app:write");

        appGroup.MapPut("{id}", async (ShortGuid id, UpdateAppDto dto, AppAdminService appAdmin, CancellationToken ct) =>
            {
                var result = await appAdmin.UpdateAppAsync(id.Guid, dto, ct);
                if (!result.IsError) return Results.Ok(MapToResponse(result.Value));

                var error = result.FirstError;
                // The catalog-delete block carries its rich blocker list through the error
                // metadata; render the exact 409 body AppDetails.vue consumes.
                if (error.Code == "App.CatalogEntriesReferenced"
                    && error.Metadata?.TryGetValue("blockers", out var blockers) == true)
                {
                    return Results.Conflict(new { Error = error.Code, Message = error.Description, Blockers = blockers });
                }

                return ToErrorResult(error);
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
                    ? await AppAdminService.FindReferencesAsync(allCatalogIds, session)
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

    // Renders an AppAdminService ErrorOr error with the error code in the body. The shared
    // ErrorOrExtensions.ToResult collapses to { error: description } (no code) — the app
    // admin SPA and the catalog security tests assert on the code, so keep {Error,Message}.
    private static IResult ToErrorResult(Error error)
    {
        var status = error.Type switch
        {
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status500InternalServerError,
        };
        return Results.Json(new { Error = error.Code, Message = error.Description }, statusCode: status);
    }
}
