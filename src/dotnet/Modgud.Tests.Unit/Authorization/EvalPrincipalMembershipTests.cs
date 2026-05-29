using Modgud.Authorization.Membership;
using Modgud.Authorization.Principals;
using Cocoar.JsEval.Engine;
using Cocoar.JsEval.Linq;
using Cocoar.JsEval.TsDefinition;
using Cocoar.JsEval.TypeScript;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Tests.Unit.Authorization;

/// <summary>
/// Federation v1 — de-risk: can a membership predicate compiled over the
/// in-memory <see cref="EvalPrincipal"/> wrapper (NOT a Principal subclass)
/// translate + evaluate the patterns the v1 script contract needs?
/// Type.Is narrowing, local-field reads, and the ephemeral external surface
/// (ExternalGroups array .includes, ExternalClaims dictionary access).
/// </summary>
public sealed class EvalPrincipalMembershipTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly IServiceScope _scope;
    private readonly IMembershipEvaluator _evaluator;
    private readonly TsTranspiler _transpiler;

    public EvalPrincipalMembershipTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJsEval(b => b
            .AddLinq()
            .AddDiscriminatorMappings<Principal>("Type",
                ("person", typeof(Person)),
                ("group", typeof(Group)),
                ("service-account", typeof(ServiceAccount)))
            .WithExecutionTimeout(TimeSpan.FromSeconds(2)));
        services.AddTsTranspiler();
        services.AddTsDefinition();
        services.AddScoped<IMembershipEvaluator, MembershipEvaluator>();

        _sp = services.BuildServiceProvider();
        _scope = _sp.CreateScope();
        _evaluator = _scope.ServiceProvider.GetRequiredService<IMembershipEvaluator>();
        _transpiler = _scope.ServiceProvider.GetRequiredService<TsTranspiler>();
    }

    public void Dispose()
    {
        _scope.Dispose();
        _sp.Dispose();
    }

    private Func<EvalPrincipal, bool> Compile(string ts)
    {
        var compiled = _transpiler.Transpile(ts);
        return _evaluator.BuildPredicate<EvalPrincipal>(compiled).Compile();
    }

    [Fact]
    public void TypeIs_Person_Narrows_Against_Wrapper()
    {
        var fn = Compile("(p: any) => Type.Is(p, 'person')");
        Assert.True(fn(new EvalPrincipal { Id = Guid.NewGuid() }));
    }

    [Fact]
    public void LocalField_Read_Works_Like_Person()
    {
        var fn = Compile("(p: any) => Type.Is(p, 'person') && p.Email != null && p.Email.endsWith('@acme.com')");
        Assert.True(fn(new EvalPrincipal { Email = "x@acme.com" }));
        Assert.False(fn(new EvalPrincipal { Email = "x@contoso.com" }));
        Assert.False(fn(new EvalPrincipal { Email = null }));
    }

    [Fact]
    public void ExternalGroups_Array_Includes_Works()
    {
        var fn = Compile("(p: any) => p.ExternalGroups.includes('Admins')");
        Assert.True(fn(new EvalPrincipal { ExternalGroups = ["IT", "Admins"] }));
        Assert.False(fn(new EvalPrincipal { ExternalGroups = ["IT"] }));
        Assert.False(fn(new EvalPrincipal { ExternalGroups = [] }));
    }

    [Fact]
    public void Realistic_Federation_Script_Type_And_Group()
    {
        // The canonical v1 federation rule: a person in the upstream group.
        var fn = Compile("(p: any) => Type.Is(p, 'person') && p.IsActive && p.ExternalGroups.includes('entra-admins')");
        Assert.True(fn(new EvalPrincipal { IsActive = true, ExternalGroups = ["entra-admins", "all-staff"] }));
        Assert.False(fn(new EvalPrincipal { IsActive = true, ExternalGroups = ["all-staff"] }));
        Assert.False(fn(new EvalPrincipal { IsActive = false, ExternalGroups = ["entra-admins"] }));
    }
}
