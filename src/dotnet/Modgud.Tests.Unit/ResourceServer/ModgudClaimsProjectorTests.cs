using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Modgud.AspNetCore.ResourceServer;

namespace Modgud.Tests.Unit.ResourceServer;

public class ModgudClaimsProjectorTests
{
    private const string Audience = "https://policy-api.example.com";

    private static ClaimsPrincipal Principal(string resourceAccess, bool authenticated = true)
    {
        var identity = new ClaimsIdentity(
            authenticated ? [new Claim(ModgudClaimTypes.ResourceAccess, resourceAccess, JsonClaimValueTypes.Json)] : [],
            authenticated ? "test" : null);
        if (!authenticated)
            identity.AddClaim(new Claim(ModgudClaimTypes.ResourceAccess, resourceAccess));
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void Projects_only_the_selected_audience()
    {
        var principal = Principal($$"""
            {
              "{{Audience}}": {
                "roles": ["Editor"],
                "permissions": ["policy:read", "policy:write"]
              },
              "https://other.example.com": {
                "roles": ["ShouldNotLeak"],
                "permissions": ["other:admin"]
              }
            }
            """);

        ModgudClaimsProjector.Project(principal, Audience);

        Assert.Contains(principal.FindAll(ClaimTypes.Role), x => x.Value == "Editor");
        Assert.DoesNotContain(principal.FindAll(ClaimTypes.Role), x => x.Value == "ShouldNotLeak");
        Assert.Contains(principal.FindAll(ModgudClaimTypes.Permission), x => x.Value == "policy:write");
        Assert.DoesNotContain(principal.FindAll(ModgudClaimTypes.Permission), x => x.Value == "other:admin");
    }

    [Fact]
    public void Different_schemes_can_project_different_audiences_without_global_state()
    {
        const string json = """
            {
              "api-a": { "permissions": ["a:read"] },
              "api-b": { "permissions": ["b:read"] }
            }
            """;
        var schemeA = Principal(json);
        var schemeB = Principal(json);

        ModgudClaimsProjector.Project(schemeA, "api-a");
        ModgudClaimsProjector.Project(schemeB, "api-b");

        Assert.Equal(["a:read"], schemeA.FindAll(ModgudClaimTypes.Permission).Select(x => x.Value));
        Assert.Equal(["b:read"], schemeB.FindAll(ModgudClaimTypes.Permission).Select(x => x.Value));
    }

    [Fact]
    public void Projection_is_idempotent()
    {
        var principal = Principal($$"""{ "{{Audience}}": { "roles": ["Editor"] } }""");

        ModgudClaimsProjector.Project(principal, Audience);
        ModgudClaimsProjector.Project(principal, Audience);

        Assert.Single(principal.FindAll(ClaimTypes.Role));
    }

    [Fact]
    public void Groups_are_never_projected()
    {
        var principal = Principal($$"""{ "{{Audience}}": { "groups": ["Internal"] } }""");

        ModgudClaimsProjector.Project(principal, Audience);

        Assert.Empty(principal.FindAll("group"));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("""{"another-api":{"permissions":["x:y"]}}""")]
    public void Missing_or_malformed_audience_data_is_a_no_op(string resourceAccess)
    {
        var principal = Principal(resourceAccess);

        ModgudClaimsProjector.Project(principal, Audience);

        Assert.Empty(principal.FindAll(ModgudClaimTypes.Permission));
    }

    [Fact]
    public void Anonymous_principals_are_not_projected()
    {
        var principal = Principal(
            $$"""{ "{{Audience}}": { "permissions": ["policy:write"] } }""",
            authenticated: false);

        ModgudClaimsProjector.Project(principal, Audience);

        Assert.Empty(principal.FindAll(ModgudClaimTypes.Permission));
    }
}
