using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Modgud.AspNetCore.ResourceServer;

namespace Modgud.Tests.Unit.ResourceServer;

public class ModgudPermissionExtensionsTests
{
    [Fact]
    public void Policy_requires_authentication_and_the_exact_permission()
    {
        var policy = ModgudPermissionExtensions.BuildPolicy("policy:write");

        Assert.Contains(policy.Requirements, x => x is DenyAnonymousAuthorizationRequirement);
        var claim = Assert.Single(policy.Requirements.OfType<ClaimsAuthorizationRequirement>());
        Assert.Equal(ModgudClaimTypes.Permission, claim.ClaimType);
        Assert.Equal(["policy:write"], claim.AllowedValues);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Empty_permission_is_rejected(string permission)
    {
        Assert.Throws<ArgumentException>(() => ModgudPermissionExtensions.BuildPolicy(permission));
    }
}
