using TimeToDo.Authorization.Principals;
using TimeToDo.Authorization.Roles;
using TimeToDo.Authorization.Services;
using Marten;
using Microsoft.Extensions.Logging;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

namespace TimeToDo.Infrastructure.AccessPolicy;

/// <summary>
/// Explains "would user X be allowed to do action Y on row Z?" in a way that
/// surfaces why — which group granted the permission, which script contributed
/// scope, whether the row fell into scope. Used by the admin Policy Simulator
/// to diagnose unexpected 403s and to validate group configurations.
/// </summary>
public interface IAuthorizationSimulator
{
    Task<SimulationResult> SimulateAsync(
        Guid userId,
        string resourceType,
        Guid? resourceId,
        string action,
        CancellationToken ct = default);
}

public enum SimulationOutcome
{
    Allowed,
    PermissionDenied,      // user lacks the required permission → 403 at endpoint gate
    ScopeDenied,           // permission granted but row not in scope → 403 (or 404 for read)
    ResourceNotFound,      // row id given but no such row exists (even without scope)
}

public record PermissionGrantTrace(string GroupName, string RoleName, string Permission);

public record ScopeScriptTrace(string GroupName, string? Script, bool? Matches);

public record SimulationResult(
    SimulationOutcome Outcome,
    string RequiredPermission,
    bool PermissionGranted,
    bool AdminBypass,
    List<PermissionGrantTrace> PermissionTrace,
    List<ScopeScriptTrace> ScopeTrace,
    bool RowInScope,
    bool RowExists,
    string Summary);

public class AuthorizationSimulator(
    IPermissionService permissionService,
    IAccessPolicyEngine accessPolicy,
    IQuerySession session,
    ILogger<AuthorizationSimulator> logger) : IAuthorizationSimulator
{
    public async Task<SimulationResult> SimulateAsync(
        Guid userId,
        string resourceType,
        Guid? resourceId,
        string action,
        CancellationToken ct = default)
    {
        var permission = action.Contains(':') ? action : $"{resourceType}:{action}";

        var permissions = await permissionService.GetUserPermissionsAsync(userId, ct);
        var adminBypass = permissions.Contains("app:admin");
        var permissionGranted = adminBypass || permissions.Contains(permission);

        var groups = await permissionService.GetUserGroupsAsync(userId, ct);
        var roles = await permissionService.GetUserRolesAsync(userId, ct);
        var rolesById = roles.ToDictionary(r => r.Id);

        var permissionTrace = BuildPermissionTrace(groups, rolesById, permission);

        // If the endpoint gate would deny, we stop there — scope is irrelevant.
        if (!permissionGranted)
        {
            return new SimulationResult(
                Outcome: SimulationOutcome.PermissionDenied,
                RequiredPermission: permission,
                PermissionGranted: false,
                AdminBypass: false,
                PermissionTrace: permissionTrace,
                ScopeTrace: [],
                RowInScope: false,
                RowExists: false,
                Summary: $"User lacks '{permission}'. No role in any of the user's groups grants it.");
        }

        if (adminBypass)
        {
            var rowExistsForAdmin = resourceId is null
                ? true
                : await RowExistsAsync(resourceType, resourceId.Value, ct);
            return new SimulationResult(
                Outcome: resourceId is null || rowExistsForAdmin
                    ? SimulationOutcome.Allowed
                    : SimulationOutcome.ResourceNotFound,
                RequiredPermission: permission,
                PermissionGranted: true,
                AdminBypass: true,
                PermissionTrace: [],
                ScopeTrace: [],
                RowInScope: true,
                RowExists: rowExistsForAdmin,
                Summary: "Admin bypass — 'app:admin' skips all scope and permission checks.");
        }

        // Scope contributing groups = groups whose roles carry the action permission
        var contributingGroups = groups
            .Where(g => g.RoleIds.Any(rid =>
                rolesById.TryGetValue(rid, out var role) && RoleGrants(role, permission)))
            .ToList();

        var scopeTrace = BuildScopeTrace(contributingGroups, resourceType);

        var rowExists = resourceId is null
            ? true
            : await RowExistsAsync(resourceType, resourceId.Value, ct);
        var rowInScope = resourceId is null
            ? true  // with no row, "in scope" collapses to "user has any scope for this action"
            : await RowInScopeAsync(userId, resourceType, resourceId.Value, permission, ct);

        var outcome = (rowExists, rowInScope) switch
        {
            (false, _) => SimulationOutcome.ResourceNotFound,
            (true, false) => SimulationOutcome.ScopeDenied,
            (true, true) => SimulationOutcome.Allowed,
        };

        var summary = BuildSummary(outcome, permission, contributingGroups, scopeTrace, resourceId);

        return new SimulationResult(
            Outcome: outcome,
            RequiredPermission: permission,
            PermissionGranted: true,
            AdminBypass: false,
            PermissionTrace: permissionTrace,
            ScopeTrace: scopeTrace,
            RowInScope: rowInScope,
            RowExists: rowExists,
            Summary: summary);
    }

    private static List<PermissionGrantTrace> BuildPermissionTrace(
        List<Group> groups,
        Dictionary<Guid, PermissionRole> rolesById,
        string permission)
    {
        var trace = new List<PermissionGrantTrace>();
        foreach (var group in groups)
        {
            foreach (var roleId in group.RoleIds)
            {
                if (!rolesById.TryGetValue(roleId, out var role)) continue;
                if (RoleGrants(role, permission))
                    trace.Add(new PermissionGrantTrace(group.Name, role.Name, permission));
            }
        }
        return trace;
    }

    private static bool RoleGrants(PermissionRole role, string permission)
    {
        foreach (var action in role.Permissions)
        {
            var full = action.Contains(':') ? action : $"{role.ResourceType}:{action}";
            if (full == permission) return true;
        }
        return false;
    }

    private static List<ScopeScriptTrace> BuildScopeTrace(
        List<Group> contributingGroups, string resourceType)
    {
        var trace = new List<ScopeScriptTrace>();
        foreach (var group in contributingGroups)
        {
            var scripts = group.AccessScripts.Where(s => s.ResourceType == resourceType).ToList();
            if (scripts.Count == 0)
            {
                // Group contributes the permission but has no script for the resource
                trace.Add(new ScopeScriptTrace(group.Name, Script: null, Matches: null));
                continue;
            }
            foreach (var script in scripts)
            {
                // We don't re-evaluate per-script here because the engine OR-combines before
                // hitting the DB; per-row per-script verification would need JsEval replay
                // and isn't necessary for the MVP summary.
                trace.Add(new ScopeScriptTrace(group.Name, script.Script, Matches: null));
            }
        }
        return trace;
    }

    private async Task<bool> RowExistsAsync(string resourceType, Guid resourceId, CancellationToken ct)
    {
        return resourceType.ToLowerInvariant() switch
        {
            "todo" => await session.Query<TodoView>().Where(t => !t.IsDeleted).AnyAsync(t => t.Id == resourceId, ct),
            "customer" => await session.Query<CustomerView>().Where(c => !c.IsDeleted).AnyAsync(c => c.Id == resourceId, ct),
            _ => false,
        };
    }

    private async Task<bool> RowInScopeAsync(
        Guid userId, string resourceType, Guid resourceId, string permission, CancellationToken ct)
    {
        try
        {
            return resourceType.ToLowerInvariant() switch
            {
                "todo" => await accessPolicy.CanAccessTodoForActionAsync(userId, resourceId, permission, ct),
                "customer" => await accessPolicy.CanAccessCustomerForActionAsync(userId, resourceId, permission, ct),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scope evaluation failed during simulation for user {UserId}", userId);
            return false;
        }
    }

    private static string BuildSummary(
        SimulationOutcome outcome, string permission,
        List<Group> contributingGroups, List<ScopeScriptTrace> scopeTrace, Guid? resourceId)
    {
        return outcome switch
        {
            SimulationOutcome.Allowed when resourceId is null =>
                $"Granted. {contributingGroups.Count} group(s) contribute '{permission}' with a matching scope.",
            SimulationOutcome.Allowed =>
                $"Granted. Row is within the scope of {contributingGroups.Count} group(s) granting '{permission}'.",
            SimulationOutcome.ScopeDenied when scopeTrace.Any(s => s.Script is null) =>
                "Permission granted, but none of the contributing groups has a matching access script for the row.",
            SimulationOutcome.ScopeDenied =>
                "Permission granted, but no contributing group's access script matches this row.",
            SimulationOutcome.ResourceNotFound =>
                "Row does not exist.",
            _ => "Unknown outcome.",
        };
    }
}
