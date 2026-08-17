using Modgud.Api;

namespace Modgud.Api.Tests.Authorization;

public class PositionTerminalsFeatureFlagContractTests
{
    [Fact]
    public void Position_terminals_are_off_by_default()
    {
        var settings = new AppSettings();

        Assert.False(settings.Features.PositionTerminals);
    }
}
