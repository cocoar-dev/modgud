using System.Linq.Expressions;
using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Marten;
using TimeToDo.Domain.Entities;
using TimeToDo.Domain.Repositories;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;

namespace TimeToDo.Infrastructure.Persistence.Marten.Repositories;

/// <summary>
/// Marten implementation of ITodoRepository.
/// </summary>
public class MartenTodoRepository : ITodoRepository
{
    private readonly IDocumentSession _session;
    private readonly DataEventDispatcher _eventDispatcher;
    private const string EventSubject = "Todo";

    public MartenTodoRepository(IDocumentSession session, DataEventDispatcher eventDispatcher)
    {
        _session = session;
        _eventDispatcher = eventDispatcher;
    }

    public async Task<Todo?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var document = await _session.LoadAsync<TodoDocument>(id, ct);
        return document?.ToDomainEntity();
    }

    public async Task<IReadOnlyList<Todo>> GetAllAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var query = _session.Query<TodoDocument>();

        var documents = includeArchived
            ? await query.ToListAsync(ct)
            : await query.Where(t => !t.IsArchived).ToListAsync(ct);

        return documents.Select(d => d.ToDomainEntity()).ToList();
    }

    public async Task<IReadOnlyList<Todo>> GetFilteredAsync(
        Expression<Func<Todo, bool>> predicate,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        // For now, get all and filter in memory (complex expression translation)
        // In a real scenario, you'd translate the expression to work with TodoDocument
        var all = await GetAllAsync(includeArchived, ct);
        return all.Where(predicate.Compile()).ToList();
    }

    public async Task<IReadOnlyList<Todo>> GetArchivedAsync(CancellationToken ct = default)
    {
        var documents = await _session.Query<TodoDocument>()
            .Where(t => t.IsArchived)
            .ToListAsync(ct);

        return documents.Select(d => d.ToDomainEntity()).ToList();
    }

    public async Task<IReadOnlyList<Todo>> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default)
    {
        var documents = await _session.Query<TodoDocument>()
            .Where(t => t.CustomerId == customerId && !t.IsArchived)
            .ToListAsync(ct);

        return documents.Select(d => d.ToDomainEntity()).ToList();
    }

    public async Task<IReadOnlyList<Todo>> GetByResponsibleUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        var documents = await _session.Query<TodoDocument>()
            .Where(t => t.ResponsibleUserIds.Contains(userId) && !t.IsArchived)
            .ToListAsync(ct);

        return documents.Select(d => d.ToDomainEntity()).ToList();
    }

    public async Task<IReadOnlyList<Todo>> GetChildTodosAsync(Guid parentTodoId, CancellationToken ct = default)
    {
        var documents = await _session.Query<TodoDocument>()
            .Where(t => t.ParentTodoId == parentTodoId)
            .ToListAsync(ct);

        return documents.Select(d => d.ToDomainEntity()).ToList();
    }

    public async Task<Todo> CreateAsync(Todo todo, CancellationToken ct = default)
    {
        var document = todo.ToDocument();
        _session.Store(document);

        // If this todo has a parent, sync the bidirectional relationship
        if (todo.ParentTodoId.HasValue)
        {
            var parentDoc = await _session.LoadAsync<TodoDocument>(todo.ParentTodoId.Value, ct);
            if (parentDoc != null && !parentDoc.ChildTodoIds.Contains(todo.Id))
            {
                parentDoc.ChildTodoIds.Add(todo.Id);
                _session.Update(parentDoc);
            }
        }

        await _session.SaveChangesAsync(ct);

        _eventDispatcher.DispatchCreatedEvent(EventSubject, document);

        // Dispatch parent update event if needed
        if (todo.ParentTodoId.HasValue)
        {
            var parentDoc = await _session.LoadAsync<TodoDocument>(todo.ParentTodoId.Value, ct);
            if (parentDoc != null)
            {
                _eventDispatcher.DispatchUpdatedEvent(EventSubject, parentDoc);
            }
        }

        return todo;
    }

    public async Task<Todo> UpdateAsync(Todo todo, CancellationToken ct = default)
    {
        var document = await _session.LoadAsync<TodoDocument>(todo.Id, ct);
        if (document == null)
            throw new InvalidOperationException($"Todo with ID {todo.Id} not found");

        document.UpdateFromEntity(todo);
        _session.Update(document);
        await _session.SaveChangesAsync(ct);

        _eventDispatcher.DispatchUpdatedEvent(EventSubject, document);

        return todo;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await DeleteAsync(new[] { id }, ct);
    }

    public async Task DeleteAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        var orphanedChildren = new List<TodoDocument>();

        foreach (var id in idList)
        {
            var todo = await _session.LoadAsync<TodoDocument>(id, ct);
            if (todo == null) continue;

            // Orphan children
            if (todo.ChildTodoIds.Any())
            {
                foreach (var childId in todo.ChildTodoIds)
                {
                    if (idList.Contains(childId)) continue;

                    var child = await _session.LoadAsync<TodoDocument>(childId, ct);
                    if (child != null && child.ParentTodoId == id)
                    {
                        child.ParentTodoId = null;
                        child.AggregateVersion++;
                        child.UpdatedAt = DateTime.UtcNow;
                        _session.Update(child);
                        orphanedChildren.Add(child);
                    }
                }
            }

            // Remove from parent if has one
            if (todo.ParentTodoId.HasValue)
            {
                var parent = await _session.LoadAsync<TodoDocument>(todo.ParentTodoId.Value, ct);
                if (parent != null && parent.ChildTodoIds.Contains(id))
                {
                    parent.ChildTodoIds.Remove(id);
                    _session.Update(parent);
                    _eventDispatcher.DispatchUpdatedEvent(EventSubject, parent);
                }
            }
        }

        _session.DeleteWhere<TodoDocument>(t => t.Id.In(idList));
        await _session.SaveChangesAsync(ct);

        foreach (var child in orphanedChildren)
        {
            _eventDispatcher.DispatchUpdatedEvent(EventSubject, child);
        }

        var shortIds = idList.Select(id => new ShortGuid(id).ToString());
        _eventDispatcher.DispatchEvent(DataEvent.Deleted(EventSubject, shortIds));
    }

    public async Task<Todo> ArchiveAsync(Guid id, Guid updatedById, CancellationToken ct = default)
    {
        var document = await _session.LoadAsync<TodoDocument>(id, ct);
        if (document == null)
            throw new InvalidOperationException($"Todo with ID {id} not found");

        document.IsArchived = true;
        document.UpdatedAt = DateTime.UtcNow;
        document.UpdatedById = updatedById;
        document.AggregateVersion++;

        _session.Update(document);
        await _session.SaveChangesAsync(ct);

        _eventDispatcher.DispatchDeletedEvent(EventSubject, new ShortGuid(document.Id).ToString());

        return document.ToDomainEntity();
    }

    public async Task<Todo> UnarchiveAsync(Guid id, Guid updatedById, CancellationToken ct = default)
    {
        var document = await _session.LoadAsync<TodoDocument>(id, ct);
        if (document == null)
            throw new InvalidOperationException($"Todo with ID {id} not found");

        document.IsArchived = false;
        document.UpdatedAt = DateTime.UtcNow;
        document.UpdatedById = updatedById;
        document.AggregateVersion++;

        _session.Update(document);
        await _session.SaveChangesAsync(ct);

        _eventDispatcher.DispatchCreatedEvent(EventSubject, document);

        return document.ToDomainEntity();
    }

    public async Task ConvertToParentTodoAsync(IEnumerable<Guid> todoIds, CancellationToken ct = default)
    {
        foreach (var todoId in todoIds)
        {
            var todo = await _session.LoadAsync<TodoDocument>(todoId, ct);
            if (todo == null) continue;

            if (todo.ParentTodoId.HasValue)
            {
                var parent = await _session.LoadAsync<TodoDocument>(todo.ParentTodoId.Value, ct);
                if (parent != null)
                {
                    parent.ChildTodoIds.Remove(todoId);
                    _session.Update(parent);
                    _eventDispatcher.DispatchUpdatedEvent(EventSubject, parent);
                }

                todo.ParentTodoId = null;
                _session.Update(todo);
                _eventDispatcher.DispatchUpdatedEvent(EventSubject, todo);
            }
        }

        await _session.SaveChangesAsync(ct);
    }

    public async Task ConvertToSubTodoAsync(Guid parentTodoId, IEnumerable<Guid> todoIds, CancellationToken ct = default)
    {
        var parent = await _session.LoadAsync<TodoDocument>(parentTodoId, ct);
        if (parent == null)
            throw new InvalidOperationException($"Parent todo with ID {parentTodoId} not found");

        foreach (var todoId in todoIds)
        {
            var todo = await _session.LoadAsync<TodoDocument>(todoId, ct);
            if (todo == null) continue;

            // Remove from old parent if exists
            if (todo.ParentTodoId.HasValue && todo.ParentTodoId.Value != parentTodoId)
            {
                var oldParent = await _session.LoadAsync<TodoDocument>(todo.ParentTodoId.Value, ct);
                if (oldParent != null)
                {
                    oldParent.ChildTodoIds.Remove(todoId);
                    _session.Update(oldParent);
                    _eventDispatcher.DispatchUpdatedEvent(EventSubject, oldParent);
                }
            }

            // Set new parent
            todo.ParentTodoId = parentTodoId;
            todo.CustomerId = parent.CustomerId;
            _session.Update(todo);

            // Add to parent's children
            if (!parent.ChildTodoIds.Contains(todoId))
            {
                parent.ChildTodoIds.Add(todoId);
            }

            _eventDispatcher.DispatchUpdatedEvent(EventSubject, todo);
        }

        _session.Update(parent);
        await _session.SaveChangesAsync(ct);

        _eventDispatcher.DispatchUpdatedEvent(EventSubject, parent);
    }
}
