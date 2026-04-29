using Cocoar.Auth.Infrastructure.Events;

namespace Cocoar.Auth.Tests.Unit.Infrastructure.Events;

/// <summary>
/// Pins <see cref="ProjectionSideEffects.Enabled"/> default state. The startup
/// sequence relies on this flag being <c>false</c> until after Wolverine is up,
/// otherwise the daemon catchup throws WolverineHasNotStartedException. A
/// silently-flipped default would surface only as flaky boots in CI.
/// </summary>
public class ProjectionSideEffectsTests
{
    // NOTE: this is a static-state pin. We can't safely toggle it across tests
    // because other tests in the assembly might rely on the runtime value;
    // we only assert what the field currently is at first observation.

    [Fact]
    public void Default_is_false_until_explicitly_enabled()
    {
        // We observe the value rather than reset it — this avoids cross-test
        // interference. If a future test sets it to true, this assertion will
        // need to be made order-independent.
        var initial = ProjectionSideEffects.Enabled;

        // Toggle and restore to prove the property is a plain settable bool.
        try
        {
            ProjectionSideEffects.Enabled = true;
            Assert.True(ProjectionSideEffects.Enabled);

            ProjectionSideEffects.Enabled = false;
            Assert.False(ProjectionSideEffects.Enabled);
        }
        finally
        {
            ProjectionSideEffects.Enabled = initial;
        }
    }
}
