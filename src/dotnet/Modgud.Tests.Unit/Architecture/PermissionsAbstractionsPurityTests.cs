using NetArchTest.Rules;

namespace Modgud.Tests.Unit.Architecture;

/// <summary>
/// <c>Modgud.Permissions.Abstractions</c> is the one assembly external
/// resource-server consumers (via <c>Modgud.Client.AspNetCore</c>) link
/// against to evaluate permissions in-process. Its whole reason to exist is
/// the absence of IdP-side baggage — Marten, Wolverine, JsEval, ASP.NET
/// hosting, anything Modgud-internal. If any of those leak in, the
/// abstraction stops being reusable and downstream services drag in the
/// kitchen sink.
/// </summary>
public class PermissionsAbstractionsPurityTests
{
    [Fact]
    public void PermissionsAbstractions_should_not_depend_on_Marten()
    {
        var result = Types.InAssembly(Assemblies.PermissionsAbstractions)
            .Should()
            .NotHaveDependencyOnAny("Marten", "Marten.Schema", "Marten.Events")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            TestResultFormatter.Format(result,
                "Modgud.Permissions.Abstractions must not depend on Marten — " +
                "the assembly is consumed by external resource servers that have " +
                "no persistence relationship with the IdP."));
    }

    [Fact]
    public void PermissionsAbstractions_should_not_depend_on_Wolverine()
    {
        var result = Types.InAssembly(Assemblies.PermissionsAbstractions)
            .Should()
            .NotHaveDependencyOnAny("Wolverine", "WolverineFx", "WolverineFx.Marten")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            TestResultFormatter.Format(result,
                "Modgud.Permissions.Abstractions must not depend on Wolverine."));
    }

    [Fact]
    public void PermissionsAbstractions_should_not_depend_on_JsEval()
    {
        var result = Types.InAssembly(Assemblies.PermissionsAbstractions)
            .Should()
            .NotHaveDependencyOnAny(
                "Cocoar.JsEval",
                "Cocoar.JsEval.Engine",
                "Cocoar.JsEval.Linq",
                "Cocoar.JsEval.TypeScript",
                "Cocoar.JsEval.TsDefinition")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            TestResultFormatter.Format(result,
                "Modgud.Permissions.Abstractions must not depend on JsEval — " +
                "membership-script evaluation is an IdP-side concern."));
    }

    [Fact]
    public void PermissionsAbstractions_should_not_depend_on_AspNetCore()
    {
        var result = Types.InAssembly(Assemblies.PermissionsAbstractions)
            .Should()
            .NotHaveDependencyOnAny(
                "Microsoft.AspNetCore",
                "Microsoft.AspNetCore.Mvc",
                "Microsoft.AspNetCore.Http",
                "Microsoft.AspNetCore.SignalR",
                "Microsoft.AspNetCore.Authentication")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            TestResultFormatter.Format(result,
                "Modgud.Permissions.Abstractions must not depend on ASP.NET Core — " +
                "the ASP.NET-aware integration helpers live in Modgud.Client.AspNetCore."));
    }

    [Fact]
    public void PermissionsAbstractions_should_not_depend_on_other_Modgud_internals()
    {
        var result = Types.InAssembly(Assemblies.PermissionsAbstractions)
            .Should()
            .NotHaveDependencyOnAny(
                "Modgud.Domain",
                "Modgud.Application",
                "Modgud.Authentication",
                "Modgud.Authorization",
                "Modgud.Infrastructure",
                "Modgud.Api",
                "Modgud.Client.AspNetCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            TestResultFormatter.Format(result,
                "Modgud.Permissions.Abstractions must stand alone — " +
                "any inward reference would tie external RS consumers to IdP internals."));
    }
}
