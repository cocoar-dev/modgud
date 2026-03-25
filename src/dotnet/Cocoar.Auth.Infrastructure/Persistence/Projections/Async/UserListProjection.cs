using Cocoar.Auth.Application.ReadModels;
using Cocoar.Auth.Domain.Events;
using JasperFx.Events;
using Marten;
using Marten.Events.Projections;

namespace Cocoar.Auth.Infrastructure.Persistence.Projections.Async;

/// <summary>
/// Async projection that builds <see cref="UserListReadModel"/> for the admin user list grid.
/// Denormalizes role names so the list view needs zero joins.
/// Reacts to both User events and Role events (when a role is renamed, all affected users update).
/// </summary>
public class UserListProjection : EventProjection
{
	public UserListReadModel Create(IEvent<UserCreated> @event)
	{
		var data = @event.Data;
		return new UserListReadModel
		{
			Id = @event.StreamId,
			UserName = data.UserName,
			Email = data.Email,
			FirstName = data.FirstName,
			LastName = data.LastName,
			IsActive = data.IsActive,
			CreatedAt = @event.Timestamp
		};
	}

	public void Project(IEvent<UserNameChanged> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<UserListReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
		if (model is null) return;

		model.UserName = @event.Data.NewUserName;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<UserEmailChanged> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<UserListReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
		if (model is null) return;

		model.Email = @event.Data.NewEmail;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<UserProfileNameChanged> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<UserListReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
		if (model is null) return;

		model.FirstName = @event.Data.NewFirstName;
		model.LastName = @event.Data.NewLastName;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<UserActivated> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<UserListReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
		if (model is null) return;

		model.IsActive = true;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<UserDeactivated> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<UserListReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
		if (model is null) return;

		model.IsActive = false;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<UserDeleted> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<UserListReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
		if (model is null) return;

		model.IsDeleted = true;
		model.IsActive = false;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<UserRestored> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<UserListReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
		if (model is null) return;

		model.IsDeleted = false;
		model.IsActive = true;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<UserTwoFactorEnabled> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<UserListReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
		if (model is null) return;

		model.TwoFactorEnabled = true;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<UserTwoFactorDisabled> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<UserListReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
		if (model is null) return;

		model.TwoFactorEnabled = false;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<UserLockedOut> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<UserListReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
		if (model is null) return;

		model.LockoutEnd = @event.Data.LockoutEnd;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<UserUnlocked> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<UserListReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
		if (model is null) return;

		model.LockoutEnd = null;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	// ── Role assignment: denormalize role name into user list ──

	public void Project(IEvent<UserRoleAssigned> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<UserListReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
		if (model is null) return;

		var role = ops.LoadAsync<RoleState>(@event.Data.RoleId).GetAwaiter().GetResult();
		if (role is not null && !model.Roles.Any(r => r.Id == role.Id))
		{
			model.Roles.Add(new UserListRoleData(role.Id, role.Name));
		}
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<UserRoleRemoved> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<UserListReadModel>(@event.Data.UserId).GetAwaiter().GetResult();
		if (model is null) return;

		model.Roles.RemoveAll(r => r.Id == @event.Data.RoleId);
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	// ── Cross-stream: when a role is renamed, update all affected users ──

	public void Project(IEvent<RoleNameChanged> @event, IDocumentOperations ops)
	{
		var usersWithRole = ops.Query<UserListReadModel>()
			.Where(u => u.Roles.Any(r => r.Id == @event.Data.RoleId))
			.ToList();

		foreach (var user in usersWithRole)
		{
			var idx = user.Roles.FindIndex(r => r.Id == @event.Data.RoleId);
			if (idx >= 0)
			{
				user.Roles[idx] = user.Roles[idx] with { Name = @event.Data.NewName };
				user.ModifiedAt = @event.Timestamp;
				ops.Store(user);
			}
		}
	}
}
