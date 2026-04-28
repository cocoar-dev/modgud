using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Domain.Events;

namespace Cocoar.Auth.Tests.Unit.Domain;

/// <summary>
/// Unit tests for ApplicationRole PendingEvents pattern.
/// Pure in-memory tests — no DB, no HTTP, sub-millisecond execution.
/// </summary>
public class ApplicationRoleEventTests
{
    private ApplicationRole CreateRole()
    {
        var role = new ApplicationRole("testrole", "Test role description");
        role.ClearPendingEvents();
        return role;
    }

    [Fact]
    public void SetName_WhenChanged_RaisesRoleNameChanged()
    {
        var role = CreateRole();
        role.SetName("newname");

        var evt = Assert.Single(role.PendingEvents);
        var changed = Assert.IsType<RoleNameChanged>(evt);
        Assert.Equal("testrole", changed.OldName);
        Assert.Equal("newname", changed.NewName);
    }

    [Fact]
    public void SetName_WhenSame_RaisesNoEvent()
    {
        var role = CreateRole();
        role.SetName("testrole");

        Assert.Empty(role.PendingEvents);
    }

    [Fact]
    public void SetDescription_WhenChanged_RaisesRoleDescriptionChanged()
    {
        var role = CreateRole();
        role.SetDescription("New description");

        var evt = Assert.Single(role.PendingEvents);
        var changed = Assert.IsType<RoleDescriptionChanged>(evt);
        Assert.Equal("Test role description", changed.OldDescription);
        Assert.Equal("New description", changed.NewDescription);
    }

    [Fact]
    public void SetDisplayName_WhenChanged_RaisesRoleDisplayNameChanged()
    {
        var role = CreateRole();
        role.SetDisplayName("Display Name");

        var evt = Assert.Single(role.PendingEvents);
        Assert.IsType<RoleDisplayNameChanged>(evt);
    }

    [Fact]
    public void SetEmail_WhenChanged_RaisesRoleEmailChanged()
    {
        var role = CreateRole();
        role.SetEmail("role@example.com");

        var evt = Assert.Single(role.PendingEvents);
        Assert.IsType<RoleEmailChanged>(evt);
    }

    [Fact]
    public void SetClientId_WhenChanged_RaisesRoleClientChanged()
    {
        var role = CreateRole();
        var clientId = Guid.NewGuid();
        role.SetClientId(clientId);

        var evt = Assert.Single(role.PendingEvents);
        var changed = Assert.IsType<RoleClientChanged>(evt);
        Assert.Null(changed.OldClientId);
        Assert.Equal(clientId, changed.NewClientId);
    }

    [Fact]
    public void SetScopes_WhenChanged_RaisesRoleScopesChanged()
    {
        var role = CreateRole();
        role.SetScopes(["openid", "profile"]);

        var evt = Assert.Single(role.PendingEvents);
        var changed = Assert.IsType<RoleScopesChanged>(evt);
        Assert.Equal(["openid", "profile"], changed.NewScopes);
    }

    [Fact]
    public void SetScopes_WhenSame_RaisesNoEvent()
    {
        var role = CreateRole();
        role.SetScopes(["openid"]);
        role.ClearPendingEvents();

        role.SetScopes(["openid"]);

        Assert.Empty(role.PendingEvents);
    }

    [Fact]
    public void AddClaim_RaisesRoleClaimAdded()
    {
        var role = CreateRole();
        role.AddClaim("permission", "read");

        var evt = Assert.Single(role.PendingEvents);
        var added = Assert.IsType<RoleClaimAdded>(evt);
        Assert.Equal("permission", added.ClaimType);
        Assert.Equal("read", added.ClaimValue);
    }

    [Fact]
    public void RemoveClaim_RaisesRoleClaimRemoved()
    {
        var role = CreateRole();
        role.AddClaim("permission", "read");
        role.ClearPendingEvents();

        role.RemoveClaim("permission", "read");

        var evt = Assert.Single(role.PendingEvents);
        Assert.IsType<RoleClaimRemoved>(evt);
    }

    [Fact]
    public void RemoveClaim_NonExistent_RaisesNoEvent()
    {
        var role = CreateRole();
        role.RemoveClaim("nonexistent", "value");

        Assert.Empty(role.PendingEvents);
    }

    [Fact]
    public void Constructor_RaisesEventsFromSetName()
    {
        var role = new ApplicationRole("admin", "Admin role");

        // Constructor calls SetName which raises RoleNameChanged
        Assert.Contains(role.PendingEvents, e => e is RoleNameChanged);
    }
}
