using BuildingBlocks.Helper;
using ErrorOr;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authorization.Apps;
using Modgud.Authorization.AspNetCore;
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

        // Delete delegates to the shared AppAdminService — the same canonical path the
        // realm-provisioning prune calls. The App-level reference block carries its rich
        // blocker list through the error metadata; render the exact 409 body AppDetails.vue
        // consumes.
        appGroup.MapDelete("{id}", async (ShortGuid id, AppAdminService appAdmin, CancellationToken ct) =>
            {
                var result = await appAdmin.DeleteAppAsync(id.Guid, ct);
                if (!result.IsError) return Results.NoContent();

                var error = result.FirstError;
                if (error.Code == "App.HasReferences"
                    && error.Metadata?.TryGetValue("appReferences", out var raw) == true
                    && raw is AppReferenceBlockers refs)
                {
                    return Results.Conflict(new
                    {
                        Error = error.Code,
                        Message = error.Description,
                        ReferencedByRoles = refs.ReferencedByRoles,
                        ReferencedByResourceServers = refs.ReferencedByResourceServers,
                        CatalogEntryReferences = refs.CatalogEntryReferences.Select(b => new
                        {
                            b.PermissionId,
                            b.Permission,
                            b.ReferencedByRoles,
                            b.ReferencedByResourceServers,
                        }),
                    });
                }

                return ToErrorResult(error);
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
