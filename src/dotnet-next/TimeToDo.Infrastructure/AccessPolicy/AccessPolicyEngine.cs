using System.Linq.Expressions;
using Cocoar.JsEval.Engine;
using Marten;
using Microsoft.Extensions.Logging;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;
using LibEngine = TimeToDo.Authorization.Access.IAccessPolicyEngine;

namespace TimeToDo.Infrastructure.AccessPolicy;

/// <summary>
/// TimeToDo-facing wrapper around the generic library <see cref="LibEngine"/>.
/// Provides view-specific helpers (Todo/Customer) used by endpoints + query handlers.
/// Injects an <see cref="AccessPolicyEnvironment"/> as <c>env</c> into todo scripts so
/// cross-resource helpers like <c>env.AllowedCustomerIds()</c> work without requiring
/// DB access until the script actually calls them.
/// </summary>
public interface IAccessPolicyEngine
{
    Task<Expression<Func<TodoView, bool>>?> BuildTodoFilterAsync(Guid userId, CancellationToken ct = default);
    Task<Expression<Func<CustomerView, bool>>?> BuildCustomerFilterAsync(Guid userId, CancellationToken ct = default);

    Task<Expression<Func<TodoView, bool>>?> BuildTodoFilterForActionAsync(Guid userId, string permission, CancellationToken ct = default);
    Task<Expression<Func<CustomerView, bool>>?> BuildCustomerFilterForActionAsync(Guid userId, string permission, CancellationToken ct = default);

    Task<bool> CanAccessTodoAsync(Guid userId, Guid todoId, CancellationToken ct = default);
    Task<bool> CanAccessCustomerAsync(Guid userId, Guid customerId, CancellationToken ct = default);

    Task<bool> CanAccessTodoForActionAsync(Guid userId, Guid todoId, string permission, CancellationToken ct = default);
    Task<bool> CanAccessCustomerForActionAsync(Guid userId, Guid customerId, string permission, CancellationToken ct = default);

    Task<bool> CanCreateTodoAsync(Guid userId, TodoView proto, CancellationToken ct = default);
    Task<bool> CanCreateCustomerAsync(Guid userId, CustomerView proto, CancellationToken ct = default);

    string TranspileTypeScript(string typeScript);
}

public class AccessPolicyEngine(
    LibEngine inner,
    JsEngine jsEngine,
    IQuerySession session,
    ILogger<AccessPolicyEngine> logger) : IAccessPolicyEngine
{
    public Task<Expression<Func<TodoView, bool>>?> BuildTodoFilterAsync(Guid userId, CancellationToken ct = default)
        => BuildTodoFilterWithHelperAsync(userId, actionPermission: null, ct);

    public Task<Expression<Func<CustomerView, bool>>?> BuildCustomerFilterAsync(Guid userId, CancellationToken ct = default)
        => inner.BuildFilterAsync<CustomerView>(userId, "customer", ct: ct);

    public Task<Expression<Func<TodoView, bool>>?> BuildTodoFilterForActionAsync(Guid userId, string permission, CancellationToken ct = default)
        => BuildTodoFilterWithHelperAsync(userId, actionPermission: permission, ct);

    public Task<Expression<Func<CustomerView, bool>>?> BuildCustomerFilterForActionAsync(Guid userId, string permission, CancellationToken ct = default)
        => inner.BuildFilterForActionAsync<CustomerView>(userId, "customer", permission, ct: ct);

    public async Task<bool> CanAccessTodoAsync(Guid userId, Guid todoId, CancellationToken ct = default)
    {
        var filter = await BuildTodoFilterAsync(userId, ct);
        if (filter is null) return true;
        return await session.Query<TodoView>().Where(filter).AnyAsync(t => t.Id == todoId, ct);
    }

    public async Task<bool> CanAccessCustomerAsync(Guid userId, Guid customerId, CancellationToken ct = default)
    {
        var filter = await BuildCustomerFilterAsync(userId, ct);
        if (filter is null) return true;
        return await session.Query<CustomerView>().Where(filter).AnyAsync(c => c.Id == customerId, ct);
    }

    public async Task<bool> CanAccessTodoForActionAsync(Guid userId, Guid todoId, string permission, CancellationToken ct = default)
    {
        var filter = await BuildTodoFilterForActionAsync(userId, permission, ct);
        if (filter is null) return true;
        return await session.Query<TodoView>().Where(filter).AnyAsync(t => t.Id == todoId, ct);
    }

    public async Task<bool> CanAccessCustomerForActionAsync(Guid userId, Guid customerId, string permission, CancellationToken ct = default)
    {
        var filter = await BuildCustomerFilterForActionAsync(userId, permission, ct);
        if (filter is null) return true;
        return await session.Query<CustomerView>().Where(filter).AnyAsync(c => c.Id == customerId, ct);
    }

    public async Task<bool> CanCreateTodoAsync(Guid userId, TodoView proto, CancellationToken ct = default)
    {
        var filter = await BuildTodoFilterForActionAsync(userId, "todo:create", ct);
        if (filter is null) return true;
        return EvaluateInMemory(filter, proto, userId, "todo");
    }

    public async Task<bool> CanCreateCustomerAsync(Guid userId, CustomerView proto, CancellationToken ct = default)
    {
        var filter = await BuildCustomerFilterForActionAsync(userId, "customer:create", ct);
        if (filter is null) return true;
        return EvaluateInMemory(filter, proto, userId, "customer");
    }

    public string TranspileTypeScript(string typeScript) => inner.TranspileTypeScript(typeScript);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<Expression<Func<TodoView, bool>>?> BuildTodoFilterWithHelperAsync(
        Guid userId, string? actionPermission, CancellationToken ct)
    {
        var env = new AccessPolicyEnvironment(userId, inner, session, ct);
        jsEngine.SetValue("env", env);

        return actionPermission is null
            ? inner.BuildFilterAsync<TodoView>(userId, "todo", ct)
            : inner.BuildFilterForActionAsync<TodoView>(userId, "todo", actionPermission, ct);
    }

    private bool EvaluateInMemory<TView>(Expression<Func<TView, bool>> filter, TView proto, Guid userId, string resourceType)
    {
        try
        {
            return filter.Compile().Invoke(proto);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Proto in-memory evaluation failed for user {UserId}, resource {Resource}. Denying create.",
                userId, resourceType);
            return false;
        }
    }
}
