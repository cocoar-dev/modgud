namespace Cocoar.Auth.Tests.Unit;

/// <summary>
/// Smoke check that the unit-test project itself runs. Keep this until the
/// first real test class lands so `dotnet test` always has something green
/// to discover.
/// </summary>
public class SanityCheckTests
{
    [Fact]
    public void OnePlusOneIsTwo() => Assert.Equal(2, 1 + 1);
}
