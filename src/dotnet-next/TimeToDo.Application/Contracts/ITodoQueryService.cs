using TimeToDo.Application.DTOs.Todo;

namespace TimeToDo.Application.Contracts;

/// <summary>
/// Query service for retrieving enriched Todo DTOs.
/// Separates read-side concerns from domain entity operations.
/// </summary>
public interface ITodoQueryService
{
    /// <summary>
    /// Gets all non-archived todos with enriched data.
    /// </summary>
    Task<IReadOnlyList<TodoDto>> GetAllAsync(
        Guid? currentUserId = null,
        string? filterId = null,
        string? orderBy = null,
        int? skip = null,
        int? take = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all archived todos with enriched data.
    /// </summary>
    Task<IReadOnlyList<TodoDto>> GetArchivedAsync(
        Guid? currentUserId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a single todo by ID with enriched data.
    /// </summary>
    Task<TodoDto?> GetByIdAsync(
        Guid id,
        Guid? currentUserId = null,
        CancellationToken ct = default);
}
