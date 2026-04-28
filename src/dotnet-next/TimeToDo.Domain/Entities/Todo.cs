using ErrorOr;
using TimeToDo.Domain.Errors;
using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Domain.Entities;

/// <summary>
/// Rich domain entity for Todo with encapsulated business rules.
/// Modifications are performed through methods that enforce invariants.
/// </summary>
public class Todo
{
    private readonly List<Guid> _childTodoIds = new();
    private readonly List<Guid> _responsibleUserIds = new();

    public Guid Id { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public DateTime? DueDate { get; private set; }
    public TodoStatus Status { get; private set; }

    public Guid? CustomerId { get; private set; }
    public IReadOnlyList<Guid> ResponsibleUserIds => _responsibleUserIds.AsReadOnly();

    public Guid? ParentTodoId { get; private set; }
    public IReadOnlyList<Guid> ChildTodoIds => _childTodoIds.AsReadOnly();

    public bool IsArchived { get; private set; }
    public bool IsCritical { get; private set; }
    public bool IsAwaitingFeedback { get; private set; }

    public int CommentsCount { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public Guid CreatedById { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public Guid? UpdatedById { get; private set; }

    public AggregateVersion AggregateVersion { get; private set; }

    private Todo() { }

    /// <summary>
    /// Factory method to create a new Todo with validation.
    /// </summary>
    public static ErrorOr<Todo> Create(
        string title,
        TodoStatus status,
        Guid createdById,
        string? description = null,
        DateTime? dueDate = null,
        Guid? customerId = null,
        IEnumerable<Guid>? responsibleUserIds = null,
        bool isCritical = false,
        bool isAwaitingFeedback = false,
        Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return DomainErrors.Todo.TitleRequired;

        var todo = new Todo
        {
            Id = id ?? Guid.NewGuid(),
            Title = title,
            Description = description,
            DueDate = dueDate,
            Status = status,
            CustomerId = customerId,
            IsCritical = isCritical,
            IsAwaitingFeedback = isAwaitingFeedback,
            CreatedAt = DateTime.UtcNow,
            CreatedById = createdById,
            AggregateVersion = AggregateVersion.Initial
        };

        if (responsibleUserIds != null)
        {
            todo._responsibleUserIds.AddRange(responsibleUserIds);
        }

        return todo;
    }

    /// <summary>
    /// Reconstitutes a Todo from persistence data.
    /// Used by repositories to create domain entities from documents.
    /// </summary>
    public static Todo Reconstitute(
        Guid id,
        string title,
        string? description,
        DateTime? dueDate,
        TodoStatus status,
        Guid? customerId,
        IEnumerable<Guid> responsibleUserIds,
        Guid? parentTodoId,
        IEnumerable<Guid> childTodoIds,
        bool isArchived,
        bool isCritical,
        bool isAwaitingFeedback,
        int commentsCount,
        DateTime createdAt,
        Guid createdById,
        DateTime? updatedAt,
        Guid? updatedById,
        int aggregateVersion)
    {
        var todo = new Todo
        {
            Id = id,
            Title = title,
            Description = description,
            DueDate = dueDate,
            Status = status,
            CustomerId = customerId,
            ParentTodoId = parentTodoId,
            IsArchived = isArchived,
            IsCritical = isCritical,
            IsAwaitingFeedback = isAwaitingFeedback,
            CommentsCount = commentsCount,
            CreatedAt = createdAt,
            CreatedById = createdById,
            UpdatedAt = updatedAt,
            UpdatedById = updatedById,
            AggregateVersion = new AggregateVersion(aggregateVersion)
        };

        todo._responsibleUserIds.AddRange(responsibleUserIds);
        todo._childTodoIds.AddRange(childTodoIds);

        return todo;
    }

    /// <summary>
    /// Updates the Todo's basic properties.
    /// </summary>
    public ErrorOr<Success> Update(
        string title,
        string? description,
        DateTime? dueDate,
        TodoStatus status,
        Guid? customerId,
        IEnumerable<Guid> responsibleUserIds,
        bool isCritical,
        bool isAwaitingFeedback,
        Guid updatedById)
    {
        if (string.IsNullOrWhiteSpace(title))
            return DomainErrors.Todo.TitleRequired;

        Title = title;
        Description = description;
        DueDate = dueDate;
        Status = status;

        // Only allow customer change if not a subtodo
        if (!ParentTodoId.HasValue)
        {
            CustomerId = customerId;
        }

        _responsibleUserIds.Clear();
        _responsibleUserIds.AddRange(responsibleUserIds);

        IsCritical = isCritical;
        IsAwaitingFeedback = isAwaitingFeedback;

        UpdatedAt = DateTime.UtcNow;
        UpdatedById = updatedById;
        AggregateVersion = AggregateVersion.Increment();

        return Result.Success;
    }

    /// <summary>
    /// Updates only the status.
    /// </summary>
    public void UpdateStatus(TodoStatus status, Guid updatedById)
    {
        Status = status;
        UpdatedAt = DateTime.UtcNow;
        UpdatedById = updatedById;
        AggregateVersion = AggregateVersion.Increment();
    }

    /// <summary>
    /// Updates the flags (critical and awaiting feedback).
    /// </summary>
    public void UpdateFlags(bool isCritical, bool isAwaitingFeedback, Guid updatedById)
    {
        IsCritical = isCritical;
        IsAwaitingFeedback = isAwaitingFeedback;
        UpdatedAt = DateTime.UtcNow;
        UpdatedById = updatedById;
        AggregateVersion = AggregateVersion.Increment();
    }

    /// <summary>
    /// Checks if this todo can become a subtodo (must not have children).
    /// </summary>
    public ErrorOr<Success> CanBecomeSubtodo()
    {
        if (_childTodoIds.Any())
            return DomainErrors.Todo.HasChildren;

        return Result.Success;
    }

    /// <summary>
    /// Checks if this todo can accept a child (must not be a subtodo itself).
    /// </summary>
    public ErrorOr<Success> CanAcceptChild()
    {
        if (ParentTodoId.HasValue)
            return DomainErrors.Todo.IsAlreadySubtodo;

        return Result.Success;
    }

    /// <summary>
    /// Sets the parent of this todo, inheriting the parent's customer.
    /// </summary>
    public void SetParent(Guid parentId, Guid? inheritedCustomerId)
    {
        ParentTodoId = parentId;
        CustomerId = inheritedCustomerId;
        UpdatedAt = DateTime.UtcNow;
        AggregateVersion = AggregateVersion.Increment();
    }

    /// <summary>
    /// Clears the parent, making this a root todo.
    /// </summary>
    public void ClearParent()
    {
        ParentTodoId = null;
        UpdatedAt = DateTime.UtcNow;
        AggregateVersion = AggregateVersion.Increment();
    }

    /// <summary>
    /// Adds a child todo ID to this todo's children list.
    /// </summary>
    public void AddChild(Guid childId)
    {
        if (!_childTodoIds.Contains(childId))
        {
            _childTodoIds.Add(childId);
            AggregateVersion = AggregateVersion.Increment();
        }
    }

    /// <summary>
    /// Removes a child todo ID from this todo's children list.
    /// </summary>
    public void RemoveChild(Guid childId)
    {
        if (_childTodoIds.Remove(childId))
        {
            AggregateVersion = AggregateVersion.Increment();
        }
    }

    /// <summary>
    /// Archives this todo.
    /// </summary>
    public void Archive(Guid updatedById)
    {
        IsArchived = true;
        UpdatedAt = DateTime.UtcNow;
        UpdatedById = updatedById;
        AggregateVersion = AggregateVersion.Increment();
    }

    /// <summary>
    /// Unarchives this todo.
    /// </summary>
    public void Unarchive(Guid updatedById)
    {
        IsArchived = false;
        UpdatedAt = DateTime.UtcNow;
        UpdatedById = updatedById;
        AggregateVersion = AggregateVersion.Increment();
    }

    /// <summary>
    /// Adds a responsible user to this todo.
    /// </summary>
    public void AddResponsibleUser(Guid userId, Guid updatedById)
    {
        if (!_responsibleUserIds.Contains(userId))
        {
            _responsibleUserIds.Add(userId);
            UpdatedAt = DateTime.UtcNow;
            UpdatedById = updatedById;
            AggregateVersion = AggregateVersion.Increment();
        }
    }

    /// <summary>
    /// Removes a responsible user from this todo.
    /// </summary>
    public void RemoveResponsibleUser(Guid userId, Guid updatedById)
    {
        if (_responsibleUserIds.Remove(userId))
        {
            UpdatedAt = DateTime.UtcNow;
            UpdatedById = updatedById;
            AggregateVersion = AggregateVersion.Increment();
        }
    }

    /// <summary>
    /// Updates the comments count (denormalized for performance).
    /// </summary>
    public void UpdateCommentsCount(int count)
    {
        CommentsCount = count;
    }

    /// <summary>
    /// Increments the comments count.
    /// </summary>
    public void IncrementCommentsCount()
    {
        CommentsCount++;
    }

    /// <summary>
    /// Decrements the comments count.
    /// </summary>
    public void DecrementCommentsCount()
    {
        if (CommentsCount > 0)
            CommentsCount--;
    }
}
