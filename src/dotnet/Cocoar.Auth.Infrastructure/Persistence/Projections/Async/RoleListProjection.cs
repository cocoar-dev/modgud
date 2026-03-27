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
/// </summary>
public class RoleListProjection : EventProjection
{
	public RoleListReadModel Create(IEvent<RoleCreated> @event)
	{
		var data = @event.Data;
		return new RoleListReadModel
		{
			Id = @event.StreamId,
			Name = data.Name,
			Description = data.Description,
			ClientId = data.ClientId,
			CreatedAt = @event.Timestamp
		};
	}

	public void Project(IEvent<RoleNameChanged> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<RoleListReadModel>(@event.Data.RoleId).GetAwaiter().GetResult();
		if (model is null) return;

		model.Name = @event.Data.NewName;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<RoleDescriptionChanged> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<RoleListReadModel>(@event.Data.RoleId).GetAwaiter().GetResult();
		if (model is null) return;

		model.Description = @event.Data.NewDescription;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<RoleDisplayNameChanged> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<RoleListReadModel>(@event.Data.RoleId).GetAwaiter().GetResult();
		if (model is null) return;

		model.DisplayName = @event.Data.NewDisplayName;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<RoleDeleted> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<RoleListReadModel>(@event.Data.RoleId).GetAwaiter().GetResult();
		if (model is null) return;

		model.IsDeleted = true;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	// ── Cross-stream: user role assignments update the user count ──

	public void Project(IEvent<UserRoleAssigned> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<RoleListReadModel>(@event.Data.RoleId).GetAwaiter().GetResult();
		if (model is null) return;

		model.UserCount++;
		ops.Store(model);
	}

	public void Project(IEvent<UserRoleRemoved> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<RoleListReadModel>(@event.Data.RoleId).GetAwaiter().GetResult();
		if (model is null) return;

		model.UserCount = Math.Max(0, model.UserCount - 1);
		ops.Store(model);
	}
}
