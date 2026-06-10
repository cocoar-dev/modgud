using BuildingBlocks.Helper;
using Marten;
using Modgud.Api.Authorization;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Services;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;

namespace Modgud.Api.Features.Roles;

/// <summary>
/// Create/Update payload for a <see cref="PermissionRole"/>. <see cref="AppId"/>
/// is the (ShortGuid) FK into the role's App; null = the role is a pure
/// realm-admin role and must therefore set <see cref="IsRealmAdmin"/> to
/// true and leave <see cref="PermissionIds"/> empty. Each
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
        roleGroup.MapPost("", async (RolePayload dto, HttpContext http, IPermissionService perms, IDocumentSession session) =>
            {
                // Privilege-escalation guard (audit H1): only a realm:admin may
                // mint a realm-admin role. permission-role:write alone is not
                // enough — a realm-admin role is the realm-wide bypass.
                if (dto.IsRealmAdmin && !await CallerPermissions.IsRealmAdminAsync(http, perms))
                    return RealmAdminForbidden();

                var built = await BuildRoleAsync(dto, session);
                if (built.Error is not null) return built.Error;

                var role = built.Role;
                session.Events.StartStream(role.Id,
                    new PermissionRoleCreatedEvent(
                        role.Id, role.Name, role.Description,
                        role.AppId, role.IsRealmAdmin, role.PermissionIds));
                await session.SaveChangesAsync();
                return Results.Ok(MapToResponse(role));
            })
            .WithName("V2_Role_Create")
            .RequiresPermission("permission-role:write");

        roleGroup.MapPut("{id}", async (ShortGuid id, RolePayload dto, HttpContext http, IPermissionService perms, IDocumentSession session) =>
            {
                var existing = await session.LoadAsync<PermissionRole>(id.Guid);
                if (existing is null || existing.IsDeleted) return Results.NotFound();

                // Privilege-escalation guard (audit H1): only a realm:admin may
                // set/keep the realm-admin flag on a role. A non-admin may still
                // de-escalate (clear the flag) or edit a non-admin role.
                if (dto.IsRealmAdmin && !await CallerPermissions.IsRealmAdminAsync(http, perms))
                    return RealmAdminForbidden();

                var built = await BuildRoleAsync(dto, session);
                if (built.Error is not null) return built.Error;

                existing.Name = built.Role.Name;
                existing.Description = built.Role.Description;
                existing.AppId = built.Role.AppId;
                existing.IsRealmAdmin = built.Role.IsRealmAdmin;
                existing.PermissionIds = built.Role.PermissionIds;
                session.Events.Append(id.Guid,
                    new PermissionRoleUpdatedEvent(
                        id.Guid, existing.Name, existing.Description,
                        existing.AppId, existing.IsRealmAdmin, existing.PermissionIds));
                await session.SaveChangesAsync();
                return Results.Ok(MapToResponse(existing));
            })
            .WithName("V2_Role_Update")
            .RequiresPermission("permission-role:write");

        roleGroup.MapDelete("{id}", async (ShortGuid id, IDocumentSession session) =>
            {
                var role = await session.LoadAsync<PermissionRole>(id.Guid);
                if (role is null || role.IsDeleted) return Results.NotFound();
                session.Events.Append(id.Guid, new PermissionRoleDeletedEvent(id.Guid));
                await session.SaveChangesAsync();
                return Results.NoContent();
            })
            .WithName("V2_Role_Delete")
            .RequiresPermission("permission-role:write");

        return application;
    }

    // 403 for the realm:admin-conferral guard (audit H1) — named so the caller
    // knows exactly which grant they lack, consistent with PermissionEndpointFilter.
    private static IResult RealmAdminForbidden() => Results.Json(
        new
        {
            Error = "Role.RealmAdminForbidden",
            Message = "Only a realm administrator may create or modify a realm-admin role.",
        },
        statusCode: StatusCodes.Status403Forbidden);

    private static object MapToResponse(PermissionRole r) => new
    {
        Id = new ShortGuid(r.Id).ToString(),
        r.Name,
        r.Description,
        AppId = r.AppId is null ? null : new ShortGuid(r.AppId.Value).ToString(),
        r.IsRealmAdmin,
        PermissionIds = r.PermissionIds.Select(id => new ShortGuid(id).ToString()).ToList(),
    };

    /// <summary>
    /// Validates a payload and produces a <see cref="PermissionRole"/> ready
    /// to persist (without the Id, which is filled in by the caller). On
    /// failure returns a 400 result describing the first conflict found.
    /// </summary>
    private static async Task<(PermissionRole Role, IResult? Error)> BuildRoleAsync(
        RolePayload dto, IDocumentSession session)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            return (new PermissionRole(), Results.BadRequest(new
            {
                Error = "Role.NameRequired",
                Message = "Name is required.",
            }));
        }

        // A client may omit PermissionIds entirely (or send a stale/renamed
        // field); System.Text.Json then binds the record param to null. Coalesce
        // to empty so a malformed/partial payload yields a clean 400 below
        // (Role.GrantsNothing / Role.PermissionIdsRequireAppLink) instead of a
        // 500 NullReferenceException.
        var permissionIdsInput = dto.PermissionIds ?? new List<string>();

        // Resolve AppId (ShortGuid → Guid). Null payload = pure realm-admin role.
        Guid? appId = null;
        App? linkedApp = null;
        if (!string.IsNullOrEmpty(dto.AppId))
        {
            if (!ShortGuid.TryParse(dto.AppId, out Guid parsed))
            {
                return (new PermissionRole(), Results.BadRequest(new
                {
                    Error = "Role.InvalidAppId",
                    Message = $"AppId '{dto.AppId}' is not a valid Guid or ShortGuid.",
                }));
            }
            linkedApp = await session.LoadAsync<App>(parsed);
            if (linkedApp is null || linkedApp.IsDeleted)
            {
                return (new PermissionRole(), Results.BadRequest(new
                {
                    Error = "Role.AppNotFound",
                    Message = $"App {dto.AppId} not found.",
                }));
            }
            appId = parsed;
        }

        // PermissionIds without an App = invalid.
        if (appId is null && permissionIdsInput.Count > 0)
        {
            return (new PermissionRole(), Results.BadRequest(new
            {
                Error = "Role.PermissionIdsRequireAppLink",
                Message = "PermissionIds cannot be set on a role without an AppId.",
            }));
        }

        // Validate each permission id resolves to an entry in the linked App's catalog.
        var catalogIds = linkedApp?.Permissions.Select(p => p.Id).ToHashSet() ?? new HashSet<Guid>();
        var permissionIds = new List<Guid>(permissionIdsInput.Count);
        var seen = new HashSet<Guid>();
        foreach (var raw in permissionIdsInput)
        {
            if (!ShortGuid.TryParse(raw, out Guid permId))
            {
                return (new PermissionRole(), Results.BadRequest(new
                {
                    Error = "Role.InvalidPermissionId",
                    Message = $"PermissionId '{raw}' is not a valid Guid or ShortGuid.",
                }));
            }
            if (!catalogIds.Contains(permId))
            {
                return (new PermissionRole(), Results.BadRequest(new
                {
                    Error = "Role.PermissionIdNotInAppCatalog",
                    Message = $"PermissionId '{raw}' does not exist in App '{linkedApp!.Slug}'s catalog.",
                }));
            }
            if (seen.Add(permId)) permissionIds.Add(permId);
        }

        // A role with no AppId and no IsRealmAdmin grants nothing. Reject — admins
        // who type that almost certainly meant something else.
        if (appId is null && !dto.IsRealmAdmin)
        {
            return (new PermissionRole(), Results.BadRequest(new
            {
                Error = "Role.GrantsNothing",
                Message = "Role must either link to an App (AppId + PermissionIds) or set IsRealmAdmin=true.",
            }));
        }

        return (new PermissionRole
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            Description = dto.Description,
            AppId = appId,
            IsRealmAdmin = dto.IsRealmAdmin,
            PermissionIds = permissionIds,
        }, null);
    }
}
