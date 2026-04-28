using TimeToDo.Authorization.Principals;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events;
using Marten.Events.Aggregation;
using TimeToDo.Domain.Comments.Events;
using TimeToDo.Infrastructure.Events;

namespace TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;

public class CommentViewProjection : SingleStreamProjection<CommentView, Guid>
{
    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<CommentView> slice)
    {
        var snapshot = slice.Snapshot;
        if (snapshot is null || !ProjectionSideEffects.Enabled)
            return ValueTask.CompletedTask;

        var action = slice.Events().Any(e => e.Data is CommentDeletedEvent)
            ? SignalRDispatchAction.Deleted
            : slice.Events().Any(e => e.Data is CommentCreatedEvent or CommentMigratedEvent)
                ? SignalRDispatchAction.Created
                : SignalRDispatchAction.Updated;

        slice.PublishMessage(new CommentViewSignalRDispatch(action, snapshot, snapshot.Id));

        return ValueTask.CompletedTask;
    }

    public async Task<CommentView> Create(CommentCreatedEvent @event, IQuerySession session)
    {
        return new CommentView
        {
            Id = @event.Id,
            Description = @event.Description,
            ReferencedItemId = @event.ReferencedItemId,
            ReferencedItemType = @event.ReferencedItemType,
            CreatedAt = @event.CreatedAt,
            CreatedBy = await BuildPrincipalRefAsync(session, @event.CreatedById),
            IsDeleted = false
        };
    }

    public async Task<CommentView> Create(CommentMigratedEvent @event, IQuerySession session)
    {
        return new CommentView
        {
            Id = @event.Id,
            Description = @event.Description,
            ReferencedItemId = @event.ReferencedItemId,
            ReferencedItemType = @event.ReferencedItemType,
            CreatedAt = @event.CreatedAt,
            CreatedBy = await BuildPrincipalRefAsync(session, @event.CreatedById),
            UpdatedAt = @event.UpdatedAt,
            UpdatedById = @event.UpdatedById,
            IsDeleted = false
        };
    }

    public CommentView Apply(CommentDeletedEvent @event, CommentView current)
    {
        return current with { IsDeleted = true };
    }

    /// <summary>
    /// Build a ViewRef for a principal from the unified Principal projection (inline,
    /// always consistent). Works for both humans and groups.
    /// </summary>
    private static async Task<ViewRef> BuildPrincipalRefAsync(IQuerySession session, Guid principalId)
    {
        var principal = await session.LoadAsync<Principal>(principalId);
        return new ViewRef
        {
            Id = principalId,
            Label = principal?.DisplayName,
            PrincipalType = principal?.Type,
        };
    }
}
