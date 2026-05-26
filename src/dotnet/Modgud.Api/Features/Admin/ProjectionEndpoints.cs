using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Membership;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Projections;
using Marten;
using Marten.Events.Daemon.Coordination;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Events;
using Modgud.Authentication.Projections;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;
using Modgud.Infrastructure.Persistence.Tenancy;
using System.Diagnostics;

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
        // Response shape: a flat `Checks` array, one entry per check, each
        // self-describing (`Title`, `Description`, per-check `Summary` and
        // `Issues`). Keeps the UI dumb: no hard-coded copy, no count
        // arithmetic, no resolution work. The admin sees exactly what was
        // checked, what was confirmed, and what's wrong — no GUID-only output.
        //
        // Source of truth:
        //   • ApplicationUser documents   → should have a Person Principal entry
        //   • Group docs                  → should have a Group Principal entry
        //   • PermissionRole docs         → referenced by groups
        //
        // Checks (each in its own `CheckResult` block):
        //   principal-sync     — source ↔ Principal projection drift
        //   dangling-members   — MemberIds in groups that resolve to nothing
        //   dangling-roles     — RoleIds in groups that resolve to nothing
        //   group-cycles       — nested-group cycles (defensive — commands prevent
        //                        creation but historical / direct-DB edits could land one)
        //   auto-group-drift   — auto-group computed membership ↔ persisted MemberIds
        group.MapGet("consistency-check", async (
            IQuerySession session,
            IMembershipEvaluator membershipEvaluator,
            CancellationToken ct) =>
        {
            var totalWatch = Stopwatch.StartNew();

            var applicationUsers = await session.Query<ApplicationUser>()
                .Where(u => !u.IsDeleted)
                .ToListAsync(ct);
            var appUserById = applicationUsers.ToDictionary(u => u.Id);
            var appUserIds = appUserById.Keys.ToHashSet();

            var groups = (await session.Query<Group>()
                .Where(g => !g.IsDeleted)
                .ToListAsync(ct)).ToList();
            var groupById = groups.ToDictionary(g => g.Id);
            var groupIds = groupById.Keys.ToHashSet();

            var principals = await session.Query<Principal>()
                .Where(p => !p.IsDeleted)
                .ToListAsync(ct);
            var principalById = principals.ToDictionary(p => p.Id);
            var principalIds = principalById.Keys.ToHashSet();

            var roles = await session.Query<PermissionRole>()
                .Where(r => !r.IsDeleted)
                .ToListAsync(ct);
            var roleById = roles.ToDictionary(r => r.Id);
            var roleIds = roleById.Keys.ToHashSet();

            // Resolve a guid to a human-readable label by trying every store in
            // turn. Falls back to the truncated GUID if nothing knows it.
            IdName LabelFor(Guid id)
            {
                if (principalById.TryGetValue(id, out var p))
                    return new IdName(id, !string.IsNullOrWhiteSpace(p.DisplayName) ? p.DisplayName : id.ToString("N").Substring(0, 8));
                if (appUserById.TryGetValue(id, out var u))
                    return new IdName(id, !string.IsNullOrWhiteSpace(u.Email) ? u.Email : (u.UserName ?? id.ToString("N").Substring(0, 8)));
                if (groupById.TryGetValue(id, out var g))
                    return new IdName(id, g.Name);
                if (roleById.TryGetValue(id, out var r))
                    return new IdName(id, r.Name);
                return new IdName(id, $"<unknown {id.ToString("N").Substring(0, 8)}>");
            }

            var checkResults = new List<CheckResult>();

            // ─── Check 1: Principal-Sync ─────────────────────────────────
            var checkWatch = Stopwatch.StartNew();
            var missingPerson = appUserIds.Where(id => !principalIds.Contains(id)).Select(LabelFor).ToList();
            var orphanPerson = principals
                .Where(p => p is Person && !appUserIds.Contains(p.Id))
                .Select(p => new IdName(p.Id, !string.IsNullOrWhiteSpace(p.DisplayName) ? p.DisplayName : "<no display name>"))
                .ToList();
            var missingGroup = groupIds.Where(id => !principalIds.Contains(id))
                .Select(id => new IdName(id, groupById[id].Name))
                .ToList();
            var orphanGroup = principals
                .Where(p => p is Group && !groupIds.Contains(p.Id))
                .Select(p => new IdName(p.Id, !string.IsNullOrWhiteSpace(p.DisplayName) ? p.DisplayName : "<no display name>"))
                .ToList();

            var principalSyncOk = missingPerson.Count == 0 && orphanPerson.Count == 0
                               && missingGroup.Count == 0 && orphanGroup.Count == 0;
            checkResults.Add(new CheckResult(
                Id: "principal-sync",
                Title: "Principal projection sync",
                Description: "Every ApplicationUser must have a matching Person Principal; every Group must have a matching Group Principal. Drift here means the inline Principal projection diverged from its source documents — usually a stale projection that a rebuild can fix.",
                Status: principalSyncOk ? "OK" : "ISSUES_FOUND",
                DurationMs: checkWatch.ElapsedMilliseconds,
                Summary: principalSyncOk
                    ? $"{appUserIds.Count}/{appUserIds.Count} ApplicationUsers ↔ Person principals, {groupIds.Count}/{groupIds.Count} Groups ↔ Group principals"
                    : $"{missingPerson.Count + orphanPerson.Count} person drift, {missingGroup.Count + orphanGroup.Count} group drift",
                Issues: new
                {
                    MissingPerson = missingPerson,
                    OrphanPerson = orphanPerson,
                    MissingGroup = missingGroup,
                    OrphanGroup = orphanGroup,
                }));

            // ─── Check 2: Dangling member references ─────────────────────
            checkWatch.Restart();
            var danglingMembers = groups
                .SelectMany(g => g.MemberIds
                    .Where(mid => !principalIds.Contains(mid))
                    .Select(mid => new DanglingMember(g.Id, g.Name, LabelFor(mid))))
                .ToList();
            checkResults.Add(new CheckResult(
                Id: "dangling-members",
                Title: "Dangling member references",
                Description: "Every entry in a group's MemberIds must resolve to a live Principal. Dangling references happen when a member was deleted without first being removed from the groups they were in — the group would silently grant nothing for those ids and the projection-pruning is a manual catch-up.",
                Status: danglingMembers.Count == 0 ? "OK" : "ISSUES_FOUND",
                DurationMs: checkWatch.ElapsedMilliseconds,
                Summary: danglingMembers.Count == 0
                    ? $"All MemberIds across {groups.Count} group(s) resolve"
                    : $"{danglingMembers.Count} dangling reference(s) found",
                Issues: new { Items = danglingMembers }));

            // ─── Check 3: Dangling role references ───────────────────────
            checkWatch.Restart();
            var danglingRoleRefs = groups
                .SelectMany(g => g.RoleIds
                    .Where(rid => !roleIds.Contains(rid))
                    .Select(rid => new DanglingRole(g.Id, g.Name, rid)))
                .ToList();
            checkResults.Add(new CheckResult(
                Id: "dangling-roles",
                Title: "Dangling role references",
                Description: "Every entry in a group's RoleIds must resolve to a live PermissionRole. Dangling references happen when a role was deleted while still bound to one or more groups — affected groups silently grant nothing for those role ids.",
                Status: danglingRoleRefs.Count == 0 ? "OK" : "ISSUES_FOUND",
                DurationMs: checkWatch.ElapsedMilliseconds,
                Summary: danglingRoleRefs.Count == 0
                    ? $"All RoleIds across {groups.Count} group(s) resolve"
                    : $"{danglingRoleRefs.Count} dangling reference(s) found",
                Issues: new { Items = danglingRoleRefs }));

            // ─── Check 4: Group cycles ───────────────────────────────────
            checkWatch.Restart();
            var cycles = GroupCycleDetector.DetectCycles(groups);
            checkResults.Add(new CheckResult(
                Id: "group-cycles",
                Title: "Nested-group cycles",
                Description: "Group membership may nest one level deep or more, but never form a cycle (A → B → A). Update commands reject cycle-introducing edits, so any cycle found here was introduced out-of-band — direct DB edits, broken migration, or a bug in older code.",
                Status: cycles.Count == 0 ? "OK" : "ISSUES_FOUND",
                DurationMs: checkWatch.ElapsedMilliseconds,
                Summary: cycles.Count == 0
                    ? $"No cycles in the group-member graph across {groups.Count} group(s)"
                    : $"{cycles.Count} cycle(s) detected",
                Issues: new { Cycles = cycles }));

            // ─── Check 5: Auto-group membership drift ────────────────────
            checkWatch.Restart();
            var autoGroups = groups.Where(g =>
                g.MembershipMode == MembershipMode.Auto &&
                !string.IsNullOrWhiteSpace(g.CompiledMembershipScript)).ToList();
            var autoDrift = new List<AutoDriftIssue>();

            foreach (var ag in autoGroups)
            {
                try
                {
                    var predicate = membershipEvaluator.BuildPredicate<Principal>(ag.CompiledMembershipScript!, ct);
                    var groupId = ag.Id;
                    var expectedMembers = await session.Query<Principal>()
                        .Where(p => !p.IsDeleted && p.Id != groupId)
                        .Where(predicate)
                        .Select(p => p.Id)
                        .ToListAsync(ct);

                    var expectedSet = expectedMembers.ToHashSet();
                    var actualSet = ag.MemberIds.ToHashSet();
                    var missing = expectedSet.Except(actualSet).Select(LabelFor).ToList();
                    var extra = actualSet.Except(expectedSet).Select(LabelFor).ToList();

                    if (missing.Count > 0 || extra.Count > 0)
                    {
                        autoDrift.Add(new AutoDriftIssue(ag.Id, ag.Name, ScriptError: false, missing, extra));
                    }
                }
                catch (Exception)
                {
                    // Predicate compile/translate failed. Don't blow up the whole
                    // consistency check — surface the script as broken so the admin
                    // can navigate to the group and fix it.
                    autoDrift.Add(new AutoDriftIssue(ag.Id, ag.Name, ScriptError: true, new List<IdName>(), new List<IdName>()));
                }
            }

            checkResults.Add(new CheckResult(
                Id: "auto-group-drift",
                Title: "Auto-group membership drift",
                Description: "Auto-groups derive their membership from a TypeScript predicate. This check re-evaluates each predicate read-only and compares the result to the persisted MemberIds. Drift means the projection's recalculator missed a relevant event — usually transient (will self-heal on the next member change) but can surface a broken script if it persists.",
                Status: autoDrift.Count == 0 ? "OK" : "ISSUES_FOUND",
                DurationMs: checkWatch.ElapsedMilliseconds,
                Summary: autoGroups.Count == 0
                    ? "No auto-groups configured — nothing to drift"
                    : (autoDrift.Count == 0
                        ? $"{autoGroups.Count}/{autoGroups.Count} auto-group(s) match their predicate"
                        : $"{autoDrift.Count}/{autoGroups.Count} auto-group(s) drifted"),
                Issues: new { Items = autoDrift }));

            totalWatch.Stop();
            var allOk = checkResults.All(c => c.Status == "OK");

            return Results.Ok(new
            {
                Status = allOk ? "OK" : "ISSUES_FOUND",
                RunAt = DateTime.UtcNow,
                DurationTotalMs = totalWatch.ElapsedMilliseconds,
                Totals = new
                {
                    ApplicationUsers = applicationUsers.Count,
                    AuthorizationGroups = groups.Count,
                    PrincipalsTotal = principals.Count,
                    PrincipalsPerson = principals.Count(p => p is Person),
                    PrincipalsGroup = principals.Count(p => p is Group),
                    Roles = roles.Count,
                },
                Checks = checkResults,
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
/// Resolved id+label pair surfaced in every consistency-check issue so the
/// admin UI never has to fall back to bare GUIDs. <see cref="Label"/> is the
/// best-effort display string we can derive from whichever store knows the id —
/// DisplayName for principals, Email for users, Name for groups/roles. Stale-id
/// fallback is "&lt;unknown 12345678&gt;".
/// </summary>
internal record IdName(Guid Id, string Label);

/// <summary>
/// One dangling member entry — a group's MemberIds list references an id that
/// no longer resolves to a live principal.
/// </summary>
internal record DanglingMember(Guid GroupId, string GroupName, IdName Member);

/// <summary>
/// One dangling role entry — a group's RoleIds list references a role id that
/// no longer resolves to a live PermissionRole. We can't label the role (it's
/// gone), so only the GUID surfaces.
/// </summary>
internal record DanglingRole(Guid GroupId, string GroupName, Guid RoleId);

/// <summary>
/// One auto-group drift entry — the predicate's expected member set differs
/// from the persisted MemberIds, or the predicate failed to compile entirely.
/// </summary>
internal record AutoDriftIssue(
    Guid GroupId,
    string GroupName,
    bool ScriptError,
    List<IdName> MissingMembers,
    List<IdName> ExtraMembers);

/// <summary>
/// One block in the consistency-check response — self-describing so the FE
/// doesn't hard-code copy. <see cref="Issues"/> is intentionally typed as
/// <c>object</c> because each check carries its own shape.
/// </summary>
internal record CheckResult(
    string Id,
    string Title,
    string Description,
    string Status,
    long DurationMs,
    string Summary,
    object Issues);

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
