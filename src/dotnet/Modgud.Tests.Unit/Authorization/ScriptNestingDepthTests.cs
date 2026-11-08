using Modgud.Authorization.Membership;

namespace Modgud.Tests.Unit.Authorization;

/// <summary>
/// B3-F07 — the nesting limit skipped everything between backticks, so code inside a
/// template interpolation `${ … }` never counted, although the doc says it does.
/// </summary>
public class ScriptNestingDepthTests
{
    [Fact]
    public void Brackets_inside_a_template_interpolation_count()
    {
        var script = "(p) => `x${" + new string('(', 60) + "1" + new string(')', 60) + "}`";

        Assert.True(ScriptInputLimits.MeasureMaxNestingDepth(script) >= 60);
    }

    [Fact]
    public void Brackets_in_the_literal_part_of_a_template_do_not_count()
    {
        // The outer paren and the interpolation brace count; the literal "(((" does not.
        Assert.Equal(2, ScriptInputLimits.MeasureMaxNestingDepth("(`(((${1}`)"));
    }

    [Fact]
    public void A_string_inside_an_interpolation_is_still_a_string()
    {
        // "{" inside the nested string must not end the interpolation.
        Assert.Equal(2, ScriptInputLimits.MeasureMaxNestingDepth("`${ f('}') }` + (g())"));
    }
}
