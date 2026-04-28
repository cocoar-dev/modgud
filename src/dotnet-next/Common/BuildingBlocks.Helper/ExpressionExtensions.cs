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
        var leftBody = new ParameterReplacer(parameter).Visit(left.Body);
        var rightBody = new ParameterReplacer(parameter).Visit(right.Body);

        var combinedBody = combiner(leftBody, rightBody);
        return Expression.Lambda<Func<T, bool>>(combinedBody, parameter);
    }
}

internal class ParameterReplacer : ExpressionVisitor
{
    private readonly ParameterExpression _parameter;
    public ParameterReplacer(ParameterExpression parameter) => _parameter = parameter;
    protected override Expression VisitParameter(ParameterExpression node) => _parameter;
}
