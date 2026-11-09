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
    private const string FeaturesNamespace = @"^Modgud\.Api\.Features(\..*)?$";

    /// <summary>B10-12 — a rule over an empty selection passes without checking anything.
    /// The namespace filter had kept a pre-rename prefix and selected no type at all.</summary>
    [Fact]
    public void The_rule_selects_the_endpoint_classes_it_is_about()
    {
        var selected = Types.InAssembly(Assemblies.Api)
            .That()
            .ResideInNamespaceMatching(FeaturesNamespace)
            .And()
            .HaveNameEndingWith("Endpoints")
            .GetTypes()
            .Count();

        Assert.True(selected >= 10, $"Only {selected} *Endpoints classes matched the slice namespace.");
    }

    [Fact]
    public void Endpoint_classes_should_be_static()
    {
        // Endpoints are extension-method hosts on WebApplication / RouteGroupBuilder.
        // An instance class would hide CQRS dispatch behind an ambient lifetime.
        var result = Types.InAssembly(Assemblies.Api)
            .That()
            .ResideInNamespaceMatching(FeaturesNamespace)
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
