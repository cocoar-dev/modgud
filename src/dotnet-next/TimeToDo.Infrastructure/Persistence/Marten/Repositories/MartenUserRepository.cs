using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Marten;
using TimeToDo.Domain.Entities;
using TimeToDo.Domain.Repositories;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;

namespace TimeToDo.Infrastructure.Persistence.Marten.Repositories;

/// <summary>
/// Marten implementation of IUserRepository.
/// </summary>
public class MartenUserRepository : IUserRepository
{
    private readonly IDocumentSession _session;
    private readonly DataEventDispatcher _eventDispatcher;
    private const string EventSubject = "User";

    public MartenUserRepository(IDocumentSession session, DataEventDispatcher eventDispatcher)
    {
        _session = session;
        _eventDispatcher = eventDispatcher;
    }

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var document = await _session.LoadAsync<UserDocument>(id, ct);
        return document?.ToDomainEntity();
    }

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default)
    {
        var documents = await _session.Query<UserDocument>().ToListAsync(ct);
        return documents.Select(d => d.ToDomainEntity()).ToList();
    }

    public async Task<User> CreateAsync(User user, CancellationToken ct = default)
    {
        var document = user.ToDocument();
        _session.Store(document);
        await _session.SaveChangesAsync(ct);

        _eventDispatcher.DispatchCreatedEvent(EventSubject, document);

        return user;
    }

    public async Task<User> UpdateAsync(User user, CancellationToken ct = default)
    {
        var document = await _session.LoadAsync<UserDocument>(user.Id, ct);
        if (document == null)
            throw new InvalidOperationException($"User with ID {user.Id} not found");

        document.UpdateFromEntity(user);
        _session.Update(document);
        await _session.SaveChangesAsync(ct);

        _eventDispatcher.DispatchUpdatedEvent(EventSubject, document);

        return user;
    }

    public async Task DeleteAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        _session.DeleteWhere<UserDocument>(u => u.Id.In(idList));
        await _session.SaveChangesAsync(ct);

        var shortIds = idList.Select(id => new ShortGuid(id).ToString());
        _eventDispatcher.DispatchEvent(DataEvent.Deleted(EventSubject, shortIds));
    }
}
