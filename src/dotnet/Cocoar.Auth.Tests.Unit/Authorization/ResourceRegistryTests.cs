using Cocoar.Auth.Domain.Authorization;

namespace Cocoar.Auth.Tests.Unit.Authorization;

/// <summary>
/// Unit tests for the ABAC ResourceRegistry. Verifies that the IDP-specific
/// resource vocabulary is registered correctly and that permission validation
/// behaves as expected. No DB, no HTTP — pure in-memory.
/// </summary>
public class ResourceRegistryTests
{
    public ResourceRegistryTests()
    {
        // Registry is static; ensure it's initialized exactly once even if other
        // test classes have already populated it.
        ResourceRegistry.Initialize();
    }

    [Theory]
    [InlineData("user:read")]
    [InlineData("user:create")]
    [InlineData("user:update")]
    [InlineData("user:delete")]
    [InlineData("user:unlock")]
    [InlineData("user:impersonate")]
    [InlineData("session:read")]
    [InlineData("session:revoke")]
    [InlineData("permission-role:read")]
    [InlineData("permission-role:create")]
    [InlineData("permission-role:update")]
    [InlineData("permission-role:delete")]
    [InlineData("authorization-group:read")]
    [InlineData("authorization-group:create")]
    [InlineData("authorization-group:update")]
    [InlineData("authorization-group:delete")]
    [InlineData("authorization-group:manage-members")]
    [InlineData("authorization-group:manage-roles")]
    [InlineData("authorization-group:edit-scripts")]
    [InlineData("oauth-client:read")]
    [InlineData("oauth-scope:read")]
    [InlineData("oauth-api:read")]
    [InlineData("login-provider:read")]
    [InlineData("realm:read")]
    [InlineData("audit-log:read")]
    [InlineData("tenant:admin")]
    [InlineData("system:admin")]
    public void IsValidPermission_ForRegisteredPermission_ReturnsTrue(string permission)
    {
        Assert.True(ResourceRegistry.IsValidPermission(permission));
    }

    [Theory]
    [InlineData("user:rocket")]              // unknown action
    [InlineData("widget:read")]              // unknown resource
    [InlineData("user")]                     // missing action
    [InlineData("system:manage-tenants")]    // legacy permission, intentionally removed
    [InlineData("app:admin")]                // TimeToDo's super-admin, intentionally not adopted
    [InlineData("")]                         // empty
    [InlineData(":")]                        // structural junk
    public void IsValidPermission_ForUnknownPermission_ReturnsFalse(string permission)
    {
        Assert.False(ResourceRegistry.IsValidPermission(permission));
    }

    [Fact]
    public void Permissions_Constants_AllResolveToValidPermissions()
    {
        // Walk the Permissions class via reflection and verify every const string
        // is something the registry actually knows about. Catches typos at test time.
        var allConsts = typeof(Permissions).GetNestedTypes()
            .SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .Concat(typeof(Permissions).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetValue(null)!))
            .ToList();

        Assert.NotEmpty(allConsts);

        foreach (var perm in allConsts)
        {
            Assert.True(
                ResourceRegistry.IsValidPermission(perm),
                $"Permissions constant '{perm}' is not registered in ResourceRegistry — typo or missing Register call?");
        }
    }

    [Fact]
    public void GetAllPermissions_IncludesAllExpectedPermissions()
    {
        var all = ResourceRegistry.GetAllPermissions();
        Assert.Contains(Permissions.User.Read, all);
        Assert.Contains(Permissions.AuthorizationGroup.EditScripts, all);
        Assert.Contains(Permissions.SystemAdmin, all);
        Assert.Contains(Permissions.TenantAdmin, all);
    }

    [Fact]
    public void GetActionsForResource_ForKnownResource_ReturnsActions()
    {
        var actions = ResourceRegistry.GetActionsForResource("authorization-group");
        Assert.Contains("manage-members", actions);
        Assert.Contains("edit-scripts", actions);
    }

    [Fact]
    public void GetActionsForResource_ForUnknownResource_ReturnsEmpty()
    {
        Assert.Empty(ResourceRegistry.GetActionsForResource("unknown-resource"));
    }
}
