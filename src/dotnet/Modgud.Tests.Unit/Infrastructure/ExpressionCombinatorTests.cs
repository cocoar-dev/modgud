using System.Linq.Expressions;
using BuildingBlocks.Helper;

namespace Modgud.Tests.Unit.Infrastructure;

/// <summary>
/// HELP-B9-02 — And/Or rebound every parameter of the combined bodies to the new one,
/// including the parameter of an inner lambda. An inner lambda that uses the outer
/// parameter then compared its own element with itself.
/// </summary>
public class ExpressionCombinatorTests
{
    [Fact]
    public void An_inner_lambda_keeps_its_own_parameter()
    {
        int[] values = [1];
        Expression<Func<int, bool>> inList = a => values.Any(v => v == a);
        Expression<Func<int, bool>> always = _ => true;

        var combined = inList.And(always).Compile();

        // Rebinding v to the new outer parameter made it "p == p" — true for every input.
        Assert.False(combined(5));
        Assert.True(combined(1));
    }
}
