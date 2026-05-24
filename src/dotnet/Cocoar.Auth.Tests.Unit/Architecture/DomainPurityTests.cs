using NetArchTest.Rules;

namespace Cocoar.Auth.Tests.Unit.Architecture;

/// <summary>
/// <c>Cocoar.Auth.Domain</c> holds events, value objects, and event-sourced
/// aggregates (the 3 OAuth aggregates use partial classes + Marten 9 source-
/// generation, which is allowed — the JasperFx.Events.SourceGenerator analyzer
/// emits zero-runtime-cost projection dispatchers and does not pull in
/// Marten persistence types at compile time).
///
/// <para>What Domain MUST NOT do is talk to the web layer. Anything from
/// ASP.NET Core is a leak — Domain types are reused server-side without an
/// HTTP context and must stay framework-neutral.</para>
///
/// <para>Inversions deliberately NOT enforced:
/// <list type="bullet">
///   <item><description>Domain → Cocoar.Auth.Authorization (legacy project
///     reference; would require unwinding the permission-id catalog dependency
///     from event records).</description></item>
/// </list></para>
/// </summary>
public class DomainPurityTests
{
    [Fact]
    public void Domain_should_not_depend_on_AspNetCore()
    {
        var result = Types.InAssembly(Assemblies.Domain)
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
                "Cocoar.Auth.Domain must not depend on ASP.NET Core — " +
                "Domain types are framework-neutral by design."));
    }

    [Fact]
    public void Domain_should_not_depend_on_OpenIddict()
    {
        // OpenIddict is the OAuth-server-side runtime. Domain holds OAuth
        // aggregates (OAuthApplicationAggregate etc.) but those describe
        // STATE, not server behaviour — OpenIddict integration lives in
        // Infrastructure.OpenIddict (MartenApplicationStore et al.).
        var result = Types.InAssembly(Assemblies.Domain)
            .Should()
            .NotHaveDependencyOnAny(
                "OpenIddict",
                "OpenIddict.Abstractions",
                "OpenIddict.Server",
                "OpenIddict.Validation",
                "OpenIddict.AspNetCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            TestResultFormatter.Format(result,
                "Cocoar.Auth.Domain must not depend on OpenIddict — " +
                "OAuth-server behaviour lives in Infrastructure.OpenIddict."));
    }
}
