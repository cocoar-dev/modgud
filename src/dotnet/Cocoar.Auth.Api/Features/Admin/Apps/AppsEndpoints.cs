using BuildingBlocks.Helper;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.AspNetCore;
using Cocoar.Auth.Authorization.Events;
using Marten;

namespace Cocoar.Auth.Api.Features.Admin.Apps;

public record CreateAppDto(
    string Slug,
    string DisplayName,
    string? Description,
    List<string> Resources);

public record UpdateAppDto(
    string DisplayName,
    string? Description,
    List<string> Resources);

/// <summary>
/// Admin surface for managing <see cref="App"/> records — the per-realm list
/// of registered Cocoar SaaS apps. The system app
/// (<see cref="AppSlugs.CocoarAuth"/>) is seeded automatically by
/// <c>AppRealmSeeder</c>, cannot be created through this API and cannot be
/// deleted; it can only have its display name / description / resources
/// edited (resources still map to <c>ResourceRegistry</c> registrations
/// hardcoded in <c>DependencyInjection</c>, so add new resources cautiously).
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

                var resources = (dto.Resources ?? [])
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var id = Guid.NewGuid();
                var created = new AppCreatedEvent(
                    Id: id,
                    Slug: dto.Slug,
                    DisplayName: dto.DisplayName,
                    Description: dto.Description,
                    Resources: resources,
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

                var resources = (dto.Resources ?? [])
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                session.Events.Append(id.Guid, new AppUpdatedEvent(
                    id.Guid,
                    dto.DisplayName,
                    dto.Description,
                    resources));
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

        return application;
    }

    private static object MapToResponse(App a) => new
    {
        Id = new ShortGuid(a.Id).ToString(),
        a.Slug,
        a.DisplayName,
        a.Description,
        a.Resources,
        a.IsSystem,
    };
}
