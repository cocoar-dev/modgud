using NetArchTest.Rules;

namespace Cocoar.Auth.Tests.Unit.Architecture;

/// <summary>
/// <c>Cocoar.Auth.Application</c> holds DTOs, services like
/// <c>OAuthAdminService</c>, and policy/validation abstractions. Marten and
/// Wolverine ARE allowed here — admin services drive aggregate-stream
/// operations directly — but the web tier is not. Endpoints, hubs,
/// middleware, FIDO2 wiring live in Api / Authentication.
/// </summary>
public class ApplicationPurityTests
{
    [Fact]
    public void Application_should_not_depend_on_AspNetCore()
    {
        var result = Types.InAssembly(Assemblies.Application)
            .Should()
            .NotHaveDependencyOnAny(
                "Microsoft.AspNetCore",
                "Microsoft.AspNetCore.Mvc",
                "Microsoft.AspNetCore.Http",
                "Microsoft.AspNetCore.SignalR")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            TestResultFormatter.Format(result,
                "Cocoar.Auth.Application must not depend on ASP.NET Core — " +
                "HTTP/SignalR wiring belongs in Api/Authentication."));
    }
}
