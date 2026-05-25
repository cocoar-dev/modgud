using Modgud.Authorization.Resources;
using Modgud.Authorization.Setup;

namespace Modgud.Tests.Unit.Resources;

/// <summary>
/// Pins the in-memory <see cref="ResourceRegistry"/> behaviour. Resources are
/// declared at startup via <see cref="AuthorizationOptions.RegisterResource"/>
/// and consumed at runtime by permission validation and the script-editor TS-
/// definition generator — the contract pinned here is shared by both.
/// </summary>
public class ResourceRegistryTests
{
    private const string A = "modgud";

    private static IResourceRegistry NewRegistryWith(params (string App, string Resource, string[] Actions)[] specs)
    {
        var options = new AuthorizationOptions();
        foreach (var (app, resource, actions) in specs)
            options.RegisterResource(app, resource, actions);
        return options.ResourceRegistry;
    }

    public class IsValidPermission
    {
        [Fact]
        public void Returns_true_for_known_app_resource_and_action()
        {
            var registry = NewRegistryWith((A, "user", ["read", "write"]));
            Assert.True(registry.IsValidPermission($"{A}:user:read"));
            Assert.True(registry.IsValidPermission($"{A}:user:write"));
        }

        [Fact]
        public void Returns_false_for_known_resource_but_unknown_action()
        {
            var registry = NewRegistryWith((A, "user", ["read"]));
            Assert.False(registry.IsValidPermission($"{A}:user:write"));
        }

        [Fact]
        public void Returns_false_for_unknown_resource()
        {
            var registry = NewRegistryWith((A, "user", ["read"]));
            Assert.False(registry.IsValidPermission($"{A}:role:read"));
        }

        [Fact]
        public void Returns_false_when_app_does_not_match()
        {
            // Resource "user" exists under modgud but not under timetodo —
            // app-scoping must keep them apart.
            var registry = NewRegistryWith((A, "user", ["read"]));
            Assert.False(registry.IsValidPermission("timetodo:user:read"));
        }

        [Theory]
        [InlineData("user")]                          // missing colons
        [InlineData("user:read")]                     // only two parts (legacy shape)
        [InlineData("modgud:user:read:extra")]   // four parts
        [InlineData(":user:read")]                    // empty app
        [InlineData("modgud::read")]             // empty resource
        [InlineData("modgud:user:")]             // empty action
        public void Returns_false_for_malformed_permissions(string permission)
        {
            var registry = NewRegistryWith((A, "user", ["read"]));
            Assert.False(registry.IsValidPermission(permission));
        }

        [Fact]
        public void Lookup_is_case_sensitive()
        {
            // Resources/actions are intentionally ordinal — drift here would silently
            // accept "User:Read" alongside "user:read" and break the permission grammar.
            var registry = NewRegistryWith((A, "user", ["read"]));
            Assert.False(registry.IsValidPermission($"{A}:User:read"));
            Assert.False(registry.IsValidPermission($"{A}:user:Read"));
        }
    }

    public class IsValidAction
    {
        [Fact]
        public void Returns_true_for_known_app_resource_and_action()
        {
            var registry = NewRegistryWith((A, "user", ["read", "write"]));
            Assert.True(registry.IsValidAction(A, "user", "read"));
            Assert.True(registry.IsValidAction(A, "user", "write"));
        }

        [Fact]
        public void Returns_false_for_unknown_action()
        {
            var registry = NewRegistryWith((A, "user", ["read"]));
            Assert.False(registry.IsValidAction(A, "user", "delete"));
        }

        [Fact]
        public void Returns_false_for_unknown_resource()
        {
            var registry = NewRegistryWith((A, "user", ["read"]));
            Assert.False(registry.IsValidAction(A, "role", "read"));
        }

        [Fact]
        public void Returns_false_for_unknown_app()
        {
            var registry = NewRegistryWith((A, "user", ["read"]));
            Assert.False(registry.IsValidAction("timetodo", "user", "read"));
        }
    }

    public class GetAllPermissions
    {
        [Fact]
        public void Empty_registry_returns_empty_list()
        {
            var registry = NewRegistryWith();
            Assert.Empty(registry.GetAllPermissions());
        }

        [Fact]
        public void Returns_app_resource_action_combinations()
        {
            var registry = NewRegistryWith(
                (A, "user", ["read", "write"]),
                (A, "role", ["read"]),
                ("timetodo", "todo", ["read"]));

            var all = registry.GetAllPermissions();

            Assert.Equal(4, all.Count);
            Assert.Contains($"{A}:user:read", all);
            Assert.Contains($"{A}:user:write", all);
            Assert.Contains($"{A}:role:read", all);
            Assert.Contains("timetodo:todo:read", all);
        }

        [Fact]
        public void Multiple_registrations_for_same_resource_are_merged()
        {
            // Two RegisterResource calls for the same (app, resource) are additive.
            var registry = NewRegistryWith(
                (A, "user", ["read"]),
                (A, "user", ["write", "delete"]));

            var actions = registry.GetActionsForResource(A, "user");

            Assert.Equal(3, actions.Count);
            Assert.Contains("read", actions);
            Assert.Contains("write", actions);
            Assert.Contains("delete", actions);
        }

        [Fact]
        public void Duplicate_action_registrations_do_not_duplicate_in_output()
        {
            var registry = NewRegistryWith(
                (A, "user", ["read"]),
                (A, "user", ["read"]));

            Assert.Single(registry.GetActionsForResource(A, "user"));
        }
    }

    public class GetActionsForResource
    {
        [Fact]
        public void Returns_actions_for_known_app_resource_pair()
        {
            var registry = NewRegistryWith((A, "user", ["read", "write"]));
            var actions = registry.GetActionsForResource(A, "user");

            Assert.Equal(2, actions.Count);
            Assert.Contains("read", actions);
            Assert.Contains("write", actions);
        }

        [Fact]
        public void Returns_empty_list_for_unknown_resource()
        {
            var registry = NewRegistryWith((A, "user", ["read"]));
            Assert.Empty(registry.GetActionsForResource(A, "nope"));
        }

        [Fact]
        public void Returns_empty_list_for_unknown_app_even_with_known_resource()
        {
            var registry = NewRegistryWith((A, "user", ["read"]));
            Assert.Empty(registry.GetActionsForResource("timetodo", "user"));
        }
    }

    public class GetResourceTypes
    {
        [Fact]
        public void Empty_registry_returns_empty()
        {
            var registry = NewRegistryWith();
            Assert.Empty(registry.GetResourceTypes(A));
        }

        [Fact]
        public void Returns_each_distinct_resource_type_for_the_given_app()
        {
            var registry = NewRegistryWith(
                (A, "user", ["read"]),
                (A, "role", ["read"]),
                (A, "user", ["write"]),       // re-registration
                ("timetodo", "todo", ["read"])); // different app, must NOT leak

            var types = registry.GetResourceTypes(A);

            Assert.Equal(2, types.Count);
            Assert.Contains("user", types);
            Assert.Contains("role", types);
            Assert.DoesNotContain("todo", types);
        }
    }

    public class GetAppSlugs
    {
        [Fact]
        public void Empty_registry_returns_empty()
        {
            var registry = NewRegistryWith();
            Assert.Empty(registry.GetAppSlugs());
        }

        [Fact]
        public void Returns_distinct_app_slugs()
        {
            var registry = NewRegistryWith(
                (A, "user", ["read"]),
                (A, "role", ["read"]),
                ("timetodo", "todo", ["read"]),
                ("timetodo", "project", ["read"]));

            var apps = registry.GetAppSlugs();

            Assert.Equal(2, apps.Count);
            Assert.Contains(A, apps);
            Assert.Contains("timetodo", apps);
        }
    }
}
