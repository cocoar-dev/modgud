using System.Linq.Expressions;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Roles;
using Cocoar.Auth.Authorization.Services;
using Cocoar.JsEval.Engine;
using Cocoar.JsEval.Linq;
using Cocoar.JsEval.TypeScript;
using Marten;
using Microsoft.Extensions.Logging;

namespace Cocoar.Auth.Authorization.Access;

public class AccessPolicyEngine(
    IPermissionService permissionService,
    JsEngine jsEngine,
    TsTranspiler tsTranspiler,
    IQuerySession session,
    ILogger<AccessPolicyEngine> logger) : IAccessPolicyEngine
{
    public Task<Expression<Func<TView, bool>>?> BuildFilterAsync<TView>(
        Guid userId, string appSlug, string resourceType,
        CancellationToken ct = default)
        => BuildFilterCoreAsync<TView>(userId, appSlug, resourceType, actionPermission: null, ct);

    public Task<Expression<Func<TView, bool>>?> BuildFilterForActionAsync<TView>(
        Guid userId, string appSlug, string resourceType, string permission,
        CancellationToken ct = default)
        => BuildFilterCoreAsync<TView>(userId, appSlug, resourceType, actionPermission: permission, ct);

    public string TranspileTypeScript(string typeScript)
        => tsTranspiler.Transpile(typeScript);

    private async Task<Expression<Func<TView, bool>>?> BuildFilterCoreAsync<TView>(
        Guid userId, string appSlug, string resourceType, string? actionPermission,
        CancellationToken ct)
    {
        var permissions = await permissionService.GetUserPermissionsAsync(userId, appSlug, ct);

        // Realm-wide bypass — the user is a system admin and unrestricted in
        // any app. Returning null disables row filtering entirely.
        if (permissions.Contains(PermissionEvaluator.RealmAdminPermission))
            return null;

        // App-wide bypass — full access within this app, regardless of
        // resource. Same effect as realm-admin but scoped.
        if (permissions.Contains($"{appSlug}:{PermissionEvaluator.AdminAction}"))
            return null;

        var groups = await permissionService.GetUserGroupsAsync(userId, ct);

        // Limit to groups active in this app — same gate the permission
        // resolution applies. Without it, scripts attached to dormant or
        // other-app groups would leak into the filter.
        groups = groups
            .Where(g => g.BoundTo.Contains(PermissionService.AllAppsWildcard)
                        || g.BoundTo.Contains(appSlug))
            .ToList();

        if (actionPermission is not null)
            groups = await FilterGroupsByPermissionAsync(groups, appSlug, actionPermission, ct);

        var accessScripts = groups
            .SelectMany(g => g.AccessScripts)
            .Where(s => s.ResourceType == resourceType)
            .ToList();

        if (accessScripts.Count == 0)
            return _ => false;

        if (accessScripts.Any(s => string.IsNullOrWhiteSpace(s.CompiledScript)))
            return null;

        var compiledScripts = accessScripts.Select(s => s.CompiledScript!).ToList();

        var userContext = new UserContext
        {
            Id = userId,
            Permissions = permissions,
            Groups = groups.Select(g => g.Name).ToList(),
            GroupIds = groups.Select(g => g.Id).ToList(),
        };

        jsEngine.SetValue("user", userContext);

        using var _ = JsLinqContext.Scope(jsEngine.UnderlyingEngine);

        var (fnName, paramName) = GetScriptInvocation(resourceType);
        var predicates = new List<Expression<Func<TView, bool>>>(compiledScripts.Count);

        foreach (var compiled in compiledScripts)
        {
            var wrapper = new AccessQueryWrapper<TView>(jsEngine.UnderlyingEngine);
            jsEngine.SetValue(paramName, wrapper);

            AccessQueryWrapper<TView>? captured = null;
            jsEngine.SetValue("setResult", (AccessQueryWrapper<TView> result) =>
            {
                captured = result;
            });

            try
            {
                var fullScript = BuildExecutableScript(compiled, fnName, paramName);
                await jsEngine.EvaluateAsync(fullScript);

                var exprs = captured?.Expressions ?? [];
                if (exprs.Count == 0)
                {
                    // setResult not called or called with unfiltered todos → unrestricted access.
                    return null;
                }

                var combined = exprs.Count == 1
                    ? exprs[0]
                    : exprs.Aggregate(CombineAnd);
                predicates.Add(combined);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Access policy script failed for user {UserId}, resource {Resource}. Script contributes no access.",
                    userId, resourceType);
            }
        }

        if (predicates.Count == 0) return _ => false;

        return OrAll(predicates);
    }

    private async Task<List<Group>> FilterGroupsByPermissionAsync(
        List<Group> groups, string appSlug, string permission, CancellationToken ct)
    {
        var roleIds = groups.SelectMany(g => g.RoleIds).Distinct().ToArray();
        if (roleIds.Length == 0) return [];

        var roles = await session.Query<PermissionRole>()
            .Where(r => r.Id.IsOneOf(roleIds) && !r.IsDeleted)
            .ToListAsync(ct);
        var rolesById = roles.ToDictionary(r => r.Id);

        return groups
            .Where(g => g.RoleIds.Any(rid =>
                rolesById.TryGetValue(rid, out var role) && RoleCarriesPermission(role, appSlug, permission)))
            .ToList();
    }

    private static bool RoleCarriesPermission(PermissionRole role, string appSlug, string permission)
    {
        foreach (var action in role.Permissions)
        {
            // Mirror PermissionService expansion: bare actions only contribute
            // when the role belongs to the requested app; fully-qualified
            // permissions pass through unchanged (cross-app grants like
            // realm:admin work even if role.AppSlug differs).
            var full = action.Contains(':')
                ? action
                : (role.AppSlug == appSlug ? $"{role.AppSlug}:{role.ResourceType}:{action}" : null);
            if (full == permission) return true;
        }
        return false;
    }

    private static Expression<Func<TView, bool>> OrAll<TView>(List<Expression<Func<TView, bool>>> predicates)
    {
        if (predicates.Count == 1) return predicates[0];

        var param = Expression.Parameter(typeof(TView), "x");
        var replacer = new ParameterReplacer(param);
        Expression? body = null;
        foreach (var pred in predicates)
        {
            var rebodied = replacer.Replace(pred.Body, pred.Parameters[0]);
            body = body is null ? rebodied : Expression.OrElse(body, rebodied);
        }
        return Expression.Lambda<Func<TView, bool>>(body!, param);
    }

    private static Expression<Func<TView, bool>> CombineAnd<TView>(
        Expression<Func<TView, bool>> left, Expression<Func<TView, bool>> right)
    {
        var param = Expression.Parameter(typeof(TView), "x");
        var replacer = new ParameterReplacer(param);
        var body = Expression.AndAlso(
            replacer.Replace(left.Body, left.Parameters[0]),
            replacer.Replace(right.Body, right.Parameters[0]));
        return Expression.Lambda<Func<TView, bool>>(body, param);
    }

    private static (string FnName, string ParamName) GetScriptInvocation(string resourceType) =>
        resourceType switch
        {
            "todo" => ("QueryTodos", "todos"),
            "customer" => ("QueryCustomers", "customers"),
            _ => ($"Query{char.ToUpperInvariant(resourceType[0])}{resourceType[1..]}", $"{resourceType}s")
        };

    /// <summary>
    /// Builds the full executable script for <c>EvaluateAsync</c> (no module system, top-level await supported).
    /// <c>env</c> and <c>user</c> are available as globals set via <c>SetValue</c> before evaluation.
    /// <list type="bullet">
    ///   <item>New format (plain body): append <c>setResult(param)</c>.</item>
    ///   <item>Old async-function format: wrap invocation with <c>setResult(await fn(param, user))</c>.</item>
    ///   <item>Legacy arrow-function format: wrap in <c>setResult(param.where(fn))</c>.</item>
    /// </list>
    /// </summary>
    private static string BuildExecutableScript(string compiled, string fnName, string paramName)
    {
        var trimmed = compiled.TrimStart();

        // Old async function format — call it and pass result to setResult
        if (trimmed.StartsWith("async function", StringComparison.Ordinal))
            return $"{compiled}\nsetResult(await {fnName}({paramName}, user));";

        // Legacy arrow function — wrap in .where() and pass to setResult
        if (trimmed.StartsWith("(", StringComparison.Ordinal))
            return $"setResult({paramName}.where({compiled}));";

        // New format: plain body, setResult(param) appended as locked footer
        return $"{compiled}\nsetResult({paramName});";
    }

    private sealed class ParameterReplacer(ParameterExpression target) : ExpressionVisitor
    {
        private ParameterExpression? _source;

        public Expression Replace(Expression body, ParameterExpression source)
        {
            _source = source;
            return Visit(body)!;
        }

        protected override Expression VisitParameter(ParameterExpression node)
            => node == _source ? target : base.VisitParameter(node);
    }
}
