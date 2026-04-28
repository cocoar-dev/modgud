using ErrorOr;
using Marten;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;
using TimeToDo.Application.DTOs.Comment;

namespace TimeToDo.Api.Features.Comments.Queries;

public record GetCommentByIdQuery(Guid CommentId, Guid? UserId);

public class GetCommentByIdHandler(IDocumentSession session)
{
    public async Task<ErrorOr<CommentListDto>> Handle(
        GetCommentByIdQuery query,
        CancellationToken ct)
    {
        var comment = await session.LoadAsync<CommentView>(query.CommentId, ct);
        if (comment is null || comment.IsDeleted)
            return Error.NotFound("Comment.NotFound", "Comment not found");

        return await comment.ToListDtoEnrichedAsync(session, query.UserId, ct);
    }
}
