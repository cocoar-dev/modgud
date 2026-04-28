using Cocoar.Auth.Application.Notifications;
using Cocoar.Auth.Application.ReadModels;
using Cocoar.Auth.Domain.Events;
using JasperFx.Events;
using Marten;
using Marten.Events.Projections;

namespace Cocoar.Auth.Infrastructure.Persistence.Projections.Async;

/// <summary>
/// Async projection for the admin group list grid.
/// Uses MultiStreamProjection with RaiseSideEffects for automatic SignalR notifications.
/// </summary>
public class GroupListProjection : MultiStreamProjection<GroupListReadModel, Guid>
{
	public GroupListProjection()
	{
		Identity<GroupCreated>(e => e.GroupId);
		Identity<GroupRenamed>(e => e.GroupId);
		Identity<GroupDescriptionChanged>(e => e.GroupId);
		Identity<GroupArchived>(e => e.GroupId);
		Identity<GroupMemberAdded>(e => e.GroupId);
		Identity<GroupMemberRemoved>(e => e.GroupId);
		Identity<GroupChildAdded>(e => e.GroupId);
		Identity<GroupChildRemoved>(e => e.GroupId);
		Identity<GroupRealmRoleGranted>(e => e.GroupId);
		Identity<GroupRealmRoleRevoked>(e => e.GroupId);
		Identity<GroupClientRoleGranted>(e => e.GroupId);
		Identity<GroupClientRoleRevoked>(e => e.GroupId);
	}

	public GroupListReadModel Create(IEvent<GroupCreated> @event)
	{
		return new GroupListReadModel
		{
			Id = @event.Data.GroupId,
			Name = @event.Data.Name,
			Description = @event.Data.Description,
			CreatedAt = @event.Timestamp
		};
	}

	public void Apply(GroupListReadModel model, IEvent<GroupRenamed> @event)
	{
		model.Name = @event.Data.NewName;
		model.ModifiedAt = @event.Timestamp;
	}

	public void Apply(GroupListReadModel model, IEvent<GroupDescriptionChanged> @event)
	{
		model.Description = @event.Data.NewDescription;
		model.ModifiedAt = @event.Timestamp;
	}

	public void Apply(GroupListReadModel model, IEvent<GroupArchived> @event)
	{
		model.IsArchived = true;
		model.ModifiedAt = @event.Timestamp;
	}

	public void Apply(GroupListReadModel model, IEvent<GroupMemberAdded> @event) => model.MemberCount++;
	public void Apply(GroupListReadModel model, IEvent<GroupMemberRemoved> @event) => model.MemberCount = Math.Max(0, model.MemberCount - 1);
	public void Apply(GroupListReadModel model, IEvent<GroupChildAdded> @event) => model.ChildGroupCount++;
	public void Apply(GroupListReadModel model, IEvent<GroupChildRemoved> @event) => model.ChildGroupCount = Math.Max(0, model.ChildGroupCount - 1);
	public void Apply(GroupListReadModel model, IEvent<GroupRealmRoleGranted> @event) => model.RoleGrantCount++;
	public void Apply(GroupListReadModel model, IEvent<GroupRealmRoleRevoked> @event) => model.RoleGrantCount = Math.Max(0, model.RoleGrantCount - 1);
	public void Apply(GroupListReadModel model, IEvent<GroupClientRoleGranted> @event) => model.RoleGrantCount++;
	public void Apply(GroupListReadModel model, IEvent<GroupClientRoleRevoked> @event) => model.RoleGrantCount = Math.Max(0, model.RoleGrantCount - 1);

	public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<GroupListReadModel> slice)
	{
		var changeType = slice.Events().Any(e => e.Data is GroupCreated) ? "created" : "updated";
		slice.PublishMessage(new EntityChangedNotification("group", changeType, slice.Snapshot?.Id.ToString()));
		return ValueTask.CompletedTask;
	}
}
