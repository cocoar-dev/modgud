using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using BuildingBlocks.Helper;
using Cocoar.Auth.Authorization.AspNetCore;
using Cocoar.Auth.Authorization.Principals;
using Marten;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Cocoar.Auth.Api.Features.Users.Commands;
using Cocoar.Auth.Authentication.Api.Users;
using Cocoar.Auth.Api.Features.Users.Queries;
using Cocoar.Auth.Application.DTOs.User;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Events;
using Cocoar.Auth.Domain.ValueObjects;

using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.Users;
using Wolverine;

namespace Cocoar.Auth.Api.Features.Users;

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
            .RequiresPermission("app:admin");

        userGroup.MapGet("", async (IMessageBus bus, int? skip = null, int? take = null) =>
            {
                var query = new GetAllUsersQuery(Skip: skip, Take: take);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<List<UserDto>>>(query);
                return result.ToResult();
            })
            .WithName("V2_User_GetAll")
            .RequiresPermission("app:admin");

        userGroup.MapPost("", async (IMessageBus bus, UserCreateDto createDto) =>
            {
                var command = new CreateUserCommand(
                    createDto.Firstname,
                    createDto.Lastname,
                    createDto.Acronym,
                    createDto.Email,
                    !string.IsNullOrWhiteSpace(createDto.UserName) ? createDto.UserName : createDto.Acronym ?? "",
                    createDto.Password);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<UserDto>>(command);
                return result.ToResult(dto =>
                {
                    dto.Status = EntityStatus.Pending;
                    return Results.Ok(dto);
                });
            })
            .WithName("V2_User_Create")
            .RequiresPermission("app:admin");

        userGroup.MapPut("{id}", async (ShortGuid id, IMessageBus bus, UserUpdateDto dto,
            IDocumentSession session, HttpContext context) =>
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
                if (dto.IsActive.HasValue)
                {
                    var person = await session.LoadAsync<Person>(id.Guid);
                    if (person is not null && !person.IsDeleted)
                    {
                        var appUser = await session.LoadAsync<ApplicationUser>(id.Guid);
                        if (appUser is not null)
                        {
                            appUser.IsActive = dto.IsActive.Value;
                            session.Store(appUser);
                        }
                        if (dto.IsActive.Value)
                            session.Events.Append(id.Guid, new UserActivatedEvent(id.Guid));
                        else
                            session.Events.Append(id.Guid, new UserDeactivatedEvent(id.Guid));
                    }
                }

                // Role management happens via Groups — no direct user→role assignments exist.

                await session.SaveChangesAsync();

                // Return optimistic result — SignalR will push the real update
                // after the async projection processes the events
                result.Value.Status = EntityStatus.Pending;
                return Results.Ok(result.Value);
            })
            .WithName("V2_User_Update")
            .RequiresPermission("app:admin");

        userGroup.MapDelete("{id}", async (string id, IMessageBus bus) =>
            {
                var guid = new ShortGuid(id).Guid;
                var command = new DeleteUsersCommand(new List<Guid> { guid });
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
                return result.ToNoContentResult();
            })
            .WithName("V2_User_DeleteSingle")
            .RequiresPermission("app:admin");

        userGroup.MapDelete("", async ([FromBody] List<string> ids, IMessageBus bus) =>
            {
                var guids = ids.Select(id => new ShortGuid(id).Guid).ToList();
                var command = new DeleteUsersCommand(guids);
                var result = await bus.InvokeAsync<ErrorOr.ErrorOr<ErrorOr.Success>>(command);
                return result.ToNoContentResult();
            })
            .WithName("V2_User_Delete")
            .RequiresPermission("app:admin");

        // Set/Reset password for a user
        userGroup.MapPut("{id}/password", async (ShortGuid id, SetPasswordDto dto, IDocumentSession session, UserManager<ApplicationUser> userManager) =>
            {
                var person = await session.LoadAsync<Person>(id.Guid);
                if (person is null || person.IsDeleted)
                    return Results.NotFound(new { Message = "User not found" });

                var appUser = await userManager.FindByIdAsync(id.Guid.ToString());
                if (appUser is null)
                {
                    // Create ApplicationUser if it doesn't exist yet
                    appUser = new ApplicationUser(person.AccountName ?? person.Acronym ?? person.Id.ToString(), person.Email)
                    {
                        Id = person.Id,
                        Firstname = person.Firstname,
                        Lastname = person.Lastname,
                        Acronym = person.Acronym,
                        IsActive = person.IsActive
                    };
                    var createResult = await userManager.CreateAsync(appUser, dto.Password);
                    if (!createResult.Succeeded)
                        return Results.Problem(
                            statusCode: StatusCodes.Status400BadRequest,
                            title: "Password error",
                            detail: string.Join("; ", createResult.Errors.Select(e => e.Description)));
                }
                else
                {
                    await userManager.RemovePasswordAsync(appUser);
                    var addResult = await userManager.AddPasswordAsync(appUser, dto.Password);
                    if (!addResult.Succeeded)
                        return Results.Problem(
                            statusCode: StatusCodes.Status400BadRequest,
                            title: "Password error",
                            detail: string.Join("; ", addResult.Errors.Select(e => e.Description)));
                }

                session.Events.Append(id.Guid, new UserPasswordChangedEvent(id.Guid, null));
                await session.SaveChangesAsync();

                return Results.Ok(new { Message = "Password set successfully" });
            })
            .WithName("V2_User_SetPassword")
            .RequiresPermission("app:admin");

        // Toggle user active/inactive
        userGroup.MapPut("{id}/active", async (ShortGuid id, SetActiveDto dto, IDocumentSession session) =>
            {
                var person = await session.LoadAsync<Person>(id.Guid);
                if (person is null || person.IsDeleted)
                    return Results.NotFound(new { Message = "User not found" });

                var appUser = await session.LoadAsync<ApplicationUser>(id.Guid);
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

                return Results.Ok(new { IsActive = dto.IsActive });
            })
            .WithName("V2_User_SetActive")
            .RequiresPermission("app:admin");

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
            .RequiresPermission("app:admin");

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
                    newMemberIds, group.RoleIds, group.AccessScripts,
                    group.MembershipMode, group.MembershipScript, group.CompiledMembershipScript,
                    group.MembershipScriptDependencies,
                    group.Email, group.EmailMode));
                await session.SaveChangesAsync();
                return Results.NoContent();
            })
            .WithName("V2_User_AddGroup")
            .RequiresPermission("app:admin");

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
                    newMemberIds, group.RoleIds, group.AccessScripts,
                    group.MembershipMode, group.MembershipScript, group.CompiledMembershipScript,
                    group.MembershipScriptDependencies,
                    group.Email, group.EmailMode));
                await session.SaveChangesAsync();
                return Results.NoContent();
            })
            .WithName("V2_User_RemoveGroup")
            .RequiresPermission("app:admin");

        return application;
    }
}
