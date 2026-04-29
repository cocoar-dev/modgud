using BuildingBlocks.Helper;
using Marten;
using Cocoar.Auth.Authorization.AspNetCore;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Events;

namespace Cocoar.Auth.Api.Features.Roles;

public record CreateRoleDto(string Name, string? Description, string ResourceType, List<string> Permissions, string? AppSlug = null);

public static class RolesEndpoints
{
    public static WebApplication MapRolesEndpoints(this WebApplication application, string path)
    {
        var roleGroup = application.MapGroup($"{path}/role")
            .WithTags("Roles")
            .RequireAuthorization();

        // ── Lookup (any authenticated user) ──────────────────────────────
        roleGroup.MapGet("lookup", async (IDocumentSession session) =>
            {
                var roles = await session.Query<PermissionRole>()
                    .Where(r => !r.IsDeleted)
                    .OrderBy(r => r.Name)
                    .ToListAsync();

                return Results.Ok(roles.Select(r => new { Id = new ShortGuid(r.Id).ToString(), r.Name, r.ResourceType }));
            })
            .WithName("V2_Role_Lookup");

        // ── Admin-only endpoints ─────────────────────────────────────────

        roleGroup.MapGet("", async (IDocumentSession session) =>
            {
                var roles = await session.Query<PermissionRole>()
                    .Where(r => !r.IsDeleted)
                    .OrderBy(r => r.Name)
                    .ToListAsync();

                return Results.Ok(roles.Select(r => new
                {
                    Id = new ShortGuid(r.Id).ToString(),
                    r.Name,
                    r.Description,
                    r.AppSlug,
                    r.ResourceType,
                    r.Permissions
                }));
            })
            .WithName("V2_Role_GetAll")
            .RequiresPermission("cocoar-auth:permission-role:read");

        roleGroup.MapGet("{id}", async (ShortGuid id, IDocumentSession session) =>
            {
                var role = await session.LoadAsync<PermissionRole>(id.Guid);
                if (role is null || role.IsDeleted) return Results.NotFound();
                return Results.Ok(new { Id = new ShortGuid(role.Id).ToString(), role.Name, role.Description, role.AppSlug, role.ResourceType, role.Permissions });
            })
            .WithName("V2_Role_GetById")
            .RequiresPermission("cocoar-auth:permission-role:read");

        roleGroup.MapPost("", async (CreateRoleDto dto, IDocumentSession session) =>
            {
                var role = new PermissionRole
                {
                    Id = Guid.NewGuid(),
                    Name = dto.Name,
                    Description = dto.Description,
                    AppSlug = string.IsNullOrEmpty(dto.AppSlug) ? AppSlugs.CocoarAuth : dto.AppSlug,
                    ResourceType = dto.ResourceType,
                    Permissions = dto.Permissions
                };
                session.Store(role);
                session.Events.StartStream(role.Id,
                    new PermissionRoleCreatedEvent(role.Id, role.Name, role.Description, role.AppSlug, role.ResourceType, role.Permissions));
                await session.SaveChangesAsync();
                return Results.Ok(new { Id = new ShortGuid(role.Id).ToString(), role.Name, role.Description, role.AppSlug, role.ResourceType, role.Permissions });
            })
            .WithName("V2_Role_Create")
            .RequiresPermission("cocoar-auth:permission-role:write");

        roleGroup.MapPut("{id}", async (ShortGuid id, CreateRoleDto dto, IDocumentSession session) =>
            {
                var role = await session.LoadAsync<PermissionRole>(id.Guid);
                if (role is null || role.IsDeleted) return Results.NotFound();
                role.Name = dto.Name;
                role.Description = dto.Description;
                role.AppSlug = string.IsNullOrEmpty(dto.AppSlug) ? AppSlugs.CocoarAuth : dto.AppSlug;
                role.ResourceType = dto.ResourceType;
                role.Permissions = dto.Permissions;
                session.Store(role);
                session.Events.Append(id.Guid, new PermissionRoleUpdatedEvent(id.Guid, role.Name, role.Description, role.AppSlug, role.ResourceType, role.Permissions));
                await session.SaveChangesAsync();
                return Results.Ok(new { Id = new ShortGuid(role.Id).ToString(), role.Name, role.Description, role.AppSlug, role.ResourceType, role.Permissions });
            })
            .WithName("V2_Role_Update")
            .RequiresPermission("cocoar-auth:permission-role:write");

        roleGroup.MapDelete("{id}", async (ShortGuid id, IDocumentSession session) =>
            {
                var role = await session.LoadAsync<PermissionRole>(id.Guid);
                if (role is null || role.IsDeleted) return Results.NotFound();
                role.IsDeleted = true;
                session.Store(role);
                session.Events.Append(id.Guid, new PermissionRoleDeletedEvent(id.Guid));
                await session.SaveChangesAsync();
                return Results.NoContent();
            })
            .WithName("V2_Role_Delete")
            .RequiresPermission("cocoar-auth:permission-role:write");

        return application;
    }
}
