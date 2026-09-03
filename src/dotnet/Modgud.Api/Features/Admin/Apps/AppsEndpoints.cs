using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using ErrorOr;
using Modgud.Application.DTOs.Applications;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authentication.Applications;
using Modgud.Api.Features.Management;
using Modgud.Authorization.Apps;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Principals;
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
    List<AppPermissionDto> Permissions,
    // ADR-0011 — an App is ONE resource: the optional per-App settings override is created
    // in the SAME tenant transaction as the App (see AppAdminService). Null = inherit the
    // realm everywhere (the zero-config default). The applier never sends it.
    ApplicationSettingsDto? Settings = null,
    // Optional pinned entity id (Guid/ShortGuid) — provisioning only; a taken id is a conflict.
    string? Id = null);

public record UpdateAppDto(
    string DisplayName,
    string? Description,
    List<AppPermissionDto> Permissions,
    ApplicationSettingsDto? Settings = null);

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

                return Results.Ok(apps.Select(a => MapToResponse(a)));
            })
            .WithName("V2_App_GetAll")
            .RequiresPermission("app:read");

        appGroup.MapGet("{id}", async (ShortGuid id, IDocumentSession session, IApplicationSettingsService settingsSvc, CancellationToken ct) =>
            {
                var app = await session.LoadAsync<App>(id.Guid, ct);
                if (app is null || app.IsDeleted) return Results.NotFound();
                var settings = await settingsSvc.GetAsync(id.Guid, ct);
                return Results.Ok(MapToResponse(app, settings.IsError ? null : settings.Value));
            })
            .WithName("V2_App_GetById")
            .RequiresPermission("app:read");

        // Create / Update both delegate to the shared AppAdminService — the single
        // canonical write path the realm-provisioning applier also calls (no divergence).
        appGroup.MapPost("", async (CreateAppDto dto, AppAdminService appAdmin, IApplicationSettingsService settingsSvc, IDocumentSession session, DataEventDispatcher dispatcher, CancellationToken ct) =>
            {
                var result = await appAdmin.CreateAppAsync(dto, ct);
                if (result.IsError) return ToErrorResult(result.FirstError);
                var settings = await settingsSvc.GetAsync(result.Value.Id, ct);
                var response = MapToResponse(result.Value, settings.IsError ? null : settings.Value);
                dispatcher.DispatchCreatedEvent("App", response, session.TenantId);
                return Results.Ok(response);
            })
            .WithName("V2_App_Create")
            .RequiresPermission("app:write");

        appGroup.MapPut("{id}", async (ShortGuid id, UpdateAppDto dto, AppAdminService appAdmin, IApplicationSettingsService settingsSvc, IDocumentSession session, DataEventDispatcher dispatcher, CancellationToken ct) =>
            {
                var result = await appAdmin.UpdateAppAsync(id.Guid, dto, ct);
                if (!result.IsError)
                {
                    var settings = await settingsSvc.GetAsync(id.Guid, ct);
                    var response = MapToResponse(result.Value, settings.IsError ? null : settings.Value);
                    dispatcher.DispatchUpdatedEvent("App", response, session.TenantId);
                    return Results.Ok(response);
                }

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
        appGroup.MapDelete("{id}", async (ShortGuid id, AppAdminService appAdmin, IDocumentSession session, DataEventDispatcher dispatcher, CancellationToken ct) =>
            {
                var result = await appAdmin.DeleteAppAsync(id.Guid, ct);
                if (!result.IsError)
                {
                    dispatcher.DispatchDeletedEvent("App", id.ToString(), session.TenantId);
                    return Results.NoContent();
                }

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

        // Deliberately mapped outside appGroup: that group is cookie-authenticated,
        // while this read is also part of the explicitly exposed OAuth Management
        // API. The management filter selects cookie or bearer and evaluates the
        // same live app-scope:read permission for both. Bearer callers are also
        // constrained to the target Apps assigned to their OAuth client.
        application.MapGet($"{path}/app/{{id}}/scope", async (
                ShortGuid id,
                IApplicationScopeResolver resolver,
                CancellationToken ct) =>
            {
                var snapshot = await resolver.ResolveAsync(id.Guid, ct);
                if (snapshot is null) return Results.NotFound();

                var rootIds = snapshot.RootGroups.Select(g => g.Id).ToHashSet();
                return Results.Ok(new
                {
                    AppId = new ShortGuid(snapshot.AppId).ToString(),
                    snapshot.AppSlug,
                    snapshot.ScopeVersion,
                    RootGroups = snapshot.RootGroups.Select(g => new
                    {
                        Id = new ShortGuid(g.Id).ToString(),
                        g.Name,
                        HasPermissions = g.RoleIds.Count > 0,
                    }),
                    Principals = snapshot.Principals.Select(p => MapScopePrincipal(p, rootIds)),
                });
            })
            .WithTags("Apps")
            .WithName("V2_App_GetScope")
            .RequiresManagementPermission(
                "app-scope:read",
                clientAppRouteParameter: "id");

        return application;
    }

    // The list endpoint passes no settings (the grid doesn't show them); the detail / create /
    // update responses pass the per-App settings override so the single App modal can render it.
    private static object MapToResponse(App a, ApplicationSettingsDto? settings = null) => new
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
        Settings = settings,
    };

    private static object MapScopePrincipal(Principal principal, IReadOnlySet<Guid> rootGroupIds)
    {
        var person = principal as Person;
        var group = principal as Group;
        var serviceAccount = principal as ServiceAccount;
        var position = principal as PositionPrincipal;

        return new
        {
            Id = new ShortGuid(principal.Id).ToString(),
            principal.Type,
            principal.DisplayName,
            principal.IsActive,
            IsScopeRoot = rootGroupIds.Contains(principal.Id),
            AccountName = person?.AccountName ?? serviceAccount?.AccountName ?? position?.AccountName,
            person?.Firstname,
            person?.Lastname,
            person?.Acronym,
            person?.Email,
            Name = group?.Name,
            Description = group?.Description,
            Purpose = serviceAccount?.Purpose ?? position?.Purpose,
        };
    }

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
