using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Modgud.Api.Authorization;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Services;

namespace Modgud.Api.Features.Roles;

/// <summary>
/// Create/Update payload for a <see cref="PermissionRole"/>. <see cref="AppId"/>
/// is the (ShortGuid) FK into an ordinary role's App. A realm-admin role
/// must instead leave <see cref="AppId"/> null and
/// <see cref="PermissionIds"/> empty. Each
/// <see cref="PermissionIds"/> entry is an <c>AppPermission.Id</c>
/// (ShortGuid) FK into <c>App.Permissions</c> of the linked App; the
/// admin endpoint validates them at write-time.
/// </summary>
public record RolePayload(
    string Name,
    string? Description,
    string? AppId,
    bool IsRealmAdmin,
    List<string> PermissionIds);

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

                return Results.Ok(roles.Select(r => new { Id = new ShortGuid(r.Id).ToString(), r.Name }));
            })
            .WithName("V2_Role_Lookup");

        // ── Admin-only endpoints ─────────────────────────────────────────

        roleGroup.MapGet("", async (IDocumentSession session) =>
            {
                var roles = await session.Query<PermissionRole>()
                    .Where(r => !r.IsDeleted)
                    .OrderBy(r => r.Name)
                    .ToListAsync();

                return Results.Ok(roles.Select(MapToResponse));
            })
            .WithName("V2_Role_GetAll")
            .RequiresPermission("permission-role:read");

        roleGroup.MapGet("{id}", async (ShortGuid id, IDocumentSession session) =>
            {
                var role = await session.LoadAsync<PermissionRole>(id.Guid);
                if (role is null || role.IsDeleted) return Results.NotFound();
                return Results.Ok(MapToResponse(role));
            })
            .WithName("V2_Role_GetById")
            .RequiresPermission("permission-role:read");

        // PermissionRoleProjection (inline) writes the PermissionRole doc
        // from PermissionRoleCreatedEvent / …UpdatedEvent / …DeletedEvent.
        // Direct session.Store(role) alongside the event in the same
        // SaveChangesAsync conflicts with the projection's own write under
        // Marten 8.34+ optimistic-concurrency detection — emit the event
        // only. Build the in-memory `role` instance just to compute the
        // response payload; the persisted doc comes from the projection.
        // Create / Update both delegate to the shared RoleAdminService — the single
        // canonical write path the realm-provisioning applier also calls. The realm:admin
        // guard is passed as a parameter (here from the HTTP caller's permissions).
        roleGroup.MapPost("", async (RolePayload dto, HttpContext http, IPermissionService perms, RoleAdminService roleAdmin, CancellationToken ct) =>
            {
                var callerIsRealmAdmin = await CallerPermissions.IsRealmAdminAsync(http, perms);
                var result = await roleAdmin.CreateRoleAsync(dto, callerIsRealmAdmin, ct);
                return result.IsError ? ToErrorResult(result.Errors) : Results.Ok(MapToResponse(result.Value));
            })
            .WithName("V2_Role_Create")
            .RequiresPermission("permission-role:write");

        roleGroup.MapPut("{id}", async (ShortGuid id, RolePayload dto, HttpContext http, IPermissionService perms, RoleAdminService roleAdmin, CancellationToken ct) =>
            {
                var callerIsRealmAdmin = await CallerPermissions.IsRealmAdminAsync(http, perms);
                var result = await roleAdmin.UpdateRoleAsync(id.Guid, dto, callerIsRealmAdmin, ct);
                return result.IsError ? ToErrorResult(result.Errors) : Results.Ok(MapToResponse(result.Value));
            })
            .WithName("V2_Role_Update")
            .RequiresPermission("permission-role:write");

        // Delete delegates to the shared RoleAdminService — the same canonical path the
        // realm-provisioning prune calls.
        roleGroup.MapDelete("{id}", async (ShortGuid id, RoleAdminService roleAdmin, CancellationToken ct) =>
            {
                var result = await roleAdmin.DeleteRoleAsync(id.Guid, ct);
                return result.IsError ? ToErrorResult(result.Errors) : Results.NoContent();
            })
            .WithName("V2_Role_Delete")
            .RequiresPermission("permission-role:write");

        return application;
    }

    // Renders a RoleAdminService ErrorOr error as HTTP with the error code in the body.
    // The shared ErrorOrExtensions.ToResult maps Forbidden → Results.Forbid(), which under
    // this app's cookie auth turns /api/* into an empty-body 403 (OnRedirectToAccessDenied)
    // — losing the code the SPA + RealmAdminEscalationGuardTests rely on. This local
    // renderer keeps the {Error,Message} body the role endpoints have always returned.
    private static IResult ToErrorResult(List<Error> errors)
    {
        var error = errors[0];
        var status = error.Type switch
        {
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };
        return Results.Json(new { Error = error.Code, Message = error.Description }, statusCode: status);
    }

    private static object MapToResponse(PermissionRole r) => new
    {
        Id = new ShortGuid(r.Id).ToString(),
        r.Name,
        r.Description,
        AppId = r.AppId is null ? null : new ShortGuid(r.AppId.Value).ToString(),
        r.IsRealmAdmin,
        PermissionIds = r.PermissionIds.Select(id => new ShortGuid(id).ToString()).ToList(),
    };
}
