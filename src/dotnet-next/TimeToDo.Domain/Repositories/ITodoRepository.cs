using System.Linq.Expressions;
using TimeToDo.Domain.Entities;

namespace TimeToDo.Domain.Repositories;

/// <summary>
/// Repository interface for Todo entity.
/// </summary>
public interface ITodoRepository
{
    /// <summary>
    /// Gets a todo by its ID.
    /// </summary>
    Task<Todo?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets all todos, optionally including archived ones.
    /// </summary>
    Task<IReadOnlyList<Todo>> GetAllAsync(bool includeArchived = false, CancellationToken ct = default);

    /// <summary>
    /// Gets todos matching a filter predicate.
    /// </summary>
    Task<IReadOnlyList<Todo>> GetFilteredAsync(
        Expression<Func<Todo, bool>> predicate,
        bool includeArchived = false,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all archived todos.
    /// </summary>
    Task<IReadOnlyList<Todo>> GetArchivedAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all todos for a specific customer.
    /// </summary>
    Task<IReadOnlyList<Todo>> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>
    /// Gets all todos where a specific user is responsible.
    /// </summary>
    Task<IReadOnlyList<Todo>> GetByResponsibleUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Gets all child todos for a parent.
    /// </summary>
    Task<IReadOnlyList<Todo>> GetChildTodosAsync(Guid parentTodoId, CancellationToken ct = default);

    /// <summary>
    /// Saves a new todo.
    /// </summary>
    Task<Todo> CreateAsync(Todo todo, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing todo.
    /// </summary>
    Task<Todo> UpdateAsync(Todo todo, CancellationToken ct = default);

    /// <summary>
    /// Deletes a todo by its ID.
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Deletes multiple todos by their IDs.
    /// </summary>
    Task DeleteAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Archives a todo.
    /// </summary>
    Task<Todo> ArchiveAsync(Guid id, Guid updatedById, CancellationToken ct = default);

    /// <summary>
    /// Unarchives a todo.
    /// </summary>
    Task<Todo> UnarchiveAsync(Guid id, Guid updatedById, CancellationToken ct = default);

    /// <summary>
    /// Converts subtodos to parent todos (removes parent relationship).
    /// </summary>
    Task ConvertToParentTodoAsync(IEnumerable<Guid> todoIds, CancellationToken ct = default);

    /// <summary>
    /// Converts todos to subtodos of the specified parent.
    /// </summary>
    Task ConvertToSubTodoAsync(Guid parentTodoId, IEnumerable<Guid> todoIds, CancellationToken ct = default);
}
