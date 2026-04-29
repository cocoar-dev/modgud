using Cocoar.Auth.Authorization.Services;

namespace Cocoar.Auth.Tests.Unit.Authorization;

/// <summary>
/// Pins the security-critical bypass logic of <see cref="PermissionEvaluator.Evaluate"/>.
/// Any change here is a permission-model change — the test names should be readable as
/// the rule statement.
/// </summary>
public class PermissionEvaluatorTests
{
    [Fact]
    public void Empty_grants_grant_nothing()
    {
        Assert.False(PermissionEvaluator.Evaluate([], "user:read"));
    }

    [Fact]
    public void Exact_grant_match_passes()
    {
        Assert.True(PermissionEvaluator.Evaluate(["user:read"], "user:read"));
    }

    [Fact]
    public void Different_action_on_same_resource_does_not_pass()
    {
        Assert.False(PermissionEvaluator.Evaluate(["user:read"], "user:write"));
    }

    [Fact]
    public void Same_action_on_different_resource_does_not_pass()
    {
        Assert.False(PermissionEvaluator.Evaluate(["user:read"], "role:read"));
    }

    [Fact]
    public void Global_app_admin_grants_anything()
    {
        var grants = new[] { PermissionEvaluator.GlobalAdminPermission };
        Assert.True(PermissionEvaluator.Evaluate(grants, "user:read"));
        Assert.True(PermissionEvaluator.Evaluate(grants, "oauth-client:write"));
        Assert.True(PermissionEvaluator.Evaluate(grants, "anything:at:all"));
    }

    [Fact]
    public void Resource_admin_grants_every_action_on_that_resource()
    {
        var grants = new[] { "oauth-client:admin" };
        Assert.True(PermissionEvaluator.Evaluate(grants, "oauth-client:read"));
        Assert.True(PermissionEvaluator.Evaluate(grants, "oauth-client:write"));
        Assert.True(PermissionEvaluator.Evaluate(grants, "oauth-client:delete"));
    }

    [Fact]
    public void Resource_admin_does_not_leak_to_other_resources()
    {
        var grants = new[] { "oauth-client:admin" };
        Assert.False(PermissionEvaluator.Evaluate(grants, "oauth-scope:read"));
        Assert.False(PermissionEvaluator.Evaluate(grants, "user:read"));
        Assert.False(PermissionEvaluator.Evaluate(grants, "oauth:read"));
    }

    [Fact]
    public void Resource_admin_does_not_match_a_substring_of_resource_name()
    {
        // Holding `oauth:admin` (resource = "oauth") must NOT cover `oauth-client:read`
        // (resource = "oauth-client") — they're separate resources, not nested.
        var grants = new[] { "oauth:admin" };
        Assert.False(PermissionEvaluator.Evaluate(grants, "oauth-client:read"));
    }

    [Fact]
    public void Permission_without_colon_only_passes_on_exact_match()
    {
        Assert.True(PermissionEvaluator.Evaluate(["bare-permission"], "bare-permission"));
        Assert.False(PermissionEvaluator.Evaluate(["bare-permission"], "other"));
    }

    [Fact]
    public void Multiple_grants_first_match_wins()
    {
        // Order independence: result is the same regardless of grant ordering.
        var grants = new[] { "irrelevant:a", "user:read", "irrelevant:b" };
        Assert.True(PermissionEvaluator.Evaluate(grants, "user:read"));
    }

    [Fact]
    public void Hashset_grants_are_treated_identically_to_list()
    {
        var asSet = new HashSet<string> { "user:read" };
        Assert.True(PermissionEvaluator.Evaluate(asSet, "user:read"));
    }

    [Fact]
    public void Null_permission_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PermissionEvaluator.Evaluate(["user:read"], null!));
    }

    [Fact]
    public void Empty_permission_throws_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => PermissionEvaluator.Evaluate(["user:read"], ""));
    }

    [Fact]
    public void Null_grants_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PermissionEvaluator.Evaluate(null!, "user:read"));
    }
}
