using System.Linq.Expressions;
using Cocoar.Auth.Application.Authorization;
using Cocoar.Auth.Domain.Authorization;
using Cocoar.JsEval.Engine;
using Cocoar.JsEval.Linq;
using Cocoar.JsEval.TypeScript;
using Microsoft.Extensions.Logging;

namespace Cocoar.Auth.Infrastructure.Authorization;

public class AccessPolicyEngine(
    IPermissionService permissionService,
    JsEngine jsEngine,
    TsTranspiler tsTranspiler,
    ILogger<AccessPolicyEngine> logger) : IAccessPolicyEngine
{
    public string TranspileTypeScript(string typeScript)
        => tsTranspiler.Transpile(typeScript);

    public async Task<Expression<Func<TView, bool>>?> BuildFilterAsync<TView>(
        Guid userId, string resourceType, CancellationToken ct = default)
    {
        var permissions = await permissionService.GetUserPermissionsAsync(userId, ct);

        // Admin bypass — null means "no filter, grant all".
        if (permissions.Contains(Permissions.SystemAdmin) || permissions.Contains(Permissions.TenantAdmin))
            return null;

        var groups = await permissionService.GetUserGroupsAsync(userId, ct);

        // All access-script entries for this resource type across the user's groups.
        // A group is represented iff it has at least one role for this resource type
        // (the frontend syncs AccessScripts against RoleIds on save).
        var accessScripts = groups
            .SelectMany(g => g.AccessScripts)
            .Where(s => s.ResourceType == resourceType)
            .ToList();

        // No group grants this resource type at all → default deny.
        if (accessScripts.Count == 0)
            return _ => false;

        // RBAC-style: an empty script means "role has no row-level filter" →
        // unrestricted access for this resource. OR-combined with other groups
        // that might restrict, unrestricted always wins (set-union semantics).
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

        var predicates = new List<Expression<Func<TView, bool>>>(compiledScripts.Count);
        foreach (var compiled in compiledScripts)
        {
            try
            {
                var jsFn = jsEngine.EvaluateExpression(compiled);
                var predicate = JsExpressionTranslator.Translate<TView, bool>(jsFn, jsEngine.UnderlyingEngine);
                predicates.Add(predicate);
            }
            catch (Exception ex)
            {
                // Fail-closed: a single broken script grants nothing from that group;
                // other groups still contribute. Log so admins can fix the script.
                logger.LogWarning(ex,
                    "Access policy script failed to translate for user {UserId}, resource {Resource}. Script contributes no access.",
                    userId, resourceType);
            }
        }

        if (predicates.Count == 0) return _ => false;

        return OrAll(predicates);
    }

    /// <summary>OR-combines multiple single-param lambdas into one by rebinding the parameter.</summary>
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
