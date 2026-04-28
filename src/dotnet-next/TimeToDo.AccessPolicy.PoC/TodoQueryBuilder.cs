using System.Linq.Expressions;

namespace TimeToDo.AccessPolicy.PoC;

/// <summary>
/// Query builder exposed to Jint scripts. Each method adds an Expression filter.
/// Methods return 'this' for fluent chaining from TypeScript/JavaScript.
///
/// The script calls methods like:
///   query.whereCustomerIn(ctx.managedCustomerIds)
///        .orWhereResponsible(ctx.userId)
///
/// Internally, each call builds an Expression&lt;Func&lt;SimpleTodoView, bool&gt;&gt;
/// that could be passed to Marten's LINQ provider → SQL.
///
/// For the PoC, we compile the expressions to Func and filter in-memory.
/// In production, these expressions go directly to session.Query&lt;TodoView&gt;().Where(...).
/// </summary>
public class TodoQueryBuilder
{
    private readonly List<Expression<Func<SimpleTodoView, bool>>> _orFilters = [];
    private readonly List<Expression<Func<SimpleTodoView, bool>>> _andFilters = [];
    private bool _allowAll;

    // ── OR filters (additive: user sees todos matching ANY of these) ──

    /// <summary>Where the todo's customer ID is in the given list.</summary>
    public TodoQueryBuilder WhereCustomerIn(object[] customerIds)
    {
        var ids = customerIds.Select(ToGuid).Where(g => g != Guid.Empty).ToList();
        if (ids.Count > 0)
            _orFilters.Add(t => t.Customer != null && ids.Contains(t.Customer.Id));
        return this;
    }

    /// <summary>Where the user is a responsible on the todo.</summary>
    public TodoQueryBuilder WhereResponsible(Guid userId)
    {
        _orFilters.Add(t => t.Responsibles.Any(r => r.Id == userId));
        return this;
    }

    /// <summary>Where the user created the todo.</summary>
    public TodoQueryBuilder WhereCreatedBy(Guid userId)
    {
        _orFilters.Add(t => t.CreatedBy != null && t.CreatedBy.Id == userId);
        return this;
    }

    /// <summary>Where the todo has a specific status.</summary>
    public TodoQueryBuilder WhereStatus(string status)
    {
        _orFilters.Add(t => t.Status == status);
        return this;
    }

    /// <summary>Allow all todos (no filter).</summary>
    public TodoQueryBuilder All()
    {
        _allowAll = true;
        return this;
    }

    // ── AND filters (restrictive: applied on top of OR results) ──

    /// <summary>Exclude archived todos.</summary>
    public TodoQueryBuilder ExcludeArchived()
    {
        _andFilters.Add(t => !t.IsArchived);
        return this;
    }

    /// <summary>Only todos with due date after the given date.</summary>
    public TodoQueryBuilder WhereDueAfter(DateTime date)
    {
        _andFilters.Add(t => t.DueDate == null || t.DueDate > date);
        return this;
    }

    /// <summary>Only critical todos.</summary>
    public TodoQueryBuilder WhereCritical()
    {
        _andFilters.Add(t => t.IsCritical);
        return this;
    }

    // ── Build the combined expression ──

    /// <summary>
    /// Combines all OR filters (union) with all AND filters (restriction)
    /// into a single Expression that can be used with LINQ/Marten.
    /// </summary>
    public Expression<Func<SimpleTodoView, bool>> Build()
    {
        if (_allowAll && _andFilters.Count == 0)
            return t => true;

        Expression<Func<SimpleTodoView, bool>> orExpression;

        if (_allowAll)
        {
            orExpression = t => true;
        }
        else if (_orFilters.Count == 0)
        {
            // No filters at all → deny everything
            return t => false;
        }
        else
        {
            orExpression = _orFilters[0];
            for (var i = 1; i < _orFilters.Count; i++)
            {
                orExpression = CombineOr(orExpression, _orFilters[i]);
            }
        }

        // Apply AND filters on top
        var result = orExpression;
        foreach (var andFilter in _andFilters)
        {
            result = CombineAnd(result, andFilter);
        }

        return result;
    }

    /// <summary>
    /// Convenience: compile and filter an in-memory list (PoC / fallback).
    /// In production, you'd pass Build() to session.Query&lt;TodoView&gt;().Where(...).
    /// </summary>
    public List<SimpleTodoView> Apply(List<SimpleTodoView> todos)
    {
        var filter = Build().Compile();
        return todos.Where(filter).ToList();
    }

    // ── Expression combinators ──

    private static Expression<Func<SimpleTodoView, bool>> CombineOr(
        Expression<Func<SimpleTodoView, bool>> left,
        Expression<Func<SimpleTodoView, bool>> right)
    {
        var parameter = Expression.Parameter(typeof(SimpleTodoView), "t");
        var body = Expression.OrElse(
            Expression.Invoke(left, parameter),
            Expression.Invoke(right, parameter));
        return Expression.Lambda<Func<SimpleTodoView, bool>>(body, parameter);
    }

    private static Expression<Func<SimpleTodoView, bool>> CombineAnd(
        Expression<Func<SimpleTodoView, bool>> left,
        Expression<Func<SimpleTodoView, bool>> right)
    {
        var parameter = Expression.Parameter(typeof(SimpleTodoView), "t");
        var body = Expression.AndAlso(
            Expression.Invoke(left, parameter),
            Expression.Invoke(right, parameter));
        return Expression.Lambda<Func<SimpleTodoView, bool>>(body, parameter);
    }

    // ── Helpers ──

    private static Guid ToGuid(object obj)
    {
        return obj switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var parsed) => parsed,
            _ => Guid.Empty
        };
    }
}
