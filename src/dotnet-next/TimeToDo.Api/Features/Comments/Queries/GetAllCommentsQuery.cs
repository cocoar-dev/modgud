using ErrorOr;
using Marten;
using BuildingBlocks.Helper;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;
using TimeToDo.Application.DTOs.Comment;

namespace TimeToDo.Api.Features.Comments.Queries;

public record GetAllCommentsQuery(
    Guid? UserId,
    string? FilterId = null,
    string? OrderBy = null,
    int? Skip = null,
    int? Take = null);

public class GetAllCommentsHandler(IDocumentSession session)
{
    public async Task<ErrorOr<List<CommentListDto>>> Handle(
        GetAllCommentsQuery query,
        CancellationToken ct)
    {
        var comments = await session.Query<CommentView>()
            .Where(c => !c.IsDeleted)
            .ToListAsync(ct);

        IEnumerable<CommentView> queryable = comments;

        // Filter by specific ID if provided
        if (!string.IsNullOrEmpty(query.FilterId))
        {
            var guid = ShortGuid.Decode(query.FilterId);
            queryable = queryable.Where(c => c.Id == guid);
        }

        // Order by specified field (default: CreatedAt)
        queryable = (query.OrderBy?.ToLower()) switch
        {
            "createdat" => queryable.OrderBy(c => c.CreatedAt),
            "updatedat" => queryable.OrderBy(c => c.UpdatedAt),
            _ => queryable.OrderBy(c => c.CreatedAt)
        };

        if (query.Skip.HasValue)
            queryable = queryable.Skip(query.Skip.Value);
        if (query.Take.HasValue)
            queryable = queryable.Take(query.Take.Value);

        var enrichedComments = new List<CommentListDto>();
        foreach (var comment in queryable)
        {
            enrichedComments.Add(await comment.ToListDtoEnrichedAsync(session, query.UserId, ct));
        }

        return enrichedComments;
    }
}
