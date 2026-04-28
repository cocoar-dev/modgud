using System.Linq.Expressions;

namespace BuildingBlocks.Helper;

public class ExpressionBuilder<T>
{
    private Expression<Func<T, bool>>? _expression;

    public static ExpressionBuilder<T> StartWith(Expression<Func<T, bool>> initial)
    {
        return new ExpressionBuilder<T> { _expression = initial };
    }

    public ExpressionBuilder<T> And(Expression<Func<T, bool>> expr)
    {
        _expression = _expression == null ? expr : _expression.And(expr);
        return this;
    }

    public ExpressionBuilder<T> Or(Expression<Func<T, bool>> expr)
    {
        _expression = _expression == null ? expr : _expression.Or(expr);
        return this;
    }

    public Expression<Func<T, bool>> Build() => _expression ?? throw new InvalidOperationException("No expressions were added.");
}
