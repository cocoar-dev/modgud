using Cocoar.Auth.Authorization.Access;

namespace Cocoar.Auth.Tests.Unit.Authorization.Access;

/// <summary>
/// Pins the security-critical bypass rule on <see cref="UserContext.HasPermission"/>.
/// The string <c>"app:admin"</c> is THE global escape hatch in the policy engine —
/// the test names should read as the rule statement.
/// </summary>
public class UserContextTests
{
    public class HasPermission
    {
        [Fact]
        public void Empty_permissions_grant_nothing()
        {
            var ctx = new UserContext();
            Assert.False(ctx.HasPermission("user:read"));
        }

        [Fact]
        public void Exact_match_passes()
        {
            var ctx = new UserContext { Permissions = ["user:read"] };
            Assert.True(ctx.HasPermission("user:read"));
        }

        [Fact]
        public void Different_permission_does_not_pass()
        {
            var ctx = new UserContext { Permissions = ["user:read"] };
            Assert.False(ctx.HasPermission("user:write"));
        }

        [Fact]
        public void Global_app_admin_grants_anything()
        {
            // The bypass: presence of "app:admin" makes every check return true.
            // Any change here is a security-model change.
            var ctx = new UserContext { Permissions = ["app:admin"] };
            Assert.True(ctx.HasPermission("user:read"));
            Assert.True(ctx.HasPermission("oauth-client:write"));
            Assert.True(ctx.HasPermission("anything:at:all"));
            Assert.True(ctx.HasPermission("bare-permission"));
        }

        [Fact]
        public void App_admin_bypass_is_case_sensitive()
        {
            // Defence-in-depth: an attacker-supplied "App:Admin" string from a
            // miscoded enricher must NOT trigger the bypass.
            var ctx = new UserContext { Permissions = ["App:Admin"] };
            Assert.False(ctx.HasPermission("user:read"));
        }

        [Fact]
        public void Resource_specific_admin_does_not_grant_other_resources()
        {
            // UserContext.HasPermission is exact-match only — wildcard semantics
            // (e.g. "user:admin" granting "user:write") are the PermissionEvaluator's
            // job, NOT this class's. Regression here would silently widen access.
            var ctx = new UserContext { Permissions = ["user:admin"] };
            Assert.False(ctx.HasPermission("user:read"));
            Assert.True(ctx.HasPermission("user:admin"));
        }

        [Fact]
        public void Multiple_permissions_any_match_passes()
        {
            var ctx = new UserContext { Permissions = ["a:b", "user:read", "c:d"] };
            Assert.True(ctx.HasPermission("user:read"));
        }
    }

    public class Defaults
    {
        [Fact]
        public void Collections_default_to_empty_not_null()
        {
            // Scripts iterate these without null checks — defaults must be empty lists.
            var ctx = new UserContext();
            Assert.NotNull(ctx.Permissions);
            Assert.NotNull(ctx.Groups);
            Assert.NotNull(ctx.GroupIds);
            Assert.Empty(ctx.Permissions);
            Assert.Empty(ctx.Groups);
            Assert.Empty(ctx.GroupIds);
        }
    }
}
