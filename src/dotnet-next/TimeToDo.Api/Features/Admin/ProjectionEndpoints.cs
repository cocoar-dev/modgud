using System.Linq.Expressions;
using TimeToDo.Authorization.Access;
using TimeToDo.Authorization.AspNetCore;
using TimeToDo.Authorization.Principals;
using TimeToDo.Authorization.Projections;
using Marten;
using Marten.Events.Daemon.Coordination;
using TimeToDo.Authentication.Domain;
using TimeToDo.Infrastructure.AccessPolicy;
using TimeToDo.Infrastructure.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Authentication.Projections;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Users;

namespace TimeToDo.Api.Features.Admin;

public static class ProjectionEndpoints
{
    public static WebApplication MapProjectionEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/projections")
            .WithTags("Admin Projections")
            .RequireAuthorization()
            .RequiresPermission("app:admin");

        group.MapPost("rebuild", async (
            IDocumentStore store,
            IProjectionCoordinator coordinator,
            CancellationToken ct) =>
        {
            Serilog.Log.Information("Admin: Starting full projection rebuild");
            try
            {
                await coordinator.PauseAsync();

                var wasEnabled = ProjectionSideEffects.Enabled;
                ProjectionSideEffects.Enabled = false;

                try
                {
                    // Async composite (UserView/CustomerView/TodoView/CommentView etc.) —
                    // explicit deletes here cover related docs the projection writes via
                    // side effects (CommentReadStatusDocument) which RebuildProjectionAsync
                    // wouldn't drop on its own.
                    await using var session = store.LightweightSession();
                    session.DeleteWhere<UserView>(x => true);
                    session.DeleteWhere<CustomerView>(x => true);
                    session.DeleteWhere<TodoView>(x => true);
                    session.DeleteWhere<CommentView>(x => true);
                    session.DeleteWhere<CommentReadStatusDocument>(x => true);
                    await session.SaveChangesAsync(ct);

                    using var daemon = await store.BuildProjectionDaemonAsync();
                    var timeout = TimeSpan.FromMinutes(10);

                    await daemon.RebuildProjectionAsync("ViewProjections", timeout, ct);

                    // Inline projections — RebuildProjectionAsync<T> drops the produced
                    // documents itself and replays from event 0. Same path the
                    // `recover rebuild-projections` CLI uses for first-migration bootstrap;
                    // this endpoint is the maintenance equivalent (when an admin is logged in).
                    await daemon.RebuildProjectionAsync<TimeToDoPrincipalProjection>(timeout, ct);
                    await daemon.RebuildProjectionAsync<PermissionRoleProjection>(timeout, ct);
                }
                finally
                {
                    ProjectionSideEffects.Enabled = wasEnabled;
                    await coordinator.ResumeAsync();
                }

                Serilog.Log.Information("Admin: Full projection rebuild completed");
                return Results.Ok(new { Message = "All projections rebuilt successfully" });
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Admin: Projection rebuild failed");
                return Results.Json(new { Message = ex.Message }, statusCode: 500);
            }
        })
        .WithName("AdminProjections_Rebuild");

        // Consistency check — reports drift between authoritative sources and derived state.
        //
        // Source of truth:
        //   • ApplicationUser documents   → should have a Person PrincipalDirectory
        //   • Group docs     → should have a Group PrincipalDirectory
        //   • PermissionRole docs         → referenced by groups
        //
        // Checks (drift / orphans / dangling references):
        //   A. PrincipalValidation drift — source count vs. projection count, per type
        //   B. Dangling principal refs in groups' MemberIds
        //   C. Dangling role refs in groups' RoleIds
        //   D. Dangling principal refs in todos' Responsibles / CreatedBy / UpdatedBy
        //   E. Nested-group cycles (defensive — UpdateGroupCommand prevents creation but
        //       historical data or direct DB edits could introduce one)
        group.MapGet("consistency-check", async (
            IQuerySession session,
            IMembershipEvaluator membershipEvaluator,
            CancellationToken ct) =>
        {
            var applicationUsers = await session.Query<ApplicationUser>()
                .Where(u => !u.IsDeleted)
                .ToListAsync(ct);
            var appUserIds = applicationUsers.Select(u => u.Id).ToHashSet();

            var groups = (await session.Query<Group>()
                .Where(g => !g.IsDeleted)
                .ToListAsync(ct)).ToList();
            var groupIds = groups.Select(g => g.Id).ToHashSet();

            var principals = await session.Query<Principal>()
                .Where(p => !p.IsDeleted)
                .ToListAsync(ct);
            var principalIds = principals.Select(p => p.Id).ToHashSet();
            var principalById = principals.ToDictionary(p => p.Id);

            var roles = await session.Query<PermissionRole>()
                .Where(r => !r.IsDeleted)
                .ToListAsync(ct);
            var roleIds = roles.Select(r => r.Id).ToHashSet();

            // A. PrincipalValidation drift
            var missingPersonPrincipals = appUserIds.Where(id => !principalIds.Contains(id)).ToList();
            var orphanPersonPrincipals = principals
                .Where(p => p is Person && !appUserIds.Contains(p.Id))
                .Select(p => p.Id)
                .ToList();

            var missingGroupPrincipals = groupIds.Where(id => !principalIds.Contains(id)).ToList();
            var orphanGroupPrincipals = principals
                .Where(p => p is Group && !groupIds.Contains(p.Id))
                .Select(p => p.Id)
                .ToList();

            // B. Dangling MemberIds in groups
            var danglingMembers = groups
                .SelectMany(g => g.MemberIds
                    .Where(mid => !principalIds.Contains(mid))
                    .Select(mid => new { GroupId = g.Id, GroupName = g.Name, MemberId = mid }))
                .ToList();

            // C. Dangling RoleIds in groups
            var danglingRoles = groups
                .SelectMany(g => g.RoleIds
                    .Where(rid => !roleIds.Contains(rid))
                    .Select(rid => new { GroupId = g.Id, GroupName = g.Name, RoleId = rid }))
                .ToList();

            // D. Dangling principal refs in todos
            var todos = await session.Query<TodoView>()
                .Where(t => !t.IsDeleted)
                .ToListAsync(ct);

            var danglingResponsibles = todos
                .SelectMany(t => t.Responsibles
                    .Where(r => !principalIds.Contains(r.Id))
                    .Select(r => new { TodoId = t.Id, TodoTitle = t.Title, PrincipalId = r.Id, r.Label }))
                .ToList();

            var danglingCreators = todos
                .Where(t => t.CreatedBy != null && !principalIds.Contains(t.CreatedBy.Id))
                .Select(t => new { TodoId = t.Id, TodoTitle = t.Title, PrincipalId = t.CreatedBy!.Id })
                .ToList();

            // E. Nested-group cycles
            var cycles = DetectCycles(groups);

            // F. Auto-group membership drift — re-evaluate each auto-group's predicate
            //    against PrincipalDirectory and compare to the materialized MemberIds.
            //    Mismatches indicate the recalc pipeline missed an event (e.g. a user was
            //    created/updated but the async handler failed or wasn't triggered).
            var autoDrift = new List<AutoGroupDrift>();
            foreach (var autoGroup in groups.Where(g => g.MembershipMode == MembershipMode.Auto
                                                    && !string.IsNullOrWhiteSpace(g.CompiledMembershipScript)))
            {
                Expression<Func<Principal, bool>>? predicate = null;
                try
                {
                    predicate = membershipEvaluator.BuildPredicate<Principal>(autoGroup.CompiledMembershipScript!);
                }
                catch
                {
                    autoDrift.Add(new AutoGroupDrift(autoGroup.Id, autoGroup.Name,
                        ScriptError: true, MissingMembers: [], ExtraMembers: []));
                    continue;
                }

                var expectedMembers = principals
                    .AsQueryable()
                    .Where(p => !p.IsDeleted)
                    .Where(predicate)
                    .Select(p => p.Id)
                    .ToHashSet();
                var actualMembers = autoGroup.MemberIds.ToHashSet();

                var missing = expectedMembers.Except(actualMembers).ToList();
                var extra = actualMembers.Except(expectedMembers).ToList();

                if (missing.Count > 0 || extra.Count > 0)
                {
                    autoDrift.Add(new AutoGroupDrift(autoGroup.Id, autoGroup.Name,
                        ScriptError: false, MissingMembers: missing, ExtraMembers: extra));
                }
            }

            var ok = missingPersonPrincipals.Count == 0
                  && orphanPersonPrincipals.Count == 0
                  && missingGroupPrincipals.Count == 0
                  && orphanGroupPrincipals.Count == 0
                  && danglingMembers.Count == 0
                  && danglingRoles.Count == 0
                  && danglingResponsibles.Count == 0
                  && danglingCreators.Count == 0
                  && cycles.Count == 0
                  && autoDrift.Count == 0;

            return Results.Ok(new
            {
                Status = ok ? "OK" : "ISSUES_FOUND",
                Totals = new
                {
                    ApplicationUsers = applicationUsers.Count,
                    Groups = groups.Count,
                    PrincipalsTotal = principals.Count,
                    PrincipalsPerson = principals.Count(p => p is Person),
                    PrincipalsGroup = principals.Count(p => p is Group),
                    Roles = roles.Count,
                    Todos = todos.Count,
                },
                PrincipalValidation = new
                {
                    MissingPerson = missingPersonPrincipals,
                    OrphanPerson = orphanPersonPrincipals,
                    MissingGroup = missingGroupPrincipals,
                    OrphanGroup = orphanGroupPrincipals,
                },
                DanglingReferences = new
                {
                    MembersInGroups = danglingMembers,
                    RolesInGroups = danglingRoles,
                    ResponsiblesInTodos = danglingResponsibles,
                    CreatorsInTodos = danglingCreators,
                },
                GroupCycles = cycles,
                AutoGroupDrift = autoDrift,
            });
        })
        .WithName("AdminProjections_ConsistencyCheck");

        return application;
    }

    private record GroupRef(Guid Id, string Name);
    private record CycleReport(List<GroupRef> Groups);
    private record AutoGroupDrift(Guid GroupId, string GroupName, bool ScriptError,
        List<Guid> MissingMembers, List<Guid> ExtraMembers);

    /// <summary>
    /// Detects cycles in the group-member graph (A → B → A). For each cycle found,
    /// reports the involved group ids. Deduplicated — A→B→A vs B→A→B both report once.
    /// </summary>
    private static List<CycleReport> DetectCycles(List<Group> groups)
    {
        var byId = groups.ToDictionary(g => g.Id);
        var groupIdSet = byId.Keys.ToHashSet();
        var cycles = new List<CycleReport>();
        var seenSignatures = new HashSet<string>();

        foreach (var start in groups)
        {
            var path = new Stack<Guid>();
            var onPath = new HashSet<Guid>();
            if (HasCycle(start.Id, byId, groupIdSet, path, onPath, out var cyclePath))
            {
                var signature = string.Join(",", cyclePath.OrderBy(id => id));
                if (seenSignatures.Add(signature))
                {
                    cycles.Add(new CycleReport(
                        cyclePath.Select(id => new GroupRef(id, byId[id].Name)).ToList()));
                }
            }
        }

        return cycles;
    }

    private static bool HasCycle(
        Guid nodeId,
        Dictionary<Guid, Group> byId,
        HashSet<Guid> groupIdSet,
        Stack<Guid> path,
        HashSet<Guid> onPath,
        out List<Guid> cyclePath)
    {
        cyclePath = [];
        path.Push(nodeId);
        onPath.Add(nodeId);

        if (byId.TryGetValue(nodeId, out var group))
        {
            foreach (var memberId in group.MemberIds)
            {
                // Only walk group-typed members
                if (!groupIdSet.Contains(memberId)) continue;

                if (onPath.Contains(memberId))
                {
                    // Cycle found — extract from `path`
                    var p = path.Reverse().ToList();
                    var idx = p.IndexOf(memberId);
                    cyclePath = p.Skip(idx).ToList();
                    return true;
                }

                if (HasCycle(memberId, byId, groupIdSet, path, onPath, out cyclePath))
                    return true;
            }
        }

        path.Pop();
        onPath.Remove(nodeId);
        return false;
    }
}
