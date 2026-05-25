using NetArchTest.Rules;

namespace Modgud.Tests.Unit.Architecture;

/// <summary>
/// Naming/structure conventions for the API slice — kept narrow on purpose
/// (only rules that are CURRENTLY true across every slice; Modgud's
/// CQRS folder convention is enforced only where it's already in use, not
/// imposed top-down).
/// </summary>
public class SliceShapeTests
{
    [Fact]
    public void Endpoint_classes_should_be_static()
    {
        // Endpoints are extension-method hosts on WebApplication / RouteGroupBuilder.
        // An instance class would hide CQRS dispatch behind an ambient lifetime.
        var result = Types.InAssembly(Assemblies.Api)
            .That()
            .ResideInNamespaceMatching(@"Cocoar\.Auth\.Api\.Features\..*")
            .And()
            .HaveNameEndingWith("Endpoints")
            .Should()
            .BeStatic()
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            TestResultFormatter.Format(result,
                "All *Endpoints classes under Modgud.Api.Features must be static."));
    }
}
