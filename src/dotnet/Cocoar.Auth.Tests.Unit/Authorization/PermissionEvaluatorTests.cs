using Cocoar.Auth.Authorization.Services;

namespace Cocoar.Auth.Tests.Unit.Authorization;

/// <summary>
/// Pins the security-critical bypass logic of <see cref="PermissionEvaluator.Evaluate"/>.
/// Any change here is a permission-model change — the test names should be readable as
/// the rule statement.
///
/// Permission strings are fully qualified as <c>"appSlug:resource:action"</c>.
/// Bypasses recognised: <c>realm:admin</c> (global), <c>app:admin</c> (per app),
/// <c>app:resource:admin</c> (per resource).
/// </summary>
public class PermissionEvaluatorTests
{
    [Fact]
    public void Empty_grants_grant_nothing()
    {
        Assert.False(PermissionEvaluator.Evaluate([], "cocoar-auth:user:read"));
    }

    [Fact]
    public void Exact_grant_match_passes()
    {
        Assert.True(PermissionEvaluator.Evaluate(["cocoar-auth:user:read"], "cocoar-auth:user:read"));
    }

    [Fact]
    public void Different_action_on_same_resource_does_not_pass()
    {
        Assert.False(PermissionEvaluator.Evaluate(["cocoar-auth:user:read"], "cocoar-auth:user:write"));
    }

    [Fact]
    public void Same_action_on_different_resource_does_not_pass()
    {
        Assert.False(PermissionEvaluator.Evaluate(["cocoar-auth:user:read"], "cocoar-auth:role:read"));
    }

    [Fact]
    public void Same_action_on_different_app_does_not_pass()
    {
        // Apps are isolated namespaces — holding "timetodo:user:read" must not
        // grant "cocoar-auth:user:read", even though the resource+action are
        // identical.
        Assert.False(PermissionEvaluator.Evaluate(["timetodo:user:read"], "cocoar-auth:user:read"));
    }

    [Fact]
    public void Realm_admin_grants_anything()
    {
        var grants = new[] { PermissionEvaluator.RealmAdminPermission };
        Assert.True(PermissionEvaluator.Evaluate(grants, "cocoar-auth:user:read"));
        Assert.True(PermissionEvaluator.Evaluate(grants, "cocoar-auth:oauth-client:write"));
        Assert.True(PermissionEvaluator.Evaluate(grants, "timetodo:todo:delete"));
        Assert.True(PermissionEvaluator.Evaluate(grants, "anything:at:all"));
    }

    [Fact]
    public void App_admin_grants_every_action_in_that_app()
    {
        var grants = new[] { "cocoar-auth:admin" };
        Assert.True(PermissionEvaluator.Evaluate(grants, "cocoar-auth:user:read"));
        Assert.True(PermissionEvaluator.Evaluate(grants, "cocoar-auth:user:write"));
        Assert.True(PermissionEvaluator.Evaluate(grants, "cocoar-auth:oauth-client:delete"));
    }

    [Fact]
    public void App_admin_does_not_leak_to_other_apps()
    {
        var grants = new[] { "cocoar-auth:admin" };
        Assert.False(PermissionEvaluator.Evaluate(grants, "timetodo:todo:read"));
    }

    [Fact]
    public void Resource_admin_grants_every_action_on_that_resource_in_that_app()
    {
        var grants = new[] { "cocoar-auth:oauth-client:admin" };
        Assert.True(PermissionEvaluator.Evaluate(grants, "cocoar-auth:oauth-client:read"));
        Assert.True(PermissionEvaluator.Evaluate(grants, "cocoar-auth:oauth-client:write"));
        Assert.True(PermissionEvaluator.Evaluate(grants, "cocoar-auth:oauth-client:delete"));
    }

    [Fact]
    public void Resource_admin_does_not_leak_to_other_resources()
    {
        var grants = new[] { "cocoar-auth:oauth-client:admin" };
        Assert.False(PermissionEvaluator.Evaluate(grants, "cocoar-auth:oauth-scope:read"));
        Assert.False(PermissionEvaluator.Evaluate(grants, "cocoar-auth:user:read"));
        Assert.False(PermissionEvaluator.Evaluate(grants, "cocoar-auth:oauth:read"));
    }

    [Fact]
    public void Resource_admin_does_not_leak_to_other_apps()
    {
        var grants = new[] { "cocoar-auth:user:admin" };
        Assert.False(PermissionEvaluator.Evaluate(grants, "timetodo:user:read"));
    }

    [Fact]
    public void Resource_admin_does_not_match_a_substring_of_resource_name()
    {
        // Holding `cocoar-auth:oauth:admin` (resource = "oauth") must NOT cover
        // `cocoar-auth:oauth-client:read` (resource = "oauth-client") — they're
        // separate resources, not nested.
        var grants = new[] { "cocoar-auth:oauth:admin" };
        Assert.False(PermissionEvaluator.Evaluate(grants, "cocoar-auth:oauth-client:read"));
    }

    [Fact]
    public void Two_segment_permission_only_passes_on_exact_match_or_realm_admin()
    {
        // Permissions outside the canonical 3-segment shape (e.g. realm:admin
        // itself, or any custom 2-segment grant) only pass via verbatim match.
        Assert.True(PermissionEvaluator.Evaluate(["realm:admin"], "realm:admin"));
        Assert.True(PermissionEvaluator.Evaluate(["custom:thing"], "custom:thing"));
        Assert.False(PermissionEvaluator.Evaluate(["custom:thing"], "custom:other"));
    }

    [Fact]
    public void Multiple_grants_first_match_wins()
    {
        // Order independence: result is the same regardless of grant ordering.
        var grants = new[] { "cocoar-auth:irrelevant:a", "cocoar-auth:user:read", "cocoar-auth:irrelevant:b" };
        Assert.True(PermissionEvaluator.Evaluate(grants, "cocoar-auth:user:read"));
    }

    [Fact]
    public void Hashset_grants_are_treated_identically_to_list()
    {
        var asSet = new HashSet<string> { "cocoar-auth:user:read" };
        Assert.True(PermissionEvaluator.Evaluate(asSet, "cocoar-auth:user:read"));
    }

    [Fact]
    public void Null_permission_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PermissionEvaluator.Evaluate(["cocoar-auth:user:read"], null!));
    }

    [Fact]
    public void Empty_permission_throws_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => PermissionEvaluator.Evaluate(["cocoar-auth:user:read"], ""));
    }

    [Fact]
    public void Null_grants_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PermissionEvaluator.Evaluate(null!, "cocoar-auth:user:read"));
    }
}
