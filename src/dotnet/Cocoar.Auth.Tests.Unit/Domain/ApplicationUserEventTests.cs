using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Domain.Events;

namespace Cocoar.Auth.Tests.Unit.Domain;

/// <summary>
/// Unit tests for ApplicationUser PendingEvents pattern.
/// Verifies that entity setters raise the correct domain events.
/// Pure in-memory tests — no DB, no HTTP, sub-millisecond execution.
/// </summary>
public class ApplicationUserEventTests
{
    private ApplicationUser CreateUser()
    {
        var user = new ApplicationUser("testuser", "test@example.com");
        user.ClearPendingEvents(); // Clear constructor events
        return user;
    }

    // ── Username ──

    [Fact]
    public void SetUserName_WhenChanged_RaisesUserNameChanged()
    {
        var user = CreateUser();
        user.SetUserName("newname");

        var evt = Assert.Single(user.PendingEvents);
        var changed = Assert.IsType<UserNameChanged>(evt);
        Assert.Equal("testuser", changed.OldUserName);
        Assert.Equal("newname", changed.NewUserName);
    }

    [Fact]
    public void SetUserName_WhenSame_RaisesNoEvent()
    {
        var user = CreateUser();
        user.SetUserName("testuser");

        Assert.Empty(user.PendingEvents);
    }

    // ── Email ──

    [Fact]
    public void SetEmail_WhenChanged_RaisesUserEmailChanged()
    {
        var user = CreateUser();
        user.SetEmail("new@example.com");

        var evt = Assert.Single(user.PendingEvents);
        var changed = Assert.IsType<UserEmailChanged>(evt);
        Assert.Equal("test@example.com", changed.OldEmail);
        Assert.Equal("new@example.com", changed.NewEmail);
    }

    [Fact]
    public void SetEmail_WhenSame_RaisesNoEvent()
    {
        var user = CreateUser();
        user.SetEmail("test@example.com");

        Assert.Empty(user.PendingEvents);
    }

    // ── Phone ──

    [Fact]
    public void SetPhoneNumber_WhenChanged_RaisesUserPhoneNumberChanged()
    {
        var user = CreateUser();
        user.SetPhoneNumber("+1234567890");

        var evt = Assert.Single(user.PendingEvents);
        Assert.IsType<UserPhoneNumberChanged>(evt);
    }

    // ── Profile Name ──

    [Fact]
    public void SetFirstName_WhenChanged_RaisesUserProfileNameChanged()
    {
        var user = CreateUser();
        user.SetFirstName("John");

        var evt = Assert.Single(user.PendingEvents);
        var changed = Assert.IsType<UserProfileNameChanged>(evt);
        Assert.Equal("John", changed.NewFirstName);
    }

    [Fact]
    public void SetLastName_WhenChanged_RaisesUserProfileNameChanged()
    {
        var user = CreateUser();
        user.SetLastName("Doe");

        var evt = Assert.Single(user.PendingEvents);
        var changed = Assert.IsType<UserProfileNameChanged>(evt);
        Assert.Equal("Doe", changed.NewLastName);
    }

    [Fact]
    public void SetFirstName_AndLastName_RaisesTwoEvents()
    {
        var user = CreateUser();
        user.SetFirstName("John");
        user.SetLastName("Doe");

        Assert.Equal(2, user.PendingEvents.Count);
        Assert.All(user.PendingEvents, e => Assert.IsType<UserProfileNameChanged>(e));
    }

    // ── Active/Deactivated ──

    [Fact]
    public void SetIsActive_ToFalse_RaisesUserDeactivated()
    {
        var user = CreateUser();
        user.SetIsActive(false);

        var evt = Assert.Single(user.PendingEvents);
        Assert.IsType<UserDeactivated>(evt);
    }

    [Fact]
    public void SetIsActive_ToTrue_WhenAlreadyActive_RaisesNoEvent()
    {
        var user = CreateUser();
        user.SetIsActive(true); // Already active

        Assert.Empty(user.PendingEvents);
    }

    // ── Two-Factor ──

    [Fact]
    public void SetTwoFactorEnabled_ToTrue_RaisesUserTwoFactorEnabled()
    {
        var user = CreateUser();
        user.SetTwoFactorEnabled(true);

        var evt = Assert.Single(user.PendingEvents);
        Assert.IsType<UserTwoFactorEnabled>(evt);
    }

    [Fact]
    public void SetTwoFactorEnabled_ToFalse_WhenAlreadyFalse_RaisesNoEvent()
    {
        var user = CreateUser();
        user.SetTwoFactorEnabled(false);

        Assert.Empty(user.PendingEvents);
    }

    // ── Email/Phone Confirmed ──

    [Fact]
    public void SetEmailConfirmed_FalseToTrue_RaisesUserEmailConfirmed()
    {
        var user = CreateUser();
        user.SetEmailConfirmed(true);

        var evt = Assert.Single(user.PendingEvents);
        Assert.IsType<UserEmailConfirmed>(evt);
    }

    [Fact]
    public void SetEmailConfirmed_TrueToFalse_RaisesNoEvent()
    {
        var user = CreateUser();
        user.SetEmailConfirmed(true);
        user.ClearPendingEvents();
        user.SetEmailConfirmed(false); // Downgrade doesn't raise

        Assert.Empty(user.PendingEvents);
    }

    // ── Roles ──

    [Fact]
    public void AddRole_RaisesUserRoleAssigned()
    {
        var user = CreateUser();
        var roleId = Guid.NewGuid();
        user.AddRole(roleId);

        var evt = Assert.Single(user.PendingEvents);
        var assigned = Assert.IsType<UserRoleAssigned>(evt);
        Assert.Equal(roleId, assigned.RoleId);
    }

    [Fact]
    public void AddRole_Duplicate_RaisesNoEvent()
    {
        var user = CreateUser();
        var roleId = Guid.NewGuid();
        user.AddRole(roleId);
        user.ClearPendingEvents();

        user.AddRole(roleId); // Already has role

        Assert.Empty(user.PendingEvents);
    }

    [Fact]
    public void RemoveRole_RaisesUserRoleRemoved()
    {
        var user = CreateUser();
        var roleId = Guid.NewGuid();
        user.AddRole(roleId);
        user.ClearPendingEvents();

        user.RemoveRole(roleId);

        var evt = Assert.Single(user.PendingEvents);
        var removed = Assert.IsType<UserRoleRemoved>(evt);
        Assert.Equal(roleId, removed.RoleId);
    }

    // ── Claims ──

    [Fact]
    public void AddClaim_RaisesUserClaimAdded()
    {
        var user = CreateUser();
        user.AddClaim("role", "admin");

        var evt = Assert.Single(user.PendingEvents);
        var added = Assert.IsType<UserClaimAdded>(evt);
        Assert.Equal("role", added.ClaimType);
        Assert.Equal("admin", added.ClaimValue);
    }

    [Fact]
    public void RemoveClaim_RaisesUserClaimRemoved()
    {
        var user = CreateUser();
        user.AddClaim("role", "admin");
        user.ClearPendingEvents();

        user.RemoveClaim("role", "admin");

        var evt = Assert.Single(user.PendingEvents);
        Assert.IsType<UserClaimRemoved>(evt);
    }

    [Fact]
    public void ReplaceClaim_RaisesRemoveThenAdd()
    {
        var user = CreateUser();
        user.AddClaim("role", "user");
        user.ClearPendingEvents();

        user.ReplaceClaim("role", "user", "admin");

        Assert.Equal(2, user.PendingEvents.Count);
        Assert.IsType<UserClaimRemoved>(user.PendingEvents[0]);
        Assert.IsType<UserClaimAdded>(user.PendingEvents[1]);
    }

    // ── External Logins ──

    [Fact]
    public void AddLogin_RaisesUserExternalLoginLinked()
    {
        var user = CreateUser();
        user.AddLogin("Google", "key123", "Google");

        var evt = Assert.Single(user.PendingEvents);
        var linked = Assert.IsType<UserExternalLoginLinked>(evt);
        Assert.Equal("Google", linked.ProviderName);
    }

    [Fact]
    public void RemoveLogin_RaisesUserExternalLoginRemoved()
    {
        var user = CreateUser();
        user.AddLogin("Google", "key123", "Google");
        user.ClearPendingEvents();

        user.RemoveLogin("Google", "key123");

        var evt = Assert.Single(user.PendingEvents);
        Assert.IsType<UserExternalLoginRemoved>(evt);
    }

    // ── Password ──

    [Fact]
    public void SetPasswordHash_WhenChanged_RaisesUserPasswordChanged()
    {
        var user = CreateUser();
        user.SetPasswordHash("hash1");
        user.ClearPendingEvents();

        user.SetPasswordHash("hash2");

        var evt = Assert.Single(user.PendingEvents);
        var changed = Assert.IsType<UserPasswordChanged>(evt);
        Assert.Equal(PasswordChangeType.UserChange, changed.ChangeType);
    }

    [Fact]
    public void SetPasswordHash_ToNull_RaisesNoEvent()
    {
        var user = CreateUser();
        user.SetPasswordHash("hash1");
        user.ClearPendingEvents();

        user.SetPasswordHash(null);

        Assert.Empty(user.PendingEvents);
    }

    // ── ClearPendingEvents ──

    [Fact]
    public void ClearPendingEvents_RemovesAllEvents()
    {
        var user = CreateUser();
        user.SetUserName("changed");
        user.SetEmail("changed@test.com");
        Assert.Equal(2, user.PendingEvents.Count);

        user.ClearPendingEvents();

        Assert.Empty(user.PendingEvents);
    }

    // ── Constructor ──

    [Fact]
    public void Constructor_RaisesEventsFromSetters()
    {
        var user = new ApplicationUser("testuser", "test@example.com");

        // Constructor calls SetUserName + SetEmail which raise events
        Assert.True(user.PendingEvents.Count >= 2);
    }

    // ── Lockout ──

    [Fact]
    public void SetLockoutEnd_ToFuture_RaisesUserLockedOut()
    {
        var user = CreateUser();
        var lockoutEnd = DateTimeOffset.UtcNow.AddMinutes(5);
        user.SetLockoutEnd(lockoutEnd);

        var evt = Assert.Single(user.PendingEvents);
        var lockedOut = Assert.IsType<UserLockedOut>(evt);
        Assert.Equal(lockoutEnd, lockedOut.LockoutEnd);
    }

    [Fact]
    public void SetLockoutEnd_ToNull_WhenLocked_RaisesUserUnlocked()
    {
        var user = CreateUser();
        user.SetLockoutEnd(DateTimeOffset.UtcNow.AddMinutes(5));
        user.ClearPendingEvents();

        user.SetLockoutEnd(null);

        var evt = Assert.Single(user.PendingEvents);
        Assert.IsType<UserUnlocked>(evt);
    }

    // ── Expiration ──

    [Fact]
    public void SetExpiresAt_WhenChanged_RaisesUserExpirationChanged()
    {
        var user = CreateUser();
        var expiry = DateTimeOffset.UtcNow.AddDays(30);
        user.SetExpiresAt(expiry);

        var evt = Assert.Single(user.PendingEvents);
        var changed = Assert.IsType<UserExpirationChanged>(evt);
        Assert.Equal(expiry, changed.NewExpiresAt);
    }
}
