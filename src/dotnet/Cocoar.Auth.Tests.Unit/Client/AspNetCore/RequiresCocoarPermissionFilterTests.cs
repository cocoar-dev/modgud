using System.Security.Claims;
using Cocoar.Auth.Client.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Cocoar.Auth.Tests.Unit.Client.AspNetCore;

/// <summary>
/// Pins the resource-server-side endpoint filter. The filter reads the
/// "permission" claims that <c>CocoarAuthClaimsTransformation</c> stamped
/// on the principal and runs them through the same
/// <c>PermissionEvaluator</c> the IdP uses — so the resource:admin and
/// realm:admin bypasses are honoured automatically.
/// </summary>
public class RequiresCocoarPermissionFilterTests
{
    private static EndpointFilterInvocationContext NewContext(
        ClaimsPrincipal? user = null)
    {
        var http = new DefaultHttpContext();
        if (user is not null) http.User = user;
        return new DefaultEndpointFilterInvocationContext(http);
    }

    private static ClaimsPrincipal NewPrincipalWithPermissions(params string[] permissions)
    {
        var identity = new ClaimsIdentity("test");
        foreach (var p in permissions)
            identity.AddClaim(new Claim(CocoarAuthClaimsTransformation.PermissionClaimType, p));
        return new ClaimsPrincipal(identity);
    }

    private sealed class CapturingNext
    {
        public bool Called { get; private set; }
        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext _)
        {
            Called = true;
            return ValueTask.FromResult<object?>(Results.Ok("inner-handler-result"));
        }
    }

    [Fact]
    public async Task Anonymous_principal_returns_401()
    {
        var filter = new RequiresCocoarPermissionFilter("policy:write");
        var anon = new ClaimsPrincipal(new ClaimsIdentity());
        var ctx = NewContext(anon);
        var next = new CapturingNext();

        var result = await filter.InvokeAsync(ctx, next.InvokeAsync);

        Assert.False(next.Called);
        Assert.IsAssignableFrom<IResult>(result);
        Assert.IsAssignableFrom<UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task Exact_permission_match_passes_to_next()
    {
        var filter = new RequiresCocoarPermissionFilter("policy:write");
        var ctx = NewContext(NewPrincipalWithPermissions("policy:write"));
        var next = new CapturingNext();

        await filter.InvokeAsync(ctx, next.InvokeAsync);

        Assert.True(next.Called);
    }

    [Fact]
    public async Task Resource_admin_bypass_passes_to_next()
    {
        // policy:admin should cover policy:write — same resource-wide
        // admin tier the IdP applies.
        var filter = new RequiresCocoarPermissionFilter("policy:write");
        var ctx = NewContext(NewPrincipalWithPermissions("policy:admin"));
        var next = new CapturingNext();

        await filter.InvokeAsync(ctx, next.InvokeAsync);

        Assert.True(next.Called);
    }

    [Fact]
    public async Task Realm_admin_bypass_passes_to_next()
    {
        // realm:admin is the global bypass — covers anything.
        var filter = new RequiresCocoarPermissionFilter("policy:write");
        var ctx = NewContext(NewPrincipalWithPermissions("realm:admin"));
        var next = new CapturingNext();

        await filter.InvokeAsync(ctx, next.InvokeAsync);

        Assert.True(next.Called);
    }

    [Fact]
    public async Task Different_resource_admin_does_not_leak()
    {
        // knowledge:admin must NOT cover policy:write — separate resources.
        var filter = new RequiresCocoarPermissionFilter("policy:write");
        var ctx = NewContext(NewPrincipalWithPermissions("knowledge:admin"));
        var next = new CapturingNext();

        var result = await filter.InvokeAsync(ctx, next.InvokeAsync);

        Assert.False(next.Called);
        Assert.IsAssignableFrom<ForbidHttpResult>(result);
    }

    [Fact]
    public async Task Empty_permission_set_returns_403()
    {
        var filter = new RequiresCocoarPermissionFilter("policy:write");
        var ctx = NewContext(NewPrincipalWithPermissions());
        var next = new CapturingNext();

        var result = await filter.InvokeAsync(ctx, next.InvokeAsync);

        Assert.False(next.Called);
        Assert.IsAssignableFrom<ForbidHttpResult>(result);
    }

    [Fact]
    public async Task Wrong_action_on_correct_resource_returns_403()
    {
        // Holding policy:read does not cover policy:write — different actions.
        var filter = new RequiresCocoarPermissionFilter("policy:write");
        var ctx = NewContext(NewPrincipalWithPermissions("policy:read"));
        var next = new CapturingNext();

        var result = await filter.InvokeAsync(ctx, next.InvokeAsync);

        Assert.False(next.Called);
        Assert.IsAssignableFrom<ForbidHttpResult>(result);
    }

    [Fact]
    public void Constructor_rejects_empty_permission_string()
    {
        Assert.Throws<ArgumentException>(() => new RequiresCocoarPermissionFilter(""));
    }
}
