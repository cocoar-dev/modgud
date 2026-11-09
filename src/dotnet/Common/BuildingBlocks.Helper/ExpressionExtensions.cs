using System.Linq.Expressions;

namespace BuildingBlocks.Helper;

public static class ExpressionExtensions
{
    public static Expression<Func<T, bool>> And<T>(this Expression<Func<T, bool>> left, Expression<Func<T, bool>> right) =>
        CombineExpressions(left, right, Expression.AndAlso);

    public static Expression<Func<T, bool>> Or<T>(this Expression<Func<T, bool>> left, Expression<Func<T, bool>> right) =>
        CombineExpressions(left, right, Expression.OrElse);

    private static Expression<Func<T, bool>> CombineExpressions<T>(
        Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right,
        Func<Expression, Expression, BinaryExpression> combiner
    )
    {
        var parameter = Expression.Parameter(typeof(T), "p");
        var leftBody = new ParameterReplacer(left.Parameters[0], parameter).Visit(left.Body);
        var rightBody = new ParameterReplacer(right.Parameters[0], parameter).Visit(right.Body);

        var combinedBody = combiner(leftBody, rightBody);
        return Expression.Lambda<Func<T, bool>>(combinedBody, parameter);
    }
}

/// <summary>Replaces exactly one parameter. Replacing every parameter also rebound the
/// parameters of inner lambdas, which changed the predicate (HELP-B9-02).</summary>
internal class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
{
    protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
}
