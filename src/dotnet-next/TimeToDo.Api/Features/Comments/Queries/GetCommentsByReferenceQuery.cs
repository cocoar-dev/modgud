using ErrorOr;
using Marten;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;
using TimeToDo.Application.DTOs.Comment;

namespace TimeToDo.Api.Features.Comments.Queries;

public record GetCommentsByReferenceQuery(Guid ReferencedItemId, string ReferencedItemType, Guid? UserId);

public class GetCommentsByReferenceHandler(IDocumentSession session)
{
    public async Task<ErrorOr<List<CommentListDto>>> Handle(
        GetCommentsByReferenceQuery query,
        CancellationToken ct)
    {
        var comments = await session.Query<CommentView>()
            .Where(c => c.ReferencedItemId == query.ReferencedItemId
                && c.ReferencedItemType == query.ReferencedItemType
                && !c.IsDeleted)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var enrichedComments = new List<CommentListDto>();
        foreach (var comment in comments)
        {
            enrichedComments.Add(await comment.ToListDtoEnrichedAsync(session, query.UserId, ct));
        }

        return enrichedComments;
    }
}
