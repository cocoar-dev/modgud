using Cocoar.Auth.Authorization.Resources;
using Cocoar.Auth.Authorization.Setup;

namespace Cocoar.Auth.Tests.Unit.Resources;

/// <summary>
/// Pins the in-memory <see cref="ResourceRegistry"/> behaviour. Resources are
/// declared at startup via <see cref="AuthorizationOptions.RegisterResource"/>
/// and consumed at runtime by permission validation and the script-editor TS-
/// definition generator — the contract pinned here is shared by both.
/// </summary>
public class ResourceRegistryTests
{
    private static IResourceRegistry NewRegistryWith(params (string Resource, string[] Actions)[] specs)
    {
        var options = new AuthorizationOptions();
        foreach (var (resource, actions) in specs)
            options.RegisterResource(resource, actions);
        return options.ResourceRegistry;
    }

    public class IsValidPermission
    {
        [Fact]
        public void Returns_true_for_known_resource_and_known_action()
        {
            var registry = NewRegistryWith(("user", ["read", "write"]));
            Assert.True(registry.IsValidPermission("user:read"));
            Assert.True(registry.IsValidPermission("user:write"));
        }

        [Fact]
        public void Returns_false_for_known_resource_but_unknown_action()
        {
            var registry = NewRegistryWith(("user", ["read"]));
            Assert.False(registry.IsValidPermission("user:write"));
        }

        [Fact]
        public void Returns_false_for_unknown_resource()
        {
            var registry = NewRegistryWith(("user", ["read"]));
            Assert.False(registry.IsValidPermission("role:read"));
        }

        [Theory]
        [InlineData("user")]                  // missing colon
        [InlineData("user:read:extra")]       // too many parts
        [InlineData(":read")]                 // empty resource
        [InlineData("user:")]                 // empty action
        public void Returns_false_for_malformed_permissions(string permission)
        {
            var registry = NewRegistryWith(("user", ["read"]));
            Assert.False(registry.IsValidPermission(permission));
        }

        [Fact]
        public void Lookup_is_case_sensitive()
        {
            // Resources/actions are intentionally ordinal — drift here would silently
            // accept "User:Read" alongside "user:read" and break the permission grammar.
            var registry = NewRegistryWith(("user", ["read"]));
            Assert.False(registry.IsValidPermission("User:read"));
            Assert.False(registry.IsValidPermission("user:Read"));
        }
    }

    public class IsValidAction
    {
        [Fact]
        public void Returns_true_for_known_resource_and_action()
        {
            var registry = NewRegistryWith(("user", ["read", "write"]));
            Assert.True(registry.IsValidAction("user", "read"));
            Assert.True(registry.IsValidAction("user", "write"));
        }

        [Fact]
        public void Returns_false_for_unknown_action()
        {
            var registry = NewRegistryWith(("user", ["read"]));
            Assert.False(registry.IsValidAction("user", "delete"));
        }

        [Fact]
        public void Returns_false_for_unknown_resource()
        {
            var registry = NewRegistryWith(("user", ["read"]));
            Assert.False(registry.IsValidAction("role", "read"));
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
        public void Returns_resource_action_combinations()
        {
            var registry = NewRegistryWith(
                ("user", ["read", "write"]),
                ("role", ["read"]));

            var all = registry.GetAllPermissions();

            Assert.Equal(3, all.Count);
            Assert.Contains("user:read", all);
            Assert.Contains("user:write", all);
            Assert.Contains("role:read", all);
        }

        [Fact]
        public void Multiple_registrations_for_same_resource_are_merged()
        {
            // Two RegisterResource calls for "user" are additive — last call doesn't replace.
            var registry = NewRegistryWith(
                ("user", ["read"]),
                ("user", ["write", "delete"]));

            var actions = registry.GetActionsForResource("user");

            Assert.Equal(3, actions.Count);
            Assert.Contains("read", actions);
            Assert.Contains("write", actions);
            Assert.Contains("delete", actions);
        }

        [Fact]
        public void Duplicate_action_registrations_do_not_duplicate_in_output()
        {
            var registry = NewRegistryWith(
                ("user", ["read"]),
                ("user", ["read"]));

            Assert.Single(registry.GetActionsForResource("user"));
        }
    }

    public class GetActionsForResource
    {
        [Fact]
        public void Returns_actions_for_known_resource()
        {
            var registry = NewRegistryWith(("user", ["read", "write"]));
            var actions = registry.GetActionsForResource("user");

            Assert.Equal(2, actions.Count);
            Assert.Contains("read", actions);
            Assert.Contains("write", actions);
        }

        [Fact]
        public void Returns_empty_list_for_unknown_resource()
        {
            var registry = NewRegistryWith(("user", ["read"]));
            Assert.Empty(registry.GetActionsForResource("nope"));
        }
    }

    public class GetResourceTypes
    {
        [Fact]
        public void Empty_registry_returns_empty()
        {
            var registry = NewRegistryWith();
            Assert.Empty(registry.GetResourceTypes());
        }

        [Fact]
        public void Returns_each_distinct_registered_resource_type()
        {
            var registry = NewRegistryWith(
                ("user", ["read"]),
                ("role", ["read"]),
                ("user", ["write"])); // re-registration, still one resource type

            var types = registry.GetResourceTypes();

            Assert.Equal(2, types.Count);
            Assert.Contains("user", types);
            Assert.Contains("role", types);
        }
    }
}
