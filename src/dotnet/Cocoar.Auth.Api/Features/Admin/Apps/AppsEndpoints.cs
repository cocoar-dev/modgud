using BuildingBlocks.Helper;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.AspNetCore;
using Cocoar.Auth.Authorization.Events;
using Cocoar.Auth.Domain.OAuth.Apis;
using Marten;

namespace Cocoar.Auth.Api.Features.Admin.Apps;

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
/// (<see cref="AppSlugs.CocoarAuth"/>) is seeded automatically by
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
            .RequiresPermission("cocoar-auth:app:read");

        appGroup.MapGet("{id}", async (ShortGuid id, IDocumentSession session) =>
            {
                var app = await session.LoadAsync<App>(id.Guid);
                if (app is null || app.IsDeleted) return Results.NotFound();
                return Results.Ok(MapToResponse(app));
            })
            .WithName("V2_App_GetById")
            .RequiresPermission("cocoar-auth:app:read");

        appGroup.MapPost("", async (CreateAppDto dto, IDocumentSession session) =>
            {
                if (!AppSlugRules.IsValidFormat(dto.Slug))
                    return Results.BadRequest(new { Error = "App.InvalidSlug",
                        Message = "Slug must be 3-63 characters, start with a letter, end with a letter or digit, and contain only lowercase letters, digits, and hyphens." });

                if (AppSlugRules.IsReserved(dto.Slug))
                    return Results.BadRequest(new { Error = "App.ReservedSlug",
                        Message = $"The slug '{dto.Slug}' is reserved." });

                if (string.IsNullOrWhiteSpace(dto.DisplayName))
                    return Results.BadRequest(new { Error = "App.DisplayNameRequired",
                        Message = "DisplayName is required." });

                var existing = await session.Query<App>()
                    .Where(a => a.Slug == dto.Slug && !a.IsDeleted)
                    .AnyAsync();
                if (existing)
                    return Results.Conflict(new { Error = "App.DuplicateSlug",
                        Message = $"An app with slug '{dto.Slug}' already exists." });

                var permissionsResult = NormalizePermissions(dto.Permissions, existingByKey: null);
                if (permissionsResult.Error is not null) return permissionsResult.Error;

                var id = Guid.NewGuid();
                var created = new AppCreatedEvent(
                    Id: id,
                    Slug: dto.Slug,
                    DisplayName: dto.DisplayName,
                    Description: dto.Description,
                    Permissions: permissionsResult.Permissions,
                    IsSystem: false);
                session.Events.StartStream<App>(id, created);
                await session.SaveChangesAsync();

                var loaded = await session.LoadAsync<App>(id);
                return Results.Ok(MapToResponse(loaded!));
            })
            .WithName("V2_App_Create")
            .RequiresPermission("cocoar-auth:app:write");

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
            .RequiresPermission("cocoar-auth:app:write");

        appGroup.MapDelete("{id}", async (ShortGuid id, IDocumentSession session) =>
            {
                var app = await session.LoadAsync<App>(id.Guid);
                if (app is null || app.IsDeleted) return Results.NotFound();

                if (app.IsSystem)
                    return Results.BadRequest(new { Error = "App.CannotDeleteSystemApp",
                        Message = $"The system app '{app.Slug}' cannot be deleted." });

                session.Events.Append(id.Guid, new AppDeletedEvent(id.Guid));
                await session.SaveChangesAsync();
                return Results.NoContent();
            })
            .WithName("V2_App_Delete")
            .RequiresPermission("cocoar-auth:app:write");

        // Klick-Aktion: provision a default Resource-Server for an App.
        // Idempotent: if a non-deleted RS with the App's slug as Name
        // already exists, surface that one (no new secret). Only when a
        // fresh RS is created does the response include a one-time
        // cleartext API secret — store it now or regenerate later.
        appGroup.MapPost("{id}/default-resource-server",
            async (ShortGuid id, IDocumentSession session, OAuthAdminService oauthAdmin) =>
            {
                var app = await session.LoadAsync<App>(id.Guid);
                if (app is null || app.IsDeleted) return Results.NotFound();

                // Look for an existing RS already linked to this app — by
                // either AppId or by slug-named convention. Idempotent path.
                var existing = await session.Query<OAuthApiState>()
                    .FirstOrDefaultAsync(a => !a.IsDeleted && a.AppId == app.Id);
                if (existing is not null)
                {
                    return Results.Ok(new
                    {
                        ApiId = new ShortGuid(existing.Id).ToString(),
                        existing.Name,
                        ApiSecret = (string?)null,  // null = "already exists, no fresh secret"
                        AlreadyExisted = true,
                    });
                }

                var createDto = new CreateOAuthApiDto
                {
                    Name = app.Slug,
                    DisplayName = app.DisplayName,
                    Description = $"Default resource server for {app.DisplayName}.",
                    Enabled = true,
                    AppId = app.Id.ToString(),
                };
                var result = await oauthAdmin.CreateApiAsync(createDto);
                if (result.IsError)
                {
                    return Results.BadRequest(new
                    {
                        Error = result.FirstError.Code,
                        result.FirstError.Description,
                    });
                }

                return Results.Ok(new
                {
                    ApiId = new ShortGuid(Guid.Parse(result.Value.Id)).ToString(),
                    Name = result.Value.Name,
                    ApiSecret = result.Value.ApiSecret,  // ONE-TIME — copy now
                    AlreadyExisted = false,
                });
            })
            .WithName("V2_App_CreateDefaultResourceServer")
            .RequiresPermission("cocoar-auth:app:write");

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
