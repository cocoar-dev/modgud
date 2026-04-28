using BuildingBlocks.Helper;
using Marten;
using TimeToDo.Application.DTOs;
using TimeToDo.Application.DTOs.Comment;
using TimeToDo.Application.DTOs.Todo;
using TimeToDo.Domain.Entities;
using TimeToDo.Domain.ValueObjects;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Users;

namespace TimeToDo.Infrastructure.Persistence.Marten.Mappers;

/// <summary>
/// Maps between Todo domain entity and TodoDocument persistence model.
/// </summary>
public static class TodoDocumentMapper
{
    public static Todo ToDomainEntity(this TodoDocument document)
    {
        return Todo.Reconstitute(
            id: document.Id,
            title: document.Title,
            description: document.Description,
            dueDate: document.DueDate,
            status: Enum.Parse<TodoStatus>(document.Status, ignoreCase: true),
            customerId: document.CustomerId,
            responsibleUserIds: document.ResponsibleUserIds,
            parentTodoId: document.ParentTodoId,
            childTodoIds: document.ChildTodoIds,
            isArchived: document.IsArchived,
            isCritical: document.IsCritical,
            isAwaitingFeedback: document.IsAwaitingFeedback,
            commentsCount: document.CommentsCount,
            createdAt: document.CreatedAt,
            createdById: document.CreatedById,
            updatedAt: document.UpdatedAt,
            updatedById: document.UpdatedById,
            aggregateVersion: document.AggregateVersion
        );
    }

    public static TodoDocument ToDocument(this Todo entity)
    {
        return new TodoDocument
        {
            Id = entity.Id,
            Title = entity.Title,
            Description = entity.Description,
            DueDate = entity.DueDate,
            Status = entity.Status.ToString(),
            CustomerId = entity.CustomerId,
            ResponsibleUserIds = entity.ResponsibleUserIds.ToList(),
            ParentTodoId = entity.ParentTodoId,
            ChildTodoIds = entity.ChildTodoIds.ToList(),
            IsArchived = entity.IsArchived,
            IsCritical = entity.IsCritical,
            IsAwaitingFeedback = entity.IsAwaitingFeedback,
            CommentsCount = entity.CommentsCount,
            CreatedAt = entity.CreatedAt,
            CreatedById = entity.CreatedById,
            UpdatedAt = entity.UpdatedAt,
            UpdatedById = entity.UpdatedById,
            AggregateVersion = entity.AggregateVersion
        };
    }

    public static void UpdateFromEntity(this TodoDocument document, Todo entity)
    {
        document.Title = entity.Title;
        document.Description = entity.Description;
        document.DueDate = entity.DueDate;
        document.Status = entity.Status.ToString();
        document.CustomerId = entity.CustomerId;
        document.ResponsibleUserIds = entity.ResponsibleUserIds.ToList();
        document.ParentTodoId = entity.ParentTodoId;
        document.ChildTodoIds = entity.ChildTodoIds.ToList();
        document.IsArchived = entity.IsArchived;
        document.IsCritical = entity.IsCritical;
        document.IsAwaitingFeedback = entity.IsAwaitingFeedback;
        document.CommentsCount = entity.CommentsCount;
        document.UpdatedAt = entity.UpdatedAt;
        document.UpdatedById = entity.UpdatedById;
        document.AggregateVersion = entity.AggregateVersion;
    }

    // Document → DTO mappings for API handlers
    public static TodoDto ToDto(this TodoDocument document)
    {
        return new TodoDto
        {
            Id = new ShortGuid(document.Id).ToString(),
            Title = document.Title,
            Description = document.Description,
            DueDate = document.DueDate,
            Status = Enum.Parse<TodoStatus>(document.Status, ignoreCase: true),
            Customer = document.CustomerId.HasValue
                ? new RefPropertyDto { Id = new ShortGuid(document.CustomerId.Value).ToString() }
                : null,
            Responsibles = document.ResponsibleUserIds?
                .Select(id => new RefPropertyDto { Id = new ShortGuid(id).ToString() })
                .ToList() ?? new(),
            ParentTodoId = document.ParentTodoId.HasValue
                ? new ShortGuid(document.ParentTodoId.Value).ToString()
                : null,
            Critical = document.IsCritical,
            AwaitingFeedback = document.IsAwaitingFeedback,
            CreatedBy = document.CreatedById != Guid.Empty
                ? new RefPropertyDto { Id = new ShortGuid(document.CreatedById).ToString() }
                : null,
            UpdatedBy = document.UpdatedById.HasValue
                ? new RefPropertyDto { Id = new ShortGuid(document.UpdatedById.Value).ToString() }
                : null,
            CreatedAt = document.CreatedAt,
            UpdatedAt = document.UpdatedAt,
            LastTouchedAt = document.UpdatedAt ?? document.CreatedAt,
            Comments = new(),
            UnreadComments = 0,
            CommentsCount = 0,
            ChildTodosCount = document.ChildTodoIds.Count,
            ChildTodosUnreadCommentsCount = 0,
            IsArchived = document.IsArchived,
            AggregateVersion = document.AggregateVersion
        };
    }

    public static async Task<TodoDto> ToDtoEnrichedAsync(
        this TodoDocument document,
        IDocumentSession session,
        Guid? currentUserId = null,
        List<CommentDocument>? comments = null,
        CancellationToken ct = default)
    {
        var dto = document.ToDto();

        // Enrich with user labels
        var userIds = new List<Guid>();
        userIds.AddRange(document.ResponsibleUserIds);
        if (document.CreatedById != Guid.Empty) userIds.Add(document.CreatedById);
        if (document.UpdatedById.HasValue) userIds.Add(document.UpdatedById.Value);

        var users = await session.Query<UserView>()
            .Where(u => u.Id.In(userIds.Distinct().ToList()))
            .ToListAsync(ct);

        var userDict = users.ToDictionary(u => u.Id, u => u.GetDisplayLabel());

        // Enrich Responsibles
        foreach (var responsible in dto.Responsibles ?? new())
        {
            var guid = ShortGuid.Decode(responsible.Id);
            if (userDict.TryGetValue(guid, out var label))
                responsible.Label = label;
        }

        // Enrich CreatedBy
        if (dto.CreatedBy != null && document.CreatedById != Guid.Empty)
        {
            if (userDict.TryGetValue(document.CreatedById, out var label))
                dto.CreatedBy.Label = label;
        }

        // Enrich UpdatedBy
        if (dto.UpdatedBy != null && document.UpdatedById.HasValue)
        {
            if (userDict.TryGetValue(document.UpdatedById.Value, out var label))
                dto.UpdatedBy.Label = label;
        }

        // Enrich Customer
        if (document.CustomerId.HasValue)
        {
            if (dto.Customer == null)
                dto.Customer = new RefPropertyDto { Id = new ShortGuid(document.CustomerId.Value).ToString() };

            var customer = await session.LoadAsync<CustomerView>(document.CustomerId.Value, ct);
            if (customer != null)
                dto.Customer.Label = customer.Name;
        }

        // Calculate ChildTodosCount and ChildTodosUnreadCommentsCount
        if (document.ChildTodoIds.Any())
        {
            var childTodos = await session.Query<TodoDocument>()
                .Where(t => t.Id.In(document.ChildTodoIds) && t.IsArchived == document.IsArchived)
                .ToListAsync(ct);

            dto.ChildTodosCount = childTodos.Count;

            if (currentUserId.HasValue)
            {
                var childTodoIds = childTodos.Select(t => t.Id).ToList();
                var childComments = await session.Query<CommentDocument>()
                    .Where(c => c.ReferencedItemId.In(childTodoIds) && c.ReferencedItemType == "todo")
                    .ToListAsync(ct);

                var childCommentIds = childComments.Select(c => c.Id).ToList();
                var readStatuses = await session.Query<CommentReadStatusDocument>()
                    .Where(s => s.CommentId.In(childCommentIds) && s.UserId == currentUserId.Value)
                    .Select(s => s.CommentId)
                    .ToListAsync(ct);
                var readCommentIds = new HashSet<Guid>(readStatuses);

                dto.ChildTodosUnreadCommentsCount = childComments.Count(c => !readCommentIds.Contains(c.Id));
            }
        }

        // Fetch and enrich Comments if user is provided
        if (currentUserId.HasValue)
        {
            var todoComments = comments ?? await session.Query<CommentDocument>()
                .Where(c => c.ReferencedItemId == document.Id && c.ReferencedItemType == "todo")
                .OrderBy(c => c.CreatedAt)
                .ToListAsync(ct);

            var commentIds = todoComments.Select(c => c.Id).ToList();

            var readStatuses = await session.Query<CommentReadStatusDocument>()
                .Where(crs => crs.CommentId.In(commentIds) && crs.UserId == currentUserId.Value)
                .ToListAsync(ct);

            var readCommentIds = new HashSet<Guid>(readStatuses.Select(rs => rs.CommentId));

            var commentUserIds = todoComments.Select(c => c.CreatedById).Distinct().ToList();
            var commentUsers = await session.Query<UserView>()
                .Where(u => u.Id.In(commentUserIds))
                .ToListAsync(ct);
            var commentUserDict = commentUsers.ToDictionary(u => u.Id);

            dto.Comments = todoComments.Select(c => new CommentListDto
            {
                Id = new ShortGuid(c.Id).ToString(),
                Description = c.Description,
                CreatedAt = c.CreatedAt,
                CreatedBy = commentUserDict.TryGetValue(c.CreatedById, out var user)
                    ? new RefPropertyDto
                    {
                        Id = new ShortGuid(c.CreatedById).ToString(),
                        Label = user.GetDisplayLabel()
                    }
                    : new RefPropertyDto { Id = new ShortGuid(c.CreatedById).ToString() },
                IHaveRead = readCommentIds.Contains(c.Id),
                UpdatedAt = c.UpdatedAt
            }).ToList();

            dto.CommentsCount = todoComments.Count;
            dto.UnreadComments = todoComments.Count - readCommentIds.Count;
        }

        return dto;
    }

    public static TodoDocument ToDocument(this TodoCreateDto dto)
    {
        return new TodoDocument
        {
            Id = Guid.NewGuid(),
            Title = dto.Title,
            Description = dto.Description,
            DueDate = dto.DueDate,
            Status = dto.Status.ToString(),
            CustomerId = dto.Customer?.Id != null ? ShortGuid.Decode(dto.Customer.Id) : null,
            ResponsibleUserIds = dto.Responsibles?
                .Select(r => ShortGuid.Decode(r.Id))
                .ToList() ?? new(),
            ParentTodoId = null,
            ChildTodoIds = new(),
            IsArchived = false,
            IsCritical = dto.Critical,
            IsAwaitingFeedback = dto.AwaitingFeedback,
            CreatedAt = DateTime.UtcNow,
            CreatedById = Guid.Empty,
            UpdatedAt = null,
            UpdatedById = null,
            AggregateVersion = 1
        };
    }

    public static void MapToDocument(this TodoDto dto, TodoDocument document)
    {
        document.Title = dto.Title;
        document.Description = dto.Description;
        document.DueDate = dto.DueDate;
        document.Status = dto.Status.ToString();
        document.CustomerId = dto.Customer?.Id != null ? ShortGuid.Decode(dto.Customer.Id) : null;
        document.ResponsibleUserIds = dto.Responsibles?
            .Select(r => ShortGuid.Decode(r.Id))
            .ToList() ?? new();
        document.ParentTodoId = dto.ParentTodoId != null ? ShortGuid.Decode(dto.ParentTodoId) : null;
        document.IsCritical = dto.Critical;
        document.IsAwaitingFeedback = dto.AwaitingFeedback;
    }
}
