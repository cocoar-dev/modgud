using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Marten;
using TimeToDo.Domain.Entities;
using TimeToDo.Domain.Repositories;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;

namespace TimeToDo.Infrastructure.Persistence.Marten.Repositories;

/// <summary>
/// Marten implementation of ICommentRepository.
/// </summary>
public class MartenCommentRepository : ICommentRepository
{
    private readonly IDocumentSession _session;
    private readonly DataEventDispatcher _eventDispatcher;
    private const string EventSubject = "Comment";

    public MartenCommentRepository(IDocumentSession session, DataEventDispatcher eventDispatcher)
    {
        _session = session;
        _eventDispatcher = eventDispatcher;
    }

    public async Task<Comment?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var document = await _session.LoadAsync<CommentDocument>(id, ct);
        return document?.ToDomainEntity();
    }

    public async Task<IReadOnlyList<Comment>> GetByReferenceAsync(Guid referencedItemId, string referencedItemType, CancellationToken ct = default)
    {
        var documents = await _session.Query<CommentDocument>()
            .Where(c => c.ReferencedItemId == referencedItemId && c.ReferencedItemType == referencedItemType)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        return documents.Select(d => d.ToDomainEntity()).ToList();
    }

    public async Task<IReadOnlyList<Comment>> GetAllAsync(CancellationToken ct = default)
    {
        var documents = await _session.Query<CommentDocument>().ToListAsync(ct);
        return documents.Select(d => d.ToDomainEntity()).ToList();
    }

    public async Task<Comment> CreateAsync(Comment comment, CancellationToken ct = default)
    {
        var document = comment.ToDocument();
        _session.Store(document);
        await _session.SaveChangesAsync(ct);

        _eventDispatcher.DispatchCreatedEvent(EventSubject, document);

        return comment;
    }

    public async Task<Comment> UpdateAsync(Comment comment, CancellationToken ct = default)
    {
        var document = await _session.LoadAsync<CommentDocument>(comment.Id, ct);
        if (document == null)
            throw new InvalidOperationException($"Comment with ID {comment.Id} not found");

        document.UpdateFromEntity(comment);
        _session.Update(document);
        await _session.SaveChangesAsync(ct);

        _eventDispatcher.DispatchUpdatedEvent(EventSubject, document);

        return comment;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var document = await _session.LoadAsync<CommentDocument>(id, ct);
        if (document == null) return;

        _session.Delete(document);
        await _session.SaveChangesAsync(ct);

        _eventDispatcher.DispatchDeletedEvent(EventSubject, new ShortGuid(id).ToString());
    }

    public async Task MarkAsReadAsync(Guid commentId, Guid userId, CancellationToken ct = default)
    {
        // Check if already marked as read
        var existing = await _session.Query<CommentReadStatusDocument>()
            .Where(s => s.CommentId == commentId && s.UserId == userId)
            .FirstOrDefaultAsync(ct);

        if (existing != null) return;

        var readStatus = new CommentReadStatusDocument
        {
            Id = Guid.NewGuid(),
            CommentId = commentId,
            UserId = userId,
            ReadAt = DateTime.UtcNow
        };

        _session.Store(readStatus);
        await _session.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetReadByUsersAsync(Guid commentId, CancellationToken ct = default)
    {
        var readStatuses = await _session.Query<CommentReadStatusDocument>()
            .Where(s => s.CommentId == commentId)
            .Select(s => s.UserId)
            .ToListAsync(ct);

        return readStatuses;
    }

    public async Task<bool> HasUserReadAsync(Guid commentId, Guid userId, CancellationToken ct = default)
    {
        var existing = await _session.Query<CommentReadStatusDocument>()
            .Where(s => s.CommentId == commentId && s.UserId == userId)
            .FirstOrDefaultAsync(ct);

        return existing != null;
    }
}
