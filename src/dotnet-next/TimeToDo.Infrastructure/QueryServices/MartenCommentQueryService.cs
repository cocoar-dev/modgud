using BuildingBlocks.Helper;
using Marten;
using TimeToDo.Application.Contracts;
using TimeToDo.Application.DTOs;
using TimeToDo.Application.DTOs.Comment;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;

namespace TimeToDo.Infrastructure.QueryServices;

public class MartenCommentQueryService(IDocumentSession session) : ICommentQueryService
{
    public async Task<IReadOnlyList<CommentListDto>> GetAllAsync(CancellationToken ct = default)
    {
        var comments = await session.Query<CommentDocument>()
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var userIds = comments.Select(c => c.CreatedById).Distinct().ToList();
        var users = await session.Query<UserDocument>()
            .Where(u => u.Id.In(userIds))
            .ToListAsync(ct);
        var userDict = users.ToDictionary(u => u.Id);

        return comments.Select(c => ToDto(c, userDict)).ToList();
    }

    public async Task<IReadOnlyList<CommentListDto>> GetByReferenceAsync(
        string referenceType,
        Guid referenceId,
        Guid? currentUserId = null,
        CancellationToken ct = default)
    {
        var comments = await session.Query<CommentDocument>()
            .Where(c => c.ReferencedItemId == referenceId && c.ReferencedItemType == referenceType)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var userIds = comments.Select(c => c.CreatedById).Distinct().ToList();
        var users = await session.Query<UserDocument>()
            .Where(u => u.Id.In(userIds))
            .ToListAsync(ct);
        var userDict = users.ToDictionary(u => u.Id);

        // Get read statuses if user provided
        var readCommentIds = new HashSet<Guid>();
        if (currentUserId.HasValue)
        {
            var commentIds = comments.Select(c => c.Id).ToList();
            var readStatuses = await session.Query<CommentReadStatusDocument>()
                .Where(s => s.CommentId.In(commentIds) && s.UserId == currentUserId.Value)
                .ToListAsync(ct);
            readCommentIds = readStatuses.Select(s => s.CommentId).ToHashSet();
        }

        return comments.Select(c => ToDto(c, userDict, readCommentIds)).ToList();
    }

    public async Task<CommentListDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var comment = await session.LoadAsync<CommentDocument>(id, ct);
        if (comment == null) return null;

        var userDict = new Dictionary<Guid, UserDocument>();
        if (comment.CreatedById != Guid.Empty)
        {
            var user = await session.LoadAsync<UserDocument>(comment.CreatedById, ct);
            if (user != null)
                userDict[user.Id] = user;
        }

        return ToDto(comment, userDict);
    }

    private static CommentListDto ToDto(
        CommentDocument document,
        Dictionary<Guid, UserDocument> userDict,
        HashSet<Guid>? readCommentIds = null)
    {
        RefPropertyDto? createdBy = null;
        if (document.CreatedById != Guid.Empty)
        {
            createdBy = new RefPropertyDto
            {
                Id = new ShortGuid(document.CreatedById).ToString()
            };
            if (userDict.TryGetValue(document.CreatedById, out var user))
            {
                createdBy.Label = $"{user.Acronym} | {user.Firstname ?? ""} {user.Lastname ?? ""}";
            }
        }

        return new CommentListDto
        {
            Id = new ShortGuid(document.Id).ToString(),
            Description = document.Description,
            CreatedAt = document.CreatedAt,
            CreatedBy = createdBy,
            UpdatedAt = document.UpdatedAt,
            IHaveRead = readCommentIds?.Contains(document.Id) ?? false
        };
    }
}
