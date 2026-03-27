using Cocoar.Auth.Application.ReadModels;
using Cocoar.Auth.Domain.Events;
using JasperFx.Events;
using Marten;
using Marten.Events.Projections;

namespace Cocoar.Auth.Infrastructure.Persistence.Projections.Async;

/// <summary>
/// Async projection for the admin group list grid.
/// </summary>
public class GroupListProjection : EventProjection
{
	public GroupListReadModel Create(IEvent<GroupCreated> @event)
	{
		return new GroupListReadModel
		{
			Id = @event.StreamId,
			Name = @event.Data.Name,
			Description = @event.Data.Description,
			CreatedAt = @event.Timestamp
		};
	}

	public void Project(IEvent<GroupRenamed> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupListReadModel>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.Name = @event.Data.NewName;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<GroupDescriptionChanged> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupListReadModel>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.Description = @event.Data.NewDescription;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<GroupArchived> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupListReadModel>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.IsArchived = true;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<GroupMemberAdded> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupListReadModel>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.MemberCount++;
		ops.Store(model);
	}

	public void Project(IEvent<GroupMemberRemoved> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupListReadModel>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.MemberCount = Math.Max(0, model.MemberCount - 1);
		ops.Store(model);
	}

	public void Project(IEvent<GroupChildAdded> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupListReadModel>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.ChildGroupCount++;
		ops.Store(model);
	}

	public void Project(IEvent<GroupChildRemoved> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupListReadModel>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.ChildGroupCount = Math.Max(0, model.ChildGroupCount - 1);
		ops.Store(model);
	}

	public void Project(IEvent<GroupRealmRoleGranted> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupListReadModel>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.RoleGrantCount++;
		ops.Store(model);
	}

	public void Project(IEvent<GroupRealmRoleRevoked> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupListReadModel>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.RoleGrantCount = Math.Max(0, model.RoleGrantCount - 1);
		ops.Store(model);
	}

	public void Project(IEvent<GroupClientRoleGranted> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupListReadModel>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.RoleGrantCount++;
		ops.Store(model);
	}

	public void Project(IEvent<GroupClientRoleRevoked> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupListReadModel>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.RoleGrantCount = Math.Max(0, model.RoleGrantCount - 1);
		ops.Store(model);
	}
}
