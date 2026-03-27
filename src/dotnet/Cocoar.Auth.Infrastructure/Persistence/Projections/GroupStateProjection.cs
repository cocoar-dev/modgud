using Cocoar.Auth.Application.Models;
using Cocoar.Auth.Domain.Events;
using JasperFx.Events;
using Marten;
using Marten.Events.Projections;

namespace Cocoar.Auth.Infrastructure.Persistence.Projections;

/// <summary>
/// Inline projection for group state — used for command validation.
/// </summary>
public class GroupStateProjection : EventProjection
{
	public GroupState Create(IEvent<GroupCreated> @event)
	{
		return new GroupState
		{
			Id = @event.StreamId,
			Name = @event.Data.Name,
			Description = @event.Data.Description,
			CreatedAt = @event.Timestamp
		};
	}

	public void Project(IEvent<GroupRenamed> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupState>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.Name = @event.Data.NewName;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<GroupDescriptionChanged> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupState>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.Description = @event.Data.NewDescription;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<GroupArchived> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupState>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.IsArchived = true;
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<GroupMemberAdded> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupState>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		if (!model.MemberIds.Contains(@event.Data.UserId))
			model.MemberIds.Add(@event.Data.UserId);
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<GroupMemberRemoved> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupState>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.MemberIds.Remove(@event.Data.UserId);
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<GroupChildAdded> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupState>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		if (!model.ChildGroupIds.Contains(@event.Data.ChildGroupId))
			model.ChildGroupIds.Add(@event.Data.ChildGroupId);
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<GroupChildRemoved> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupState>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.ChildGroupIds.Remove(@event.Data.ChildGroupId);
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<GroupRealmRoleGranted> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupState>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		if (!model.RealmRoleGrants.Any(g => g.RoleId == @event.Data.RoleId))
			model.RealmRoleGrants.Add(new GroupRealmRoleGrantData(@event.Data.RoleId));
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<GroupRealmRoleRevoked> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupState>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.RealmRoleGrants.RemoveAll(g => g.RoleId == @event.Data.RoleId);
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<GroupClientRoleGranted> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupState>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		if (!model.ClientRoleGrants.Any(g => g.RoleId == @event.Data.RoleId && g.ClientId == @event.Data.ClientId))
			model.ClientRoleGrants.Add(new GroupClientRoleGrantData(@event.Data.RoleId, @event.Data.ClientId));
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}

	public void Project(IEvent<GroupClientRoleRevoked> @event, IDocumentOperations ops)
	{
		var model = ops.LoadAsync<GroupState>(@event.Data.GroupId).GetAwaiter().GetResult();
		if (model is null) return;
		model.ClientRoleGrants.RemoveAll(g => g.RoleId == @event.Data.RoleId && g.ClientId == @event.Data.ClientId);
		model.ModifiedAt = @event.Timestamp;
		ops.Store(model);
	}
}
