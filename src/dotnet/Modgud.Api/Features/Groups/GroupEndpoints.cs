using BuildingBlocks.Helper;
using Modgud.Api.Authorization;
using Modgud.Authorization.Apps;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Commands;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using ErrorOr;
using Marten;
using Wolverine;

namespace Modgud.Api.Features.Groups;

public record CreateGroupDto(
    string Name,
    string? Description,
    List<string> MemberIds,
    List<string> RoleIds,
    MembershipMode MembershipMode = MembershipMode.Manual,
    string? MembershipScript = null,
    string? Email = null,
    EmailMode EmailMode = EmailMode.Shared,
    List<string>? BoundTo = null,
    bool ExternallyDrivable = false);

public static class GroupEndpoints
{
    public static WebApplication MapGroupEndpoints(this WebApplication application, string path)
    {
        var groupGroup = application.MapGroup($"{path}/group")
            .WithTags("Groups")
            .RequireAuthorization();

        // ── Lookup (any authenticated user) ──────────────────────────────
        groupGroup.MapGet("lookup", async (IDocumentSession session) =>
            {
                var groups = await session.Query<Group>()
                    .Where(g => !g.IsDeleted)
                    .OrderBy(g => g.Name)
                    .ToListAsync();

                return Results.Ok(groups.Select(g => new { Id = new ShortGuid(g.Id).ToString(), g.Name }));
            })
            .WithName("V2_Group_Lookup");

        // ── Admin-only endpoints ─────────────────────────────────────────

        groupGroup.MapGet("", async (IDocumentSession session) =>
            {
                var groups = await session.Query<Group>()
                    .Where(g => !g.IsDeleted)
                    .OrderBy(g => g.Name)
                    .ToListAsync();

                return Results.Ok(groups.Select(MapToResponse));
            })
            .WithName("V2_Group_GetAll")
            .RequiresPermission("authorization-group:read");

        groupGroup.MapGet("{id}", async (ShortGuid id, IDocumentSession session) =>
            {
                var group = await session.LoadAsync<Group>(id.Guid);
                if (group is null || group.IsDeleted) return Results.NotFound();
                return Results.Ok(MapToResponse(group));
            })
            .WithName("V2_Group_GetById")
            .RequiresPermission("authorization-group:read");

        // Effective members — direct principals + nested (transitively resolved)
        // via sub-groups. Each nested entry carries the first direct member-group
        // it was reached through, so the UI can show "via: <name>".
        groupGroup.MapGet("{id}/effective-members", async (ShortGuid id, IDocumentSession session) =>
            {
                var group = await session.LoadAsync<Group>(id.Guid);
                if (group is null || group.IsDeleted) return Results.NotFound();

                var allGroups = (await session.Query<Group>()
                    .Where(g => !g.IsDeleted)
                    .ToListAsync()).ToDictionary(g => g.Id);
                var principals = (await session.Query<Principal>()
                    .Where(p => !p.IsDeleted)
                    .ToListAsync()).ToDictionary(p => p.Id);

                object MapPrincipal(Principal p, string? viaId = null, string? viaName = null)
                {
                    var person = p as Modgud.Authorization.Principals.Person;
                    return new
                    {
                        Id = new ShortGuid(p.Id).ToString(),
                        Label = p.DisplayName,
                        Type = p.Type,
                        UserName = (p as Modgud.Authorization.Principals.Person)?.AccountName,
                        Firstname = person?.Firstname,
                        Lastname = person?.Lastname,
                        Acronym = person?.Acronym,
                        Description = p is Group g && allGroups.TryGetValue(p.Id, out var sub)
                            ? sub.Description
                            : null,
                        ViaId = viaId,
                        ViaName = viaName,
                    };
                }

                var direct = group.MemberIds
                    .Where(principals.ContainsKey)
                    .Select(mId => MapPrincipal(principals[mId]))
                    .ToList();

                // BFS through sub-groups collecting persons not already in the direct set.
                var visited = new HashSet<Guid>(group.MemberIds);
                var nested = new List<object>();
                var queue = new Queue<(Guid subGroupId, Group via)>();
                foreach (var mId in group.MemberIds)
                    if (allGroups.TryGetValue(mId, out var sub)) queue.Enqueue((mId, sub));

                while (queue.Count > 0)
                {
                    var (currentId, via) = queue.Dequeue();
                    if (!allGroups.TryGetValue(currentId, out var current)) continue;
                    foreach (var memberId in current.MemberIds)
                    {
                        if (!visited.Add(memberId)) continue;
                        if (allGroups.TryGetValue(memberId, out var subGroup))
                        {
                            // Still a group — keep traversing, "via" stays at the first hop.
                            queue.Enqueue((memberId, via));
                            // Also include the sub-group itself as nested member (structural info).
                            nested.Add(MapPrincipal(principals[memberId],
                                new ShortGuid(via.Id).ToString(), via.Name));
                        }
                        else if (principals.TryGetValue(memberId, out var p))
                        {
                            nested.Add(MapPrincipal(p,
                                new ShortGuid(via.Id).ToString(), via.Name));
                        }
                    }
                }

                return Results.Ok(new { Direct = direct, Nested = nested });
            })
            .WithName("V2_Group_EffectiveMembers")
            .RequiresPermission("authorization-group:read");

        groupGroup.MapPost("", async (CreateGroupDto dto, HttpContext http, IPermissionService perms, IMessageBus bus) =>
            {
                // BoundTo defaults to [modgud] on create — the only app
                // that exists in Phase 1. UI will override once additional
                // apps can be registered.
                var boundTo = dto.BoundTo ?? [AppSlugs.Modgud];
                var callerIsRealmAdmin = await CallerPermissions.IsRealmAdminAsync(http, perms);
                var command = new CreateGroupCommand(
                    dto.Name, dto.Description,
                    dto.MemberIds.Select(m => new ShortGuid(m).Guid).ToList(),
                    dto.RoleIds.Select(r => new ShortGuid(r).Guid).ToList(),
                    dto.MembershipMode, dto.MembershipScript,
                    dto.Email, dto.EmailMode,
                    boundTo, dto.ExternallyDrivable, callerIsRealmAdmin);
                var result = await bus.InvokeAsync<ErrorOr<Group>>(command);
                return result.Match<IResult>(
                    group => Results.Ok(MapToResponse(group)),
                    ToErrorResult);
            })
            .WithName("V2_Group_Create")
            .RequiresPermission("authorization-group:write");

        groupGroup.MapPut("{id}", async (ShortGuid id, CreateGroupDto dto, HttpContext http, IPermissionService perms, IMessageBus bus) =>
            {
                // On update, null BoundTo means "keep what's stored" — the
                // command handler reads the current value if not supplied.
                var callerIsRealmAdmin = await CallerPermissions.IsRealmAdminAsync(http, perms);
                var command = new UpdateGroupCommand(
                    id.Guid, dto.Name, dto.Description,
                    dto.MemberIds.Select(m => new ShortGuid(m).Guid).ToList(),
                    dto.RoleIds.Select(r => new ShortGuid(r).Guid).ToList(),
                    dto.MembershipMode, dto.MembershipScript,
                    dto.Email, dto.EmailMode,
                    dto.BoundTo, dto.ExternallyDrivable, callerIsRealmAdmin);
                var result = await bus.InvokeAsync<ErrorOr<Group>>(command);
                return result.Match<IResult>(
                    group => Results.Ok(MapToResponse(group)),
                    ToErrorResult);
            })
            .WithName("V2_Group_Update")
            .RequiresPermission("authorization-group:write");

        // Delete delegates to the shared DeleteGroupCommand — the same canonical path the
        // realm-provisioning prune calls (mirrors create/update; no longer endpoint-inline).
        groupGroup.MapDelete("{id}", async (ShortGuid id, IMessageBus bus) =>
            {
                var result = await bus.InvokeAsync<ErrorOr<Success>>(new DeleteGroupCommand(id.Guid));
                return result.Match<IResult>(_ => Results.NoContent(), ToErrorResult);
            })
            .WithName("V2_Group_Delete")
            .RequiresPermission("authorization-group:write");

        return application;
    }

    // Maps command errors to HTTP: NotFound→404, Forbidden (realm:admin
    // conferral guard, audit H1)→403, everything else→400.
    private static IResult ToErrorResult(List<Error> errors)
    {
        if (errors.Any(e => e.Type == ErrorType.NotFound))
            return Results.NotFound();

        var status = errors.Any(e => e.Type == ErrorType.Forbidden)
            ? StatusCodes.Status403Forbidden
            : StatusCodes.Status400BadRequest;
        return Results.Json(
            new { Errors = errors.Select(e => new { e.Code, e.Description }) },
            statusCode: status);
    }

    private static object MapToResponse(Group g) => new
    {
        Id = new ShortGuid(g.Id).ToString(),
        g.Name,
        g.Description,
        MemberIds = g.MemberIds.Select(id => new ShortGuid(id).ToString()),
        RoleIds = g.RoleIds.Select(id => new ShortGuid(id).ToString()),
        MembershipMode = g.MembershipMode.ToString(),
        g.MembershipScript,
        g.MembershipLastError,
        g.Email,
        EmailMode = g.EmailMode.ToString(),
        g.BoundTo,
        g.ExternallyDrivable,
    };
}
