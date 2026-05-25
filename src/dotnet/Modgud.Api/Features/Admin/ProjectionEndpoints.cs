using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Projections;
using Marten;
using Marten.Events.Daemon.Coordination;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Events;
using Modgud.Authentication.Projections;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Features.Admin;

public static class ProjectionEndpoints
{
    // Serialises the rebuild endpoint. Concurrent rebuilds would race on the
    // process-wide ProjectionSideEffects.Enabled flag — the second caller can
    // capture the first's interim `false` and permanently disable side effects
    // when the first call's `finally` restores it.
    private static readonly SemaphoreSlim _rebuildGate = new(1, 1);

    public static WebApplication MapProjectionEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/projections")
            .WithTags("Admin Projections")
            .RequireAuthorization()
            .RequiresPermission("realm:admin");

        group.MapPost("rebuild", async (
            IDocumentStore store,
            IProjectionCoordinator coordinator,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (!await _rebuildGate.WaitAsync(0, ct))
            {
                Serilog.Log.Warning("Admin: Projection rebuild rejected — another rebuild is already running");
                return Results.Conflict(new { Message = "A projection rebuild is already in progress" });
            }

            // Rebuild is scoped to the realm the admin is currently logged into —
            // the host they hit determines which tenant DB gets replayed. We
            // intentionally do NOT iterate every active realm: rebuild is heavy,
            // non-resumable mid-flight, and per-realm host gating is the right
            // authorization boundary. Defensive fallback to the system tenant if
            // RealmMiddleware didn't run (shouldn't happen for this authenticated
            // route, but opening against system is safer than throwing).
            //
            // Under MasterTableTenancy a session without an explicit tenant id is
            // an error ("Default tenant does not supported"), so passing the id
            // explicitly here is REQUIRED, not optional.
            var tenantId = httpContext.Items[TenantConstants.HttpContextTenantIdKey] as string
                           ?? TenantConstants.SystemTenantId;

            Serilog.Log.Information("Admin: Starting full projection rebuild for tenant {TenantId}", tenantId);
            try
            {
                await coordinator.PauseAsync();

                var wasEnabled = ProjectionSideEffects.Enabled;
                ProjectionSideEffects.Enabled = false;

                try
                {
                    // Async composite (UserView only in the IdP-only baseline) —
                    // explicit deletes mirror what each projection writes so
                    // RebuildProjectionAsync sees a clean slate before the daemon replays.
                    await using var session = store.LightweightSession(tenantId);
                    session.DeleteWhere<UserView>(x => true);
                    await session.SaveChangesAsync(ct);

                    // Daemon is scoped to a single tenant database under MasterTableTenancy.
                    using var daemon = await store.BuildProjectionDaemonAsync(tenantId);
                    var timeout = TimeSpan.FromMinutes(10);

                    await daemon.RebuildProjectionAsync("ViewProjections", timeout, ct);

                    // Inline projections — RebuildProjectionAsync<T> drops the produced
                    // documents itself and replays from event 0. Same path the
                    // `recover rebuild-projections` CLI uses for first-migration bootstrap;
                    // this endpoint is the maintenance equivalent (when an admin is logged in).
                    await daemon.RebuildProjectionAsync<ModgudPrincipalProjection>(timeout, ct);
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
            finally
            {
                _rebuildGate.Release();
            }
        })
        .WithName("AdminProjections_Rebuild");

        // Consistency check — reports drift between authoritative sources and derived state.
        //
        // Source of truth:
        //   • ApplicationUser documents   → should have a Person Principal entry
        //   • Group docs                  → should have a Group Principal entry
        //   • PermissionRole docs         → referenced by groups
        //
        // Checks:
        //   A. PrincipalValidation drift — source count vs. projection count, per type
        //   B. Dangling principal refs in groups' MemberIds
        //   C. Dangling role refs in groups' RoleIds
        //   D. Nested-group cycles (defensive — UpdateGroupCommand prevents creation but
        //       historical data or direct DB edits could introduce one)
        //   E. Auto-group membership drift
        group.MapGet("consistency-check", async (
            IQuerySession session,
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

            // D. Nested-group cycles
            var cycles = GroupCycleDetector.DetectCycles(groups);

            var ok = missingPersonPrincipals.Count == 0
                  && orphanPersonPrincipals.Count == 0
                  && missingGroupPrincipals.Count == 0
                  && orphanGroupPrincipals.Count == 0
                  && danglingMembers.Count == 0
                  && danglingRoles.Count == 0
                  && cycles.Count == 0;

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
                },
                GroupCycles = cycles,
            });
        })
        .WithName("AdminProjections_ConsistencyCheck");

        return application;
    }

}

/// <summary>
/// Identifier + display-name pair surfaced in the cycle-detection report so the
/// admin UI can render "<c>Eng</c> → <c>Leads</c> → <c>Eng</c>" without a follow-up lookup.
/// </summary>
internal record GroupRef(Guid Id, string Name);

/// <summary>
/// One detected cycle in the nested-group graph, deduplicated across rotations
/// (A→B→A and B→A→B report once).
/// </summary>
internal record CycleReport(List<GroupRef> Groups);

/// <summary>
/// Pure cycle detection over the group-membership graph. Extracted from
/// <see cref="ProjectionEndpoints"/> so the DFS + dedup behaviour can be unit
/// tested without spinning up Marten — historical data or out-of-band DB edits
/// are the reason this defensive check exists at all.
/// <para>Internal — only the consistency-check endpoint and its tests should use it.</para>
/// </summary>
internal static class GroupCycleDetector
{
    /// <summary>
    /// Detects cycles in the group-member graph (A → B → A). For each cycle found,
    /// reports the involved group ids. Deduplicated — A→B→A vs B→A→B both report once.
    /// </summary>
    public static List<CycleReport> DetectCycles(List<Group> groups)
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
