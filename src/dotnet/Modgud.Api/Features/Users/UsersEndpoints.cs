using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using BuildingBlocks.Helper;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using Marten;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Api.Features.Users.Commands;
using Modgud.Authentication.Api.Users;
using Modgud.Api.Features.Users.Queries;
using Modgud.Application.DTOs.User;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;
using Modgud.Authentication.Sessions;
using Modgud.Domain.ValueObjects;

using Modgud.Infrastructure.Persistence.Marten.Projections.Users;
using Wolverine;

namespace Modgud.Api.Features.Users;

public record SetPasswordDto(string Password);
public record SetActiveDto(bool IsActive);
public record AddUserToGroupDto(string GroupId);

public static class UsersEndpoints
{
    public static WebApplication MapUsersEndpoints(this WebApplication application, string path)
    {
        var userGroup = application.MapGroup($"{path}/user")
            .WithTags("Users V2 (Marten)")
            .RequireAuthorization();

        // ── Lookup endpoints (any authenticated user) ──────────────────────

        userGroup.MapGet("lookup", async (IDocumentSession session) =>
            {
                var users = await session.Query<UserView>()
                    .Where(u => !u.IsDeleted && u.IsActive)
                    .ToListAsync();

                return Results.Ok(users
                    .OrderBy(u => u.Acronym ?? u.Firstname)
                    .Select(u => new
                    {
                        Id = new ShortGuid(u.Id).ToString(),
                        Label = u.Acronym ?? $"{u.Firstname} {u.Lastname}".Trim(),
                        u.UserName,
                    }));
            })
            .WithName("V2_User_Lookup");

        // ── Admin-only read endpoints ─────────────────────────────────────

        userGroup.MapGet("{id}", async (ShortGuid id, IMessageBus bus) =>
            {
                var query = new GetUserByIdQuery(id.Guid);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<UserDto>>(query);
                return result.ToResult();
            })
            .WithName("V2_User_GetById")
            .RequiresPermission("user:read");

        userGroup.MapGet("", async (IMessageBus bus, int? skip = null, int? take = null) =>
            {
                var query = new GetAllUsersQuery(Skip: skip, Take: take);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<List<UserDto>>>(query);
                return result.ToResult();
            })
            .WithName("V2_User_GetAll")
            .RequiresPermission("user:read");

        userGroup.MapPost("", async (IMessageBus bus, UserCreateDto createDto) =>
            {
                var command = new CreateUserCommand(
                    createDto.Firstname,
                    createDto.Lastname,
                    createDto.Acronym,
                    createDto.Email,
                    // Pass the username through raw (possibly blank). The handler
                    // applies the configurable (App⊕realm) registration-field policy:
                    // by default a blank username defaults to the email, but a realm
                    // that marks Username=Required rejects a blank one.
                    createDto.UserName ?? "",
                    createDto.Password,
                    createDto.EmailConfirmed,
                    createDto.IsActive,
                    createDto.GroupIds,
                    createDto.GracePeriodDaysOverride,
                    createDto.TwoFactorExempt);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<UserDto>>(command);
                return result.ToResult(dto =>
                {
                    dto.Status = EntityStatus.Pending;
                    return Results.Ok(dto);
                });
            })
            .WithName("V2_User_Create")
            .RequiresPermission("user:write");

        userGroup.MapPut("{id}", async (ShortGuid id, IMessageBus bus, UserUpdateDto dto,
            IDocumentSession session, IUserAccessRevoker accessRevoker, HttpContext context) =>
            {
                // 1. Update profile
                var command = new UpdateUserCommand(
                    id.Guid,
                    dto.Firstname,
                    dto.Lastname,
                    dto.Acronym,
                    dto.Email,
                    dto.UserName);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<UserDto>>(command);
                if (result.IsError) return result.ToResult();

                // 2. Toggle active if changed
                var deactivated = false;
                if (dto.IsActive.HasValue)
                {
                    var person = await session.LoadAsync<Person>(id.Guid);
                    if (person is not null && !person.IsDeleted)
                    {
                        var appUser = await session.LoadAsync<ApplicationUser>(id.Guid);
                        var wasActive = appUser?.IsActive ?? true;
                        if (appUser is not null)
                        {
                            appUser.IsActive = dto.IsActive.Value;
                            session.Store(appUser);
                        }
                        if (dto.IsActive.Value)
                            session.Events.Append(id.Guid, new UserActivatedEvent(id.Guid));
                        else
                        {
                            session.Events.Append(id.Guid, new UserDeactivatedEvent(id.Guid));
                            // Only a real active→inactive transition needs the kill
                            // switch — a no-op re-deactivate shouldn't churn the stamp.
                            deactivated = wasActive;
                        }
                    }
                }

                // 3. Admin override of EmailConfirmed — direct write to the
                //    ApplicationUser doc; not event-sourced, so SignalR push
                //    pulls the fresh value via SignalRProjectionDispatchHandler.
                if (dto.EmailConfirmed.HasValue)
                {
                    var appUser = await session.LoadAsync<ApplicationUser>(id.Guid);
                    if (appUser is not null && appUser.EmailConfirmed != dto.EmailConfirmed.Value)
                    {
                        appUser.EmailConfirmed = dto.EmailConfirmed.Value;
                        session.Store(appUser);
                    }
                    result.Value.EmailConfirmed = dto.EmailConfirmed.Value;
                }

                // Role management happens via Groups — no direct user→role assignments exist.

                await session.SaveChangesAsync();

                // Deactivation is a kill switch: revoke live access (OAuth grants +
                // sessions + cookie) AFTER the IsActive flip is committed. Consent
                // grants are kept (Deactivation reason) so reactivation is seamless.
                // No ct passed: the revoke deliberately runs to completion even if
                // the client disconnects (a kill switch must not be half-applied).
                if (deactivated)
                    await accessRevoker.RevokeAllAccessAsync(id.Guid, AccessRevocationReason.Deactivation);

                // Return optimistic result — SignalR will push the real update
                // after the async projection processes the events
                result.Value.Status = EntityStatus.Pending;
                return Results.Ok(result.Value);
            })
            .WithName("V2_User_Update")
            .RequiresPermission("user:write");

        userGroup.MapDelete("{id}", async (string id, IMessageBus bus, HttpContext context) =>
            {
                var guid = new ShortGuid(id).Guid;
                var command = new DeleteUsersCommand([guid], context.GetUserId());
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
                return result.ToNoContentResult();
            })
            .WithName("V2_User_DeleteSingle")
            .RequiresPermission("user:write");

        userGroup.MapDelete("", async ([FromBody] List<string> ids, IMessageBus bus, HttpContext context) =>
            {
                var guids = ids.Select(id => new ShortGuid(id).Guid).ToList();
                var command = new DeleteUsersCommand(guids, context.GetUserId());
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
                return result.ToNoContentResult();
            })
            .WithName("V2_User_Delete")
            .RequiresPermission("user:write");

        // Restore one user from the recycle bin (clear pending + reactivate).
        userGroup.MapPost("{id}/restore", async (string id, IMessageBus bus, HttpContext context) =>
            {
                var guid = new ShortGuid(id).Guid;
                var command = new RestoreUsersCommand([guid], context.GetUserId());
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
                return result.ToNoContentResult();
            })
            .WithName("V2_User_RestoreSingle")
            .RequiresPermission("user:write");

        // Bulk restore from the recycle bin.
        userGroup.MapPost("restore", async ([FromBody] List<string> ids, IMessageBus bus, HttpContext context) =>
            {
                var guids = ids.Select(id => new ShortGuid(id).Guid).ToList();
                var command = new RestoreUsersCommand(guids, context.GetUserId());
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
                return result.ToNoContentResult();
            })
            .WithName("V2_User_Restore")
            .RequiresPermission("user:write");

        // Set/Reset password for a user — delegates to the shared canonical
        // SetUserPasswordHandler (the path the realm-provisioning applier also uses).
        userGroup.MapPut("{id}/password", async (ShortGuid id, SetPasswordDto dto, SetUserPasswordHandler setPassword, CancellationToken ct) =>
            {
                var result = await setPassword.Handle(id.Guid, dto.Password, ct);
                if (!result.IsError) return Results.Ok(new { Message = "Password set successfully" });

                var error = result.FirstError;
                return error.Type == ErrorOr.ErrorType.NotFound
                    ? Results.NotFound(new { Message = "User not found" })
                    : Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                        title: "Password error", detail: error.Description);
            })
            .WithName("V2_User_SetPassword")
            .RequiresPermission("user:write");

        // Toggle user active/inactive
        userGroup.MapPut("{id}/active", async (ShortGuid id, SetActiveDto dto,
            IDocumentSession session, IUserAccessRevoker accessRevoker) =>
            {
                var person = await session.LoadAsync<Person>(id.Guid);
                if (person is null || person.IsDeleted)
                    return Results.NotFound(new { Message = "User not found" });

                var appUser = await session.LoadAsync<ApplicationUser>(id.Guid);
                var wasActive = appUser?.IsActive ?? true;
                if (appUser is not null)
                {
                    appUser.IsActive = dto.IsActive;
                    session.Store(appUser);
                }

                if (dto.IsActive)
                    session.Events.Append(id.Guid, new UserActivatedEvent(id.Guid));
                else
                    session.Events.Append(id.Guid, new UserDeactivatedEvent(id.Guid));

                await session.SaveChangesAsync();

                // Deactivation kills live access (tokens + sessions + cookie),
                // keeping consent grants so reactivation is seamless. Only on a real
                // active→inactive transition; runs to completion (no ct) as a kill switch.
                if (!dto.IsActive && wasActive)
                    await accessRevoker.RevokeAllAccessAsync(id.Guid, AccessRevocationReason.Deactivation);

                return Results.Ok(new { IsActive = dto.IsActive });
            })
            .WithName("V2_User_SetActive")
            .RequiresPermission("user:write");

        // ── Group membership — user-centric view and editing ──────────────

        // Returns the user's direct + inherited group memberships. "Via" carries
        // the direct group that pulled the inherited one into the set.
        userGroup.MapGet("{id}/groups", async (ShortGuid id, IDocumentSession session) =>
            {
                var person = await session.LoadAsync<Person>(id.Guid);
                if (person is null || person.IsDeleted)
                    return Results.NotFound(new { error = "User not found" });

                var allGroups = await session.Query<Group>()
                    .Where(g => !g.IsDeleted)
                    .ToListAsync();

                var directGroups = allGroups.Where(g => g.MemberIds.Contains(id.Guid)).ToList();
                var directIds = directGroups.Select(g => g.Id).ToHashSet();

                // BFS upward from direct groups — each hop carries the direct group
                // we entered through, so the UI can render "inherited via: <name>".
                var visited = new HashSet<Guid>(directIds);
                var queue = new Queue<(Guid currentId, Group via)>();
                foreach (var d in directGroups) queue.Enqueue((d.Id, d));

                var inherited = new List<(Group Group, Group Via)>();
                while (queue.Count > 0)
                {
                    var (currentId, via) = queue.Dequeue();
                    foreach (var parent in allGroups.Where(g => g.MemberIds.Contains(currentId)))
                    {
                        if (visited.Add(parent.Id))
                        {
                            inherited.Add((parent, via));
                            queue.Enqueue((parent.Id, via));
                        }
                    }
                }

                return Results.Ok(new
                {
                    Direct = directGroups
                        .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                        .Select(g => new
                        {
                            Id = new ShortGuid(g.Id).ToString(),
                            g.Name,
                            g.Description,
                            IsAuto = g.MembershipMode == MembershipMode.Auto,
                        }),
                    Inherited = inherited
                        .OrderBy(x => x.Group.Name, StringComparer.CurrentCultureIgnoreCase)
                        .Select(x => new
                        {
                            Id = new ShortGuid(x.Group.Id).ToString(),
                            Name = x.Group.Name,
                            Description = x.Group.Description,
                            IsAuto = x.Group.MembershipMode == MembershipMode.Auto,
                            ViaId = new ShortGuid(x.Via.Id).ToString(),
                            ViaName = x.Via.Name,
                        }),
                });
            })
            .WithName("V2_User_GetGroups")
            .RequiresPermission("user:read");

        // Admin debug surface: returns the live effective group membership of a
        // user — direct + inherited (manual) + auto-script matches — independent
        // of whether MemberIds is materialized. The `MaterializedMatches=false`
        // flag on AutoMatched rows is the gold debug signal: "the script would
        // match but the user isn't in MemberIds — somebody never recomputed."
        userGroup.MapGet("{id}/effective-groups", async (
            ShortGuid id, IDocumentSession session, IEffectiveGroupsResolver resolver) =>
            {
                var person = await session.LoadAsync<Person>(id.Guid);
                if (person is null || person.IsDeleted)
                    return Results.NotFound(new { error = "User not found" });

                var result = await resolver.ResolveAsync(id.Guid);

                return Results.Ok(new
                {
                    PrincipalId = new ShortGuid(result.PrincipalId).ToString(),
                    Groups = result.Groups.Select(g => new
                    {
                        Id = new ShortGuid(g.Id).ToString(),
                        g.Name,
                        g.Description,
                        Roles = g.Roles.Select(r => new
                        {
                            Id = new ShortGuid(r.Id).ToString(),
                            r.Name,
                        }),
                        Source = g.Source.ToString(),
                        Via = g.Via?.Select(v => new
                        {
                            Id = new ShortGuid(v.Id).ToString(),
                            v.Name,
                        }),
                        g.MaterializedMatches,
                    }),
                    Diagnostics = result.Diagnostics.Select(d => new
                    {
                        GroupId = new ShortGuid(d.GroupId).ToString(),
                        d.GroupName,
                        Kind = d.Kind.ToString(),
                        d.Error,
                    }),
                });
            })
            .WithName("V2_User_GetEffectiveGroups")
            .RequiresPermission("user:read");

        // Add the user to a group. Auto-groups reject the add — membership is
        // script-driven and a manual add would be overwritten on next recompute.
        userGroup.MapPost("{id}/groups", async (ShortGuid id, AddUserToGroupDto dto, IDocumentSession session) =>
            {
                var person = await session.LoadAsync<Person>(id.Guid);
                if (person is null || person.IsDeleted)
                    return Results.NotFound(new { error = "User not found" });

                var groupId = new ShortGuid(dto.GroupId).Guid;
                var group = await session.LoadAsync<Group>(groupId);
                if (group is null || group.IsDeleted)
                    return Results.NotFound(new { error = "Group not found" });

                if (group.MembershipMode == MembershipMode.Auto)
                    return Results.BadRequest(new { error = "Cannot add members to an auto-membership group." });

                if (group.MemberIds.Contains(id.Guid))
                    return Results.NoContent(); // idempotent

                var newMemberIds = group.MemberIds.Append(id.Guid).ToList();
                session.Events.Append(groupId, new GroupUpdatedEvent(
                    groupId, group.Name, group.Description,
                    newMemberIds, group.RoleIds,
                    group.MembershipMode, group.MembershipScript, group.CompiledMembershipScript,
                    group.MembershipScriptDependencies,
                    group.Email, group.EmailMode,
                    BoundTo: group.BoundTo,
                    ExternallyDrivable: group.ExternallyDrivable));
                await session.SaveChangesAsync();
                return Results.NoContent();
            })
            .WithName("V2_User_AddGroup")
            .RequiresPermission("user:write");

        // Remove the user from a group. Only affects direct membership — inherited
        // groups must be edited at the source.
        userGroup.MapDelete("{id}/groups/{groupId}", async (ShortGuid id, ShortGuid groupId, IDocumentSession session) =>
            {
                var person = await session.LoadAsync<Person>(id.Guid);
                if (person is null || person.IsDeleted)
                    return Results.NotFound(new { error = "User not found" });

                var group = await session.LoadAsync<Group>(groupId.Guid);
                if (group is null || group.IsDeleted)
                    return Results.NotFound(new { error = "Group not found" });

                if (group.MembershipMode == MembershipMode.Auto)
                    return Results.BadRequest(new { error = "Cannot remove members from an auto-membership group." });

                if (!group.MemberIds.Contains(id.Guid))
                    return Results.NoContent(); // idempotent

                var newMemberIds = group.MemberIds.Where(m => m != id.Guid).ToList();
                session.Events.Append(groupId.Guid, new GroupUpdatedEvent(
                    groupId.Guid, group.Name, group.Description,
                    newMemberIds, group.RoleIds,
                    group.MembershipMode, group.MembershipScript, group.CompiledMembershipScript,
                    group.MembershipScriptDependencies,
                    group.Email, group.EmailMode,
                    BoundTo: group.BoundTo,
                    ExternallyDrivable: group.ExternallyDrivable));
                await session.SaveChangesAsync();
                return Results.NoContent();
            })
            .WithName("V2_User_RemoveGroup")
            .RequiresPermission("user:write");

        return application;
    }
}
