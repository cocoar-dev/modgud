using System.Security.Claims;
using Cocoar.Auth.Client.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Cocoar.Auth.Tests.Unit.Client.AspNetCore;

/// <summary>
/// Pins the resource-server-side endpoint filter. The filter reads the
/// <c>"permission"</c> claims that
/// <see cref="CocoarAuthClaimsTransformation"/> stamped on the principal
/// (flattened from <c>resource_access[<audience>].permissions</c>) and
/// does pure exact-match against the requested string.
///
/// <para>The IdP pre-expanded bypass tiers (<c>realm:admin</c>,
/// <c>&lt;r&gt;:admin</c>) before emission, so the filter does NOT know
/// about admin bypasses — they're already represented as concrete
/// permissions in the claim set. A user with <c>policy:admin</c>
/// upstream sees <c>policy:read</c>, <c>policy:write</c>, ... materialised
/// in the principal claims; the filter just checks membership.</para>
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
    public async Task Pre_expanded_admin_bypass_passes_to_next()
    {
        // The IdP already expanded policy:admin upstream into policy:read,
        // policy:write, policy:admin (every <r>:<a> in the catalog) before
        // putting them in resource_access. From the filter's perspective
        // it's just exact-match against the materialised list.
        var filter = new RequiresCocoarPermissionFilter("policy:write");
        var ctx = NewContext(NewPrincipalWithPermissions(
            "policy:read", "policy:write", "policy:admin"));
        var next = new CapturingNext();

        await filter.InvokeAsync(ctx, next.InvokeAsync);

        Assert.True(next.Called);
    }

    [Fact]
    public async Task Lone_admin_marker_does_not_grant_other_actions()
    {
        // If the principal somehow only has the bare "policy:admin" claim
        // (e.g. because the IdP didn't pre-expand, or the test didn't
        // simulate it), the filter does NOT bypass — exact-match only.
        // This pins that the filter doesn't accidentally implement
        // bypass semantics on top of an already-expanded source.
        var filter = new RequiresCocoarPermissionFilter("policy:write");
        var ctx = NewContext(NewPrincipalWithPermissions("policy:admin"));
        var next = new CapturingNext();

        var result = await filter.InvokeAsync(ctx, next.InvokeAsync);

        Assert.False(next.Called);
        Assert.IsAssignableFrom<ForbidHttpResult>(result);
    }

    [Fact]
    public async Task Different_resource_does_not_leak()
    {
        // Holding knowledge:write must NOT cover policy:write.
        var filter = new RequiresCocoarPermissionFilter("policy:write");
        var ctx = NewContext(NewPrincipalWithPermissions("knowledge:write"));
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
