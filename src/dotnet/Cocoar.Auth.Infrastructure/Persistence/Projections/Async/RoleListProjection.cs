using Cocoar.Auth.Application.Notifications;
using Cocoar.Auth.Application.ReadModels;
using Cocoar.Auth.Domain.Events;
using JasperFx.Events;
using Marten;
using Marten.Events.Projections;

namespace Cocoar.Auth.Infrastructure.Persistence.Projections.Async;

/// <summary>
/// Async projection that builds <see cref="RoleListReadModel"/> for the admin role list grid.
/// Maintains a pre-computed user count per role.
/// Reacts to both Role events and User role assignment events.
/// Uses MultiStreamProjection with RaiseSideEffects for automatic SignalR notifications.
/// </summary>
public class RoleListProjection : MultiStreamProjection<RoleListReadModel, Guid>
{
	public RoleListProjection()
	{
		// Role events: stream ID = role ID = document ID
		Identity<RoleCreated>(e => e.RoleId);
		Identity<RoleNameChanged>(e => e.RoleId);
		Identity<RoleDescriptionChanged>(e => e.RoleId);
		Identity<RoleDisplayNameChanged>(e => e.RoleId);
		Identity<RoleDeleted>(e => e.RoleId);

		// Cross-stream: user events that update the role's user count
		Identity<UserRoleAssigned>(e => e.RoleId);
		Identity<UserRoleRemoved>(e => e.RoleId);
	}

	public RoleListReadModel Create(IEvent<RoleCreated> @event)
	{
		var data = @event.Data;
		return new RoleListReadModel
		{
			Id = data.RoleId,
			Name = data.Name,
			Description = data.Description,
			ClientId = data.ClientId,
			CreatedAt = @event.Timestamp
		};
	}

	public void Apply(RoleListReadModel model, IEvent<RoleNameChanged> @event)
	{
		model.Name = @event.Data.NewName;
		model.ModifiedAt = @event.Timestamp;
	}

	public void Apply(RoleListReadModel model, IEvent<RoleDescriptionChanged> @event)
	{
		model.Description = @event.Data.NewDescription;
		model.ModifiedAt = @event.Timestamp;
	}

	public void Apply(RoleListReadModel model, IEvent<RoleDisplayNameChanged> @event)
	{
		model.DisplayName = @event.Data.NewDisplayName;
		model.ModifiedAt = @event.Timestamp;
	}

	public void Apply(RoleListReadModel model, IEvent<RoleDeleted> @event)
	{
		model.IsDeleted = true;
		model.ModifiedAt = @event.Timestamp;
	}

	// Cross-stream: user role assignments update the user count
	public void Apply(RoleListReadModel model, IEvent<UserRoleAssigned> @event) => model.UserCount++;
	public void Apply(RoleListReadModel model, IEvent<UserRoleRemoved> @event) => model.UserCount = Math.Max(0, model.UserCount - 1);

	public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<RoleListReadModel> slice)
	{
		string changeType;
		if (slice.Events().Any(e => e.Data is RoleCreated)) changeType = "created";
		else if (slice.Events().Any(e => e.Data is RoleDeleted)) changeType = "deleted";
		else changeType = "updated";

		slice.PublishMessage(new EntityChangedNotification("role", changeType, slice.Snapshot?.Id.ToString()));
		return ValueTask.CompletedTask;
	}
}
