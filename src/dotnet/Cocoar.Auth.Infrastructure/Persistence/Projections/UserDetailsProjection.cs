using Cocoar.Auth.Application.Models;
using Cocoar.Auth.Domain.Events;
using JasperFx.Events;
using Marten;
using Marten.Events.Projections;

namespace Cocoar.Auth.Infrastructure.Persistence.Projections;

/// <summary>
/// Projection that builds <see cref="UserDetailsReadModel"/> with denormalized role data.
/// Queries inline projections (RoleState) to embed role information.
/// Uses EventProjection because cross-stream handlers (UserRoleAssigned, RoleNameChanged,
/// RoleDescriptionChanged) require IDocumentOperations — not supported in MultiStreamProjection's Apply().
/// </summary>
public class UserDetailsProjection : EventProjection
{
    public UserDetailsReadModel Create(IEvent<UserCreated> @event)
    {
        var data = @event.Data;
        return new UserDetailsReadModel
        {
            Id = @event.StreamId,
            UserName = data.UserName,
            Email = data.Email,
            PhoneNumber = data.PhoneNumber,
            FirstName = data.FirstName,
            LastName = data.LastName,
            IsActive = data.IsActive,
            LockoutEnabled = data.LockoutEnabled,
            AccessFailedCount = 0,
            CreatedAt = @event.Timestamp
        };
    }

    public void Project(IEvent<UserNameChanged> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.UserName = @event.Data.NewUserName;
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    public void Project(IEvent<UserEmailChanged> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.Email = @event.Data.NewEmail;
        model.EmailConfirmed = false;
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    public void Project(IEvent<UserPhoneNumberChanged> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.PhoneNumber = @event.Data.NewPhoneNumber;
        model.PhoneNumberConfirmed = false;
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    public void Project(IEvent<UserProfileNameChanged> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.FirstName = @event.Data.NewFirstName;
        model.LastName = @event.Data.NewLastName;
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    public void Project(IEvent<UserExpirationChanged> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.ExpiresAt = @event.Data.NewExpiresAt;
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    public void Project(IEvent<UserActivated> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.IsActive = true;
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    public void Project(IEvent<UserDeactivated> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.IsActive = false;
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    public void Project(IEvent<UserDeleted> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.IsDeleted = true;
        model.IsActive = false;
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    /// <summary>
    /// When a role is assigned, query RoleState to get full role info for denormalization.
    /// </summary>
    public void Project(IEvent<UserRoleAssigned> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        var role = ops.LoadAsync<RoleState>(@event.Data.RoleId).GetAwaiter().GetResult();
        if (role is not null && !model.Roles.Any(r => r.Id == role.Id))
        {
            model.Roles.Add(new RoleInfo
            {
                Id = role.Id,
                Name = role.Name,
                Description = role.Description
            });
        }
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    public void Project(IEvent<UserRoleRemoved> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        var roleToRemove = model.Roles.FirstOrDefault(r => r.Id == @event.Data.RoleId);
        if (roleToRemove is not null)
        {
            model.Roles.Remove(roleToRemove);
        }
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    public void Project(IEvent<UserClaimAdded> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        var claim = new ClaimInfo(@event.Data.ClaimType, @event.Data.ClaimValue);
        if (!model.Claims.Contains(claim))
        {
            model.Claims.Add(claim);
        }
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    public void Project(IEvent<UserClaimRemoved> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        var claimToRemove = model.Claims.FirstOrDefault(c => c.Type == @event.Data.ClaimType && c.Value == @event.Data.ClaimValue);
        if (claimToRemove is not null)
        {
            model.Claims.Remove(claimToRemove);
        }
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    public void Project(IEvent<UserTwoFactorEnabled> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.TwoFactorEnabled = true;
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    public void Project(IEvent<UserTwoFactorDisabled> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.TwoFactorEnabled = false;
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    public void Project(IEvent<UserLockedOut> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.LockoutEnd = @event.Data.LockoutEnd;
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    public void Project(IEvent<UserUnlocked> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.LockoutEnd = null;
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    public void Project(IEvent<UserEmailConfirmed> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.EmailConfirmed = true;
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    public void Project(IEvent<UserPhoneNumberConfirmed> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserDetailsReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.PhoneNumberConfirmed = true;
        model.ModifiedAt = @event.Timestamp;
        ops.Store(model);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ROLE CHANGE HANDLERS - Update all affected users
    // ═══════════════════════════════════════════════════════════════════════════

    public void Project(IEvent<RoleNameChanged> @event, IDocumentOperations ops)
    {
        var usersWithRole = ops.Query<UserDetailsReadModel>()
            .Where(u => u.Roles.Any(r => r.Id == @event.Data.RoleId))
            .ToList();

        foreach (var user in usersWithRole)
        {
            var role = user.Roles.FirstOrDefault(r => r.Id == @event.Data.RoleId);
            if (role != null)
            {
                role.Name = @event.Data.NewName;
                user.ModifiedAt = @event.Timestamp;
                ops.Store(user);
            }
        }
    }

    public void Project(IEvent<RoleDescriptionChanged> @event, IDocumentOperations ops)
    {
        var usersWithRole = ops.Query<UserDetailsReadModel>()
            .Where(u => u.Roles.Any(r => r.Id == @event.Data.RoleId))
            .ToList();

        foreach (var user in usersWithRole)
        {
            var role = user.Roles.FirstOrDefault(r => r.Id == @event.Data.RoleId);
            if (role != null)
            {
                role.Description = @event.Data.NewDescription;
                user.ModifiedAt = @event.Timestamp;
                ops.Store(user);
            }
        }
    }

    // NOTE: RoleDeleted handler is NOT needed because:
    // - Business rule prevents deleting roles with assigned users
    // - Roles use soft delete (IsDeleted flag) not hard delete
    // - If a role is deleted, it means no users have it assigned
}
