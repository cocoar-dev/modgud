using TimeToDo.Authorization.Principals;
using Marten;
using Marten.Patching;
using TimeToDo.Api.Features.Shared;
using TimeToDo.Domain.Users.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;


namespace TimeToDo.Api.Features.Comments;

public class CommentViewUserLabelSyncHandler(ILogger<CommentViewUserLabelSyncHandler> logger)
    : ReferenceSyncHandler<UserUpdatedEvent>(logger)
{
    protected override bool ShouldSync(UserUpdatedEvent @event)
        => @event.Firstname.HasValue || @event.Lastname.HasValue || @event.Acronym.HasValue;

    protected override async Task SyncAsync(UserUpdatedEvent @event, IDocumentSession session)
    {
        var user = await session.LoadAsync<Principal>(@event.Id);
        if (user is null) return;

        var newLabel = user.DisplayName;
        Logger.LogInformation("[CommentView:UserLabelSync] Label='{NewLabel}' for user {UserId}", newLabel, @event.Id);

        session.Patch<CommentView>(c => c.CreatedBy != null && c.CreatedBy.Id == @event.Id && !c.IsDeleted)
            .Set(c => c.CreatedBy!.Label, newLabel);
    }
}
