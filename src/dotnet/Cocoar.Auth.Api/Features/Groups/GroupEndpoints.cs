using BuildingBlocks.Helper;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.AspNetCore;
using Cocoar.Auth.Authorization.Commands;
using Cocoar.Auth.Authorization.Events;
using Cocoar.Auth.Authorization.Principals;
using ErrorOr;
using Marten;
using Wolverine;

namespace Cocoar.Auth.Api.Features.Groups;

public record CreateGroupDto(
    string Name,
    string? Description,
    List<string> MemberIds,
    List<string> RoleIds,
    List<AccessScriptDto> AccessScripts,
    MembershipMode MembershipMode = MembershipMode.Manual,
    string? MembershipScript = null,
    string? Email = null,
    EmailMode EmailMode = EmailMode.Shared,
    List<string>? BoundTo = null);

public record AccessScriptDto(string ResourceType, string? Script);

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
                    var person = p as Cocoar.Auth.Authorization.Principals.Person;
                    return new
                    {
                        Id = new ShortGuid(p.Id).ToString(),
                        Label = p.DisplayName,
                        Type = p.Type,
                        UserName = (p as Cocoar.Auth.Authorization.Principals.Person)?.AccountName,
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

        groupGroup.MapPost("", async (CreateGroupDto dto, IMessageBus bus) =>
            {
                // BoundTo defaults to [cocoar-auth] on create — the only app
                // that exists in Phase 1. UI will override once additional
                // apps can be registered.
                var boundTo = dto.BoundTo ?? [AppSlugs.CocoarAuth];
                var command = new CreateGroupCommand(
                    dto.Name, dto.Description,
                    dto.MemberIds.Select(m => new ShortGuid(m).Guid).ToList(),
                    dto.RoleIds.Select(r => new ShortGuid(r).Guid).ToList(),
                    dto.AccessScripts.Select(s => new AccessScriptInput(s.ResourceType, s.Script)).ToList(),
                    dto.MembershipMode, dto.MembershipScript,
                    dto.Email, dto.EmailMode,
                    boundTo);
                var result = await bus.InvokeAsync<ErrorOr<Group>>(command);
                return result.Match<IResult>(
                    group => Results.Ok(MapToResponse(group)),
                    errors => Results.BadRequest(new { Errors = errors.Select(e => new { e.Code, e.Description }) }));
            })
            .WithName("V2_Group_Create")
            .RequiresPermission("authorization-group:write");

        groupGroup.MapPut("{id}", async (ShortGuid id, CreateGroupDto dto, IMessageBus bus) =>
            {
                // On update, null BoundTo means "keep what's stored" — the
                // command handler reads the current value if not supplied.
                var command = new UpdateGroupCommand(
                    id.Guid, dto.Name, dto.Description,
                    dto.MemberIds.Select(m => new ShortGuid(m).Guid).ToList(),
                    dto.RoleIds.Select(r => new ShortGuid(r).Guid).ToList(),
                    dto.AccessScripts.Select(s => new AccessScriptInput(s.ResourceType, s.Script)).ToList(),
                    dto.MembershipMode, dto.MembershipScript,
                    dto.Email, dto.EmailMode,
                    dto.BoundTo);
                var result = await bus.InvokeAsync<ErrorOr<Group>>(command);
                return result.Match<IResult>(
                    group => Results.Ok(MapToResponse(group)),
                    errors => errors.Any(e => e.Type == ErrorType.NotFound)
                        ? Results.NotFound()
                        : Results.BadRequest(new { Errors = errors.Select(e => new { e.Code, e.Description }) }));
            })
            .WithName("V2_Group_Update")
            .RequiresPermission("authorization-group:write");

        groupGroup.MapDelete("{id}", async (ShortGuid id, IDocumentSession session) =>
            {
                var group = await session.LoadAsync<Group>(id.Guid);
                if (group is null || group.IsDeleted) return Results.NotFound();
                group.IsDeleted = true;
                session.Events.Append(id.Guid, new GroupDeletedEvent(id.Guid));
                await session.SaveChangesAsync();
                return Results.NoContent();
            })
            .WithName("V2_Group_Delete")
            .RequiresPermission("authorization-group:write");

        return application;
    }

    private static object MapToResponse(Group g) => new
    {
        Id = new ShortGuid(g.Id).ToString(),
        g.Name,
        g.Description,
        MemberIds = g.MemberIds.Select(id => new ShortGuid(id).ToString()),
        RoleIds = g.RoleIds.Select(id => new ShortGuid(id).ToString()),
        AccessScripts = g.AccessScripts.Select(s => new { s.ResourceType, s.Script }),
        MembershipMode = g.MembershipMode.ToString(),
        g.MembershipScript,
        g.MembershipLastError,
        g.Email,
        EmailMode = g.EmailMode.ToString(),
        g.BoundTo,
    };
}
