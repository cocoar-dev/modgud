using Cocoar.Auth.Authorization.Access;

namespace Cocoar.Auth.Tests.Unit.Authorization.Access;

/// <summary>
/// Pins the bypass rules on <see cref="UserContext.HasPermission"/>. Delegates
/// to <c>PermissionEvaluator</c>, so script answers and backend
/// <c>RequiresPermission</c> answers are identical for the same principal.
///
/// Permission strings are fully qualified as <c>"appSlug:resource:action"</c>.
/// </summary>
public class UserContextTests
{
    public class HasPermission
    {
        [Fact]
        public void Empty_permissions_grant_nothing()
        {
            var ctx = new UserContext();
            Assert.False(ctx.HasPermission("cocoar-auth:user:read"));
        }

        [Fact]
        public void Exact_match_passes()
        {
            var ctx = new UserContext { Permissions = ["cocoar-auth:user:read"] };
            Assert.True(ctx.HasPermission("cocoar-auth:user:read"));
        }

        [Fact]
        public void Different_permission_does_not_pass()
        {
            var ctx = new UserContext { Permissions = ["cocoar-auth:user:read"] };
            Assert.False(ctx.HasPermission("cocoar-auth:user:write"));
        }

        [Fact]
        public void Realm_admin_grants_anything()
        {
            // The realm-wide bypass: presence of "realm:admin" makes every
            // check return true regardless of app. Any change here is a
            // security-model change.
            var ctx = new UserContext { Permissions = ["realm:admin"] };
            Assert.True(ctx.HasPermission("cocoar-auth:user:read"));
            Assert.True(ctx.HasPermission("cocoar-auth:oauth-client:write"));
            Assert.True(ctx.HasPermission("timetodo:todo:delete"));
            Assert.True(ctx.HasPermission("anything:at:all"));
        }

        [Fact]
        public void Realm_admin_bypass_is_case_sensitive()
        {
            // Defence-in-depth: an attacker-supplied "Realm:Admin" string from a
            // miscoded enricher must NOT trigger the bypass.
            var ctx = new UserContext { Permissions = ["Realm:Admin"] };
            Assert.False(ctx.HasPermission("cocoar-auth:user:read"));
        }

        [Fact]
        public void App_admin_grants_every_action_in_that_app()
        {
            var ctx = new UserContext { Permissions = ["cocoar-auth:admin"] };
            Assert.True(ctx.HasPermission("cocoar-auth:user:read"));
            Assert.True(ctx.HasPermission("cocoar-auth:oauth-client:write"));
        }

        [Fact]
        public void App_admin_does_not_leak_to_other_apps()
        {
            var ctx = new UserContext { Permissions = ["cocoar-auth:admin"] };
            Assert.False(ctx.HasPermission("timetodo:todo:read"));
        }

        [Fact]
        public void Resource_admin_grants_every_action_on_that_resource()
        {
            // Aligned with PermissionEvaluator: holding "cocoar-auth:user:admin"
            // implicitly grants every "cocoar-auth:user:*" action. Scripts and
            // backend RequiresPermission filters agree.
            var ctx = new UserContext { Permissions = ["cocoar-auth:user:admin"] };
            Assert.True(ctx.HasPermission("cocoar-auth:user:read"));
            Assert.True(ctx.HasPermission("cocoar-auth:user:write"));
            Assert.True(ctx.HasPermission("cocoar-auth:user:admin"));
        }

        [Fact]
        public void Resource_admin_does_not_leak_to_other_resources()
        {
            var ctx = new UserContext { Permissions = ["cocoar-auth:user:admin"] };
            Assert.False(ctx.HasPermission("cocoar-auth:oauth-client:read"));
            Assert.False(ctx.HasPermission("cocoar-auth:role:read"));
        }

        [Fact]
        public void Multiple_permissions_any_match_passes()
        {
            var ctx = new UserContext { Permissions = ["cocoar-auth:a:b", "cocoar-auth:user:read", "cocoar-auth:c:d"] };
            Assert.True(ctx.HasPermission("cocoar-auth:user:read"));
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
